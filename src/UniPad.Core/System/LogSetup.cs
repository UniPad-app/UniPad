using Serilog;
using Serilog.Events;

namespace UniPad.Core.SystemServices;

/// <summary>
/// Central Serilog bootstrap. Logs are the primary diagnostic channel for the weird-controller
/// scenarios this project targets, so they are written eagerly and kept for a week.
/// </summary>
public static class LogSetup
{
    private static bool _initialised;

    /// <summary>Configures <see cref="Log.Logger"/>. Safe to call more than once.</summary>
    /// <param name="verbose">When true, emits Debug level records too.</param>
    public static void Initialise(bool verbose = false)
    {
        if (_initialised)
        {
            return;
        }

        _initialised = true;

        var logFile = Path.Combine(PortablePaths.LogsDirectory, "unipad-.log");

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Is(verbose ? LogEventLevel.Debug : LogEventLevel.Information)
            .Enrich.FromLogContext()
            .WriteTo.File(
                logFile,
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7,
                shared: true,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
            .WriteTo.Console(
                outputTemplate: "{Timestamp:HH:mm:ss} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        Log.Information("=== UniPad starting ===");
        Log.Information("Data root: {Root} (portable={Portable})", PortablePaths.DataRoot, PortablePaths.IsPortableMode);
        Log.Information("Executable: {Exe}", PortablePaths.ExecutablePath);
        Log.Information("OS: {Os}", Environment.OSVersion.VersionString);
        Log.Information("Runtime: {Runtime}", Environment.Version);
    }

    /// <summary>Flushes buffered log events. Call before the process exits.</summary>
    public static void Shutdown()
    {
        Log.Information("=== UniPad stopping ===");
        Log.CloseAndFlush();
    }
}
