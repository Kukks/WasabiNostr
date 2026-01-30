using System.Net.Http.Json;
using System.Net.Mime;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NNostr.Client;
using WasabiNostr.Web.WasabiFinder;

namespace WasabiNostr.Web.State;

public class ApplicationState
{
    readonly SemaphoreSlim _semaphore = new(1);

    public bool Loading
    {
        get => _semaphore.CurrentCount == 0;
        set
        {
            if (!value)
            {
                _semaphore.Release();
                StateHasChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    private async Task StartLoading()
    {
        StateHasChanged?.Invoke(this, EventArgs.Empty);
        await _semaphore.WaitAsync();
    }

    public EventHandler? StateHasChanged { get; set; }
    public bool? WasabiConfigDetected { get; set; }
    public bool WasabiConfigError { get; set; } = false;
    public string? NetworkTypeDetected { get; set; }
    public string? Coordinator { get; set; }
    public List<string> Relays { get; set; } =
    [
        "wss://relay.primal.net"
    ];

    public static int Kind = 15750;
    public static string TypeTagIdentifier = "type";
    public static string TypeTagValue = "wabisabi";
    public static string NetworkTagIdentifier = "network";
    public static string EndpointTagIdentifier = "endpoint";

    public bool Discovering => _tokenSource is not null && !_tokenSource.IsCancellationRequested;
    private CancellationTokenSource? _tokenSource;
    private (NostrEvent result, RoundParameters roundParameters)? _expandedCoordinator;


    public async Task Discover()
    {
        _tokenSource?.Cancel();
        _tokenSource = new CancellationTokenSource();
        var cancellationToken = _tokenSource.Token;
        await StartLoading();
        try
        {
            EventResults = new();
            if (NetworkTypeDetected is null)
                return;
            Client = new CompositeNostrClient(Relays.Select(s => Uri.TryCreate(s, UriKind.Absolute, out var uri) ? uri : null)
                .Where(u => u is not null).ToArray()!);
            
            Client.StateChanged += (sender, args) =>
            {
                StateHasChanged?.Invoke(this, EventArgs.Empty);
                if(Discovering && args.Item2 == WebSocketState.Closed)
                {
                    _ = Client.Connect(_tokenSource.Token);
                }
            };
            await Client.ConnectAndWaitUntilConnected(cancellationToken);
            StateHasChanged?.Invoke(this, EventArgs.Empty);
            _ = Task.Run(async () =>
            {
                await foreach (var evt in Client.SubscribeForEvents(new[]
                               {
                                   new NostrSubscriptionFilter()
                                   {
                                       Kinds = new[] {Kind},
                                   }
                               }, false, cancellationToken))
                {

                    if(evt.GetTaggedData(TypeTagIdentifier)?.Any() is not true || !evt.GetTaggedData(TypeTagIdentifier).Contains(TypeTagValue, StringComparer.InvariantCultureIgnoreCase))
                    {
                        continue;
                    }
                    var network = evt.GetTaggedData(NetworkTagIdentifier);
var options = new []{NetworkTypeDetected.ToLower(), $"{NetworkTypeDetected.ToLower()}net"};
                    if(network?.Any(n => options.Contains(n, StringComparer.InvariantCultureIgnoreCase)) is not true)
                    {
                        continue;
                    }
                    
                    EventResults?.RemoveAll(@event =>
                        @event.PublicKey == evt.PublicKey && @event.CreatedAt < evt.CreatedAt);
                    
                    
                    EventResults?.Add(evt);
                    StateHasChanged?.Invoke(this, EventArgs.Empty);
                    
                }

                Client.Dispose();
            }, cancellationToken);
        }
        finally
        {
            Loading = false;
        }
    }

    public List<NostrEvent>? EventResults { get; set; }

    public CompositeNostrClient? Client { get; set; }

    private string GetCoordinatorConfigKey(string network)
    {
        if (network == "mainnet")
            network = "";
        else if(!network.EndsWith("net", StringComparison.InvariantCultureIgnoreCase))
            network += "Net";
        return $"{network}CoordinatorUri";
    }

    private string? GetCoordinator(JsonObject config, string network)
    {
        if (config.TryGetPropertyValue(GetCoordinatorConfigKey(network), out var coordinator))
        {
            return coordinator?.GetValue<string>();
        }

        return config
            .TryGetPropertyValue(
                $"{network}{(network.Equals("testnet", StringComparison.InvariantCultureIgnoreCase) ? "Clearnet" : "")}BackendUri",
                out coordinator)
            ? coordinator?.GetValue<string>()
            : null;
    }

    public async Task DetectWasabi()
    {
        await StartLoading();
        try
        {
            var path = WasabiHelper.GetConfigPath();
            if (File.Exists(path))
            {
                WasabiConfigDetected = true;
                var config = JsonNode.Parse(await File.ReadAllTextAsync(path)) as JsonObject;
                if (config is null)
                {
                    WasabiConfigError = true;
                }
                else
                {
                    NetworkTypeDetected = config["Network"]?.GetValue<string>();
                    if (NetworkTypeDetected is null)
                    {
                        NetworkTypeDetected = "mainnet";
                    }

                    Coordinator = GetCoordinator(config, NetworkTypeDetected);
                }
            }
            else
            {
                WasabiConfigDetected = false;
                WasabiConfigError = false;
            }
        }
        finally
        {
            Loading = false;
        }
    }

    public async Task SetConfig(string endpoint)
    {
        await StartLoading();

        try
        {
            if (WasabiConfigError)
            {
                return;
            }

            var path = WasabiHelper.GetConfigPath();
            var config = (JsonObject) JsonNode.Parse(await File.ReadAllTextAsync(path))!;
            config[GetCoordinatorConfigKey(NetworkTypeDetected!)] = endpoint;
            await File.WriteAllTextAsync(path, config.ToString());
        }
        finally
        {
            Loading = false;
            await DetectWasabi();
        }
    }

    public async Task<string?> FetchInfo(NostrEvent evt)
    {
        await StartLoading();

        try
        {
            var endpoint = evt.GetTaggedData(EndpointTagIdentifier).First();

            var client = new HttpClient();
            
            endpoint = endpoint.Replace("plugins/wabisabi-coordinator/plugins/wabisabi-coordinator", "plugins/wabisabi-coordinator");
            var res = await client.PostAsync(new Uri(new Uri(endpoint), "wabisabi/status"),
                new StringContent("{\"RoundCheckpoints\": []}", Encoding.UTF8, MediaTypeNames.Application.Json));
            if (!res.IsSuccessStatusCode)
            {
                return "Could not connect";
            }

            var activeRounds = await res.Content.ReadFromJsonAsync<RootObject>();
            var lastRound = activeRounds.roundStates.MaxBy(states => DateTimeOffset.Parse(states.inputRegistrationEnd));
            if (lastRound is null)
            {
                return "No active rounds";
            }

            var roundCreatedEvent =
                lastRound.coinjoinState.events.FirstOrDefault(events => events.Type == "RoundCreated");
            if (roundCreatedEvent is null)
            {
                return "No round created event";
            }

            ExpandedCoordinator = (evt, roundCreatedEvent.roundParameters);
        }
        catch (Exception e)
        {
            return "Error fetching round: "+ e.Message;
            // ignored
        }
        finally
        {
            Loading = false;
        }

        return null;
    }

    public (NostrEvent result, RoundParameters roundParameters)? ExpandedCoordinator
    {
        get => _expandedCoordinator;
        set
        {
            _expandedCoordinator = value;
            
            StateHasChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public bool RelaysExpanded => !Discovering && !Loading;

    public void CancelDiscovery()
    {
        _tokenSource?.Cancel();
    }

    public class RootObject
    {
        public RoundStates[] roundStates { get; set; }
    }

    public class RoundStates
    {
        public CoinjoinState coinjoinState { get; set; }
        public string inputRegistrationEnd { get; set; }
    }


    public class CoinjoinState
    {
        public Events[] events { get; set; }
    }

    public class Events
    {
        public string Type { get; set; }
        public RoundParameters? roundParameters { get; set; }
    }

    public class RoundParameters
    {
        public string network { get; set; }
        public int miningFeeRate { get; set; }
        public CoordinationFeeRate coordinationFeeRate { get; set; }
        public long maxSuggestedAmount { get; set; }
        public int minInputCountByRound { get; set; }
        public int maxInputCountByRound { get; set; }
        public AllowedInputAmounts allowedInputAmounts { get; set; }
        public AllowedOutputAmounts allowedOutputAmounts { get; set; }
        public int[] allowedInputTypes { get; set; }
        public int[] allowedOutputTypes { get; set; }
        public string standardInputRegistrationTimeout { get; set; }
        public string connectionConfirmationTimeout { get; set; }
        public string outputRegistrationTimeout { get; set; }
        public string transactionSigningTimeout { get; set; }
        public string blameInputRegistrationTimeout { get; set; }
        public int minAmountCredentialValue { get; set; }
        public long maxAmountCredentialValue { get; set; }
        public int initialInputVsizeAllocation { get; set; }
        public int maxVsizeCredentialValue { get; set; }
        public int maxVsizeAllocationPerAlice { get; set; }
        public string coordinationIdentifier { get; set; }
        public int maxTransactionSize { get; set; }
        public int minRelayTxFee { get; set; }
    }

    public class CoordinationFeeRate
    {
        public double rate { get; set; }
        public int plebsDontPayThreshold { get; set; }
    }

    public class AllowedInputAmounts
    {
        public long min { get; set; }
        public long max { get; set; }
    }

    public class AllowedOutputAmounts
    {
        public int min { get; set; }
        public long max { get; set; }
    }

    
    public enum ScriptType
    {
        Witness,
        P2PKH,
        P2SH,
        P2PK,
        P2WPKH,
        P2WSH,
        MultiSig,
        Taproot,
    }
}

