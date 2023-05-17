using System.Drawing;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using Photino.Blazor;
using PhotinoNET;
using WasabiNostr.Web.State;

namespace WasabiNostr.Web
{
    public static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            var appBuilder = PhotinoBlazorAppBuilder.CreateDefault(args);
            appBuilder.Services.AddSingleton<ApplicationState>();
            appBuilder.Services.AddMudServices();

            appBuilder.Services
                .AddLogging();

            // register root component and selector
            appBuilder.RootComponents.Add<App>("app");

            var app = appBuilder.Build();

            // customize window.
            app.MainWindow
                .SetResizable(true)
                .SetZoom(0)
                // .SetIconFile("favicon.ico")
                .SetTitle("Wasabi Nostr");

            app.MainWindow.Center();
            Size? size = null;
            Size? lastAcceptedSize = null;
            
            app.MainWindow.WindowSizeChangedHandler += (sender, e) =>
            {
                try
                {
                    PhotinoWindow? window = sender as PhotinoWindow;

                    if (size == null)
                    {
                        size = e;
                    }
                    else
                    {
                        double zoomx = (double)e.Width  / ((Size)size).Width  * 100;
                        double zoomy = (double)e.Height / ((Size)size).Height * 100;
                        double zoom   = Math.Min(zoomx, zoomy);
                        if (zoom < 75 && lastAcceptedSize is not null)
                        {
                            window.SetSize(lastAcceptedSize.Value);
                        }
                        else
                        {
                            lastAcceptedSize = window.Size;
                            window.SetZoom((int) zoom);
                        }
                    }      
                }
                catch (Exception exc)
                {
                    Console.WriteLine(e);
                }
              
            };
            AppDomain.CurrentDomain.UnhandledException += (sender, error) =>
            {
                app.MainWindow.ShowMessage("Fatal exception", error.ExceptionObject.ToString());
            };

            app.Run();

        }
    }
}