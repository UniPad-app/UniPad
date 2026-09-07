
namespace UniPad.Core.SystemServices;

/// <summary>
/// Resolves where UniPad keeps its user data.
/// <para>
/// Priority 1: <c>&lt;exe_dir&gt;\UniPad_Data</c> - true portable mode (USB stick, extracted zip).
/// Priority 2: <c>%APPDATA%\UniPad</c> - used when the exe lives somewhere read-only such as
/// <c>Program Files</c> or a write-protected removable drive.
/// </para>
/// <para>
/// Writability is probed by actually creating and deleting a temp file rather than by
/// inspecting the path string, because ACLs and virtualisation make path heuristics unreliable.
/// </para>
/// </summary>
public static class PortablePaths
{
    private const string PortableFolderName = "UniPad_Data";
    private const string AppDataFolderName = "UniPad";

    private static readonly Lazy<string> LazyRoot = new(ResolveRoot, isThreadSafe: true);

    /// <summary>Root data directory. Guaranteed to exist and be writable.</summary>
    public static string DataRoot => LazyRoot.Value;

    /// <summary>Directory holding named profile JSON documents.</summary>
    public static string ProfilesDirectory => EnsureDirectory(Path.Combine(DataRoot, "profiles"));

    /// <summary>Directory holding rolling Serilog log files.</summary>
    public static string LogsDirectory => EnsureDirectory(Path.Combine(DataRoot, "logs"));

    /// <summary>Path of the global application configuration document.</summary>
    public static string ConfigFile => Path.Combine(DataRoot, "config.json");

    /// <summary>Path of the SDL community controller mapping database.</summary>
    public static string GameControllerDbFile => Path.Combine(DataRoot, "gamecontrollerdb.txt");

    /// <summary>Path of the per-game auto profile switching table.</summary>
    public static string GamesFile => Path.Combine(DataRoot, "games.json");

    /// <summary>True when data lives next to the executable (real portable mode).</summary>
    public static bool IsPortableMode { get; private set; }

    /// <summary>Directory that contains the running executable (or the app base for single-file).</summary>
    public static string ExecutableDirectory
    {
        get
        {
            // PLATFORM: for single-file publishes, Assembly.Location is empty, so fall back to
            // the process path which points at the real .exe on disk rather than the extraction dir.
            string? dir = null;
            try
            {
                var processPath = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(processPath))
                {
                    dir = Path.GetDirectoryName(processPath);
                }
            }
            catch
            {
                // Ignored - fall through to assembly probing.
            }

            // Assembly.Location is deliberately not consulted: in a single-file bundle it always
            // returns an empty string, so AppContext.BaseDirectory is the only correct fallback.
            return string.IsNullOrEmpty(dir) ? AppContext.BaseDirectory : dir!;
        }
    }

    /// <summary>Full path of the running executable, used for HidHide whitelisting and autostart.</summary>
    public static string ExecutablePath
    {
        get
        {
            var processPath = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(processPath))
            {
                return processPath;
            }

            // Environment.ProcessPath is the single-file-safe way to find the host executable.
            return Path.Combine(AppContext.BaseDirectory, "UniPad.exe");
        }
    }

    private static string ResolveRoot()
    {
        var portable = Path.Combine(ExecutableDirectory, PortableFolderName);
        if (TryPrepareWritable(portable))
        {
            IsPortableMode = true;
            return portable;
        }

        var appData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            AppDataFolderName);

        if (TryPrepareWritable(appData))
        {
            IsPortableMode = false;
            return appData;
        }

        // Last resort: the OS temp folder. Better to run with volatile settings than to crash.
        var temp = Path.Combine(Path.GetTempPath(), AppDataFolderName);
        Directory.CreateDirectory(temp);
        IsPortableMode = false;
        return temp;
    }

    /// <summary>
    /// Creates <paramref name="directory"/> if needed and verifies it accepts writes by
    /// round-tripping a throwaway probe file.
    /// </summary>
    private static bool TryPrepareWritable(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var probe = Path.Combine(directory, $".write-probe-{Guid.NewGuid():N}");
            File.WriteAllText(probe, "probe");
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string EnsureDirectory(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
        }
        catch
        {
            // Caller will surface the failure when it actually tries to read or write.
        }

        return path;
    }
}
