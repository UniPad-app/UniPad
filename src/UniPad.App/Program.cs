using Avalonia;
using Serilog;
using UniPad.Core.SystemServices;

namespace UniPad.App;

/// <summary>Process entry point.</summary>
public static class Program
{
    /// <summary>
    /// Application entry point. Avalonia requires this to run before any Avalonia type is touched,
    /// so all initialisation happens inside <see cref="App"/> instead.
    /// </summary>
    [STAThread]
    public static int Main(string[] args)
    {
        // --tray suppresses the initial window, used by the autostart registry entry.
        App.StartHidden = args.Any(a =>
            string.Equals(a, "--tray", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(a, "-t", StringComparison.OrdinalIgnoreCase));

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            Log.Fatal(e.ExceptionObject as Exception, "Unhandled exception in AppDomain");
            Log.CloseAndFlush();
        };

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Error(e.Exception, "Unobserved task exception");
            e.SetObserved();
        };

        try
        {
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            // Logging may not be configured yet, so also write a crash file next to the data root.
            try
            {
                Log.Fatal(ex, "Fatal error during startup");
                Log.CloseAndFlush();

                var crashFile = Path.Combine(PortablePaths.DataRoot, "crash.txt");
                File.WriteAllText(crashFile, ex.ToString());
            }
            catch
            {
                // Nothing further can be done.
            }

            return 1;
        }
    }

    /// <summary>Builds the Avalonia application. Also used by the XAML previewer.</summary>
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
