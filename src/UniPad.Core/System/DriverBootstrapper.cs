using System.Diagnostics;
using System.Reflection;
using Microsoft.Win32;
using Serilog;

namespace UniPad.Core.SystemServices;

/// <summary>State of a required native driver.</summary>
public enum DriverState
{
    /// <summary>The driver is installed and usable.</summary>
    Installed,
    /// <summary>The driver is absent.</summary>
    Missing,
    /// <summary>State could not be determined (non-Windows host, registry access denied).</summary>
    Unknown,
}

/// <summary>Stage an installation has reached, reported while it runs.</summary>
public enum DriverInstallPhase
{
    /// <summary>Fetching the official installer because nothing is bundled.</summary>
    Downloading,

    /// <summary>Running a bundled installer.</summary>
    Installing,

    /// <summary>Running a downloaded installer, which raises the Windows elevation prompt.</summary>
    InstallingElevated,
}

/// <summary>How an installation attempt ended.</summary>
/// <remarks>
/// Reported as a value rather than left implicit in the message text, because this layer has no
/// string table and the interface has to be able to say the same thing in another language.
/// </remarks>
public enum DriverInstallOutcome
{
    /// <summary>The driver is installed and usable now.</summary>
    Installed,

    /// <summary>Installed, but Windows wants a reboot before the driver loads.</summary>
    InstalledRestartRequired,

    /// <summary>The user dismissed the elevation prompt or the installer.</summary>
    Cancelled,

    /// <summary>Driver installation was attempted on a host that is not Windows.</summary>
    NotSupported,

    /// <summary>This build carries no bundled installer for the driver.</summary>
    NotBundled,

    /// <summary>The download finished but is far too small to be a real installer.</summary>
    DownloadIncomplete,

    /// <summary>The installer file could not be found on disk.</summary>
    InstallerMissing,

    /// <summary>The installer was still running after the timeout elapsed.</summary>
    InstallerTimedOut,

    /// <summary>The installer ran and reported a failure.</summary>
    InstallerFailed,

    /// <summary>Something else went wrong; the detail carries the exception text.</summary>
    Failed,
}

/// <summary>Progress notification raised while a driver is being installed.</summary>
/// <param name="Phase">What is happening right now.</param>
/// <param name="DriverName">Driver being installed.</param>
public readonly record struct DriverInstallProgress(DriverInstallPhase Phase, string DriverName);

/// <summary>Result of an installation attempt.</summary>
/// <param name="Succeeded">True when the installer reported success.</param>
/// <param name="RestartRequired">True when the installer asked for a reboot (exit code 3010).</param>
/// <param name="Outcome">Why it ended this way, for the caller to localise.</param>
/// <param name="DriverName">Driver involved.</param>
/// <param name="Detail">
/// Untranslatable extra context - an exception message, a path, an exit code - or empty.
/// </param>
/// <param name="Message">Ready-made English summary, used for logging.</param>
public readonly record struct DriverInstallResult(
    bool Succeeded,
    bool RestartRequired,
    DriverInstallOutcome Outcome,
    string DriverName,
    string Detail,
    string Message);

/// <summary>
/// Detects and, with the user's consent, installs the ViGEmBus and HidHide drivers.
/// <para>
/// UniPad itself never runs elevated. Installation spawns a separate elevated msiexec process, so
/// the main application keeps normal user rights - running an input hook as administrator
/// permanently would be poor practice.
/// </para>
/// </summary>
public static class DriverBootstrapper
{
    /// <summary>Registry key that ViGEmBus writes on install.</summary>
    private const string ViGEmDependencyKey =
        @"Installer\Dependencies\{2CE7CF20-B0B8-46F9-B8AE-1D62F5A1D1F1}";

    /// <summary>Fallback: ViGEmBus service key.</summary>
    private const string ViGEmServiceKey = @"SYSTEM\CurrentControlSet\Services\ViGEmBus";

    /// <summary>Registry key that HidHide writes on install.</summary>
    private const string HidHideDependencyKey =
        @"Installer\Dependencies\NSS.Drivers.HidHide.x64";

    /// <summary>Registry location of the HidHide CLI tool.</summary>
    private const string HidHidePathKey =
        @"SOFTWARE\Nefarius Software Solutions e.U.\Nefarius Software Solutions e.U. HidHide";

    /// <summary>Embedded resource name of the bundled ViGEmBus installer, if present.</summary>
    public const string ViGEmResourceName = "UniPad.App.Resources.ViGEmBus_x64.msi";

    /// <summary>Embedded resource name of the bundled HidHide installer, if present.</summary>
    public const string HidHideResourceName = "UniPad.App.Resources.HidHide_x64.msi";

    /// <summary>
    /// Official ViGEmBus release used when no MSI is bundled. Pinned to an exact version rather
    /// than "latest" so a release cannot silently change what UniPad installs on a user's machine.
    /// </summary>
    public const string ViGEmDownloadUrl =
        "https://github.com/nefarius/ViGEmBus/releases/download/v1.22.0/ViGEmBus_1.22.0_x64_x86_arm64.exe";

    /// <summary>Official HidHide release used when no MSI is bundled.</summary>
    public const string HidHideDownloadUrl =
        "https://github.com/nefarius/HidHide/releases/download/v1.5.230.0/HidHide_1.5.230_x64.exe";

    /// <summary>Builds a successful result.</summary>
    private static DriverInstallResult Success(string driver, bool restartRequired) =>
        new(true,
            restartRequired,
            restartRequired ? DriverInstallOutcome.InstalledRestartRequired : DriverInstallOutcome.Installed,
            driver,
            string.Empty,
            restartRequired
                ? $"{driver} installed. A restart is required."
                : $"{driver} installed successfully.");

    /// <summary>Builds a failed result.</summary>
    private static DriverInstallResult Failure(
        DriverInstallOutcome outcome,
        string driver,
        string detail,
        string message) =>
        new(false, false, outcome, driver, detail, message);

    /// <summary>Checks whether ViGEmBus appears to be installed.</summary>
    public static DriverState GetViGEmState()
    {
        if (!OperatingSystem.IsWindows())
        {
            return DriverState.Unknown;
        }

        try
        {
            // PLATFORM: Windows registry probe for the driver's install footprint.
            using var serviceKey = Registry.LocalMachine.OpenSubKey(ViGEmServiceKey);
            if (serviceKey is not null)
            {
                return DriverState.Installed;
            }

            using var dependency = Registry.ClassesRoot.OpenSubKey(ViGEmDependencyKey);
            if (dependency?.GetValue("Version") is not null)
            {
                return DriverState.Installed;
            }

            // Some ViGEmBus builds register under a differently-suffixed dependency GUID, so scan.
            using var dependencies = Registry.ClassesRoot.OpenSubKey(@"Installer\Dependencies");
            if (dependencies is not null)
            {
                foreach (var name in dependencies.GetSubKeyNames())
                {
                    using var candidate = dependencies.OpenSubKey(name);
                    var displayName = candidate?.GetValue("DisplayName") as string;
                    if (displayName?.Contains("ViGEm", StringComparison.OrdinalIgnoreCase) == true)
                    {
                        return DriverState.Installed;
                    }
                }
            }

            return DriverState.Missing;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "ViGEmBus registry probe failed");
            return DriverState.Unknown;
        }
    }

    /// <summary>Checks whether HidHide appears to be installed.</summary>
    public static DriverState GetHidHideState()
    {
        if (!OperatingSystem.IsWindows())
        {
            return DriverState.Unknown;
        }

        try
        {
            // PLATFORM: Windows registry probe.
            using var dependency = Registry.ClassesRoot.OpenSubKey(HidHideDependencyKey);
            if (dependency?.GetValue("Version") is not null)
            {
                return DriverState.Installed;
            }

            return GetHidHideCliPath() is not null ? DriverState.Installed : DriverState.Missing;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "HidHide registry probe failed");
            return DriverState.Unknown;
        }
    }

    /// <summary>Locates <c>HidHideCLI.exe</c>, or null when HidHide is not installed.</summary>
    public static string? GetHidHideCliPath()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            // PLATFORM: Windows registry read.
            using var key = Registry.LocalMachine.OpenSubKey(HidHidePathKey)
                            ?? Registry.ClassesRoot.OpenSubKey(HidHidePathKey);

            if (key?.GetValue("Path") is string basePath && !string.IsNullOrWhiteSpace(basePath))
            {
                var candidate = Path.Combine(basePath, "x64", "HidHideCLI.exe");
                if (File.Exists(candidate))
                {
                    return candidate;
                }

                candidate = Path.Combine(basePath, "HidHideCLI.exe");
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }

            // Fall back to the default install location.
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var defaultPath = Path.Combine(programFiles, "Nefarius Software Solutions e.U.", "HidHide", "x64", "HidHideCLI.exe");
            return File.Exists(defaultPath) ? defaultPath : null;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "HidHide CLI lookup failed");
            return null;
        }
    }

    /// <summary>True when a bundled MSI for the named resource exists inside the executable.</summary>
    public static bool HasEmbeddedInstaller(string resourceName)
    {
        var assembly = Assembly.GetEntryAssembly();
        if (assembly is null)
        {
            return false;
        }

        return assembly.GetManifestResourceNames()
            .Any(n => string.Equals(n, resourceName, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Extracts an embedded MSI to the temp folder and runs it elevated and silently.
    /// </summary>
    /// <param name="resourceName">Embedded resource name of the MSI.</param>
    /// <param name="friendlyName">Driver name used in messages.</param>
    public static DriverInstallResult InstallFromEmbeddedResource(string resourceName, string friendlyName)
    {
        if (!OperatingSystem.IsWindows())
        {
            return Failure(DriverInstallOutcome.NotSupported, friendlyName, string.Empty,
                "Driver installation is only supported on Windows.");
        }

        var assembly = Assembly.GetEntryAssembly();
        if (assembly is null)
        {
            return Failure(DriverInstallOutcome.Failed, friendlyName, "entry assembly not resolved",
                "Could not resolve the entry assembly.");
        }

        using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream is null)
        {
            return Failure(DriverInstallOutcome.NotBundled, friendlyName, string.Empty,
                $"{friendlyName} installer is not bundled with this build. Please install it manually.");
        }

        var tempPath = Path.Combine(Path.GetTempPath(), $"UniPad_{friendlyName}_{Guid.NewGuid():N}.msi");

        try
        {
            using (var file = File.Create(tempPath))
            {
                stream.CopyTo(file);
            }

            return RunMsi(tempPath, friendlyName);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to extract {Driver} installer", friendlyName);
            return Failure(DriverInstallOutcome.Failed, friendlyName, ex.Message,
                $"Extraction failed: {ex.Message}");
        }
        finally
        {
            TryDelete(tempPath);
        }
    }

    /// <summary>Runs an MSI from disk, elevated and silent.</summary>
    public static DriverInstallResult RunMsi(string msiPath, string friendlyName)
    {
        if (!File.Exists(msiPath))
        {
            return Failure(DriverInstallOutcome.InstallerMissing, friendlyName, msiPath,
                $"Installer not found: {msiPath}");
        }

        try
        {
            // PLATFORM: "runas" triggers the Windows UAC prompt for this child process only.
            var startInfo = new ProcessStartInfo
            {
                FileName = "msiexec",
                Arguments = $"/i \"{msiPath}\" /qn /norestart",
                UseShellExecute = true,
                Verb = "runas",
                CreateNoWindow = true,
            };

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return Failure(DriverInstallOutcome.Failed, friendlyName, "installer process did not start",
                    "Could not start the installer process.");
            }

            // Driver installs can be slow on cold systems; five minutes is generous but finite.
            if (!process.WaitForExit(300_000))
            {
                return Failure(DriverInstallOutcome.InstallerTimedOut, friendlyName, string.Empty,
                    "Installer timed out.");
            }

            return InterpretExitCode(process.ExitCode, friendlyName);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Running {Driver} installer failed", friendlyName);
            return Failure(DriverInstallOutcome.Failed, friendlyName, ex.Message,
                $"Installation failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Installs a driver without requiring the user to find anything themselves: uses the bundled
    /// MSI when this build has one, and otherwise downloads the pinned official installer from the
    /// vendor's GitHub releases.
    /// <para>
    /// This is what keeps the "download one exe and run it" promise honest. ViGEmBus is a
    /// kernel-mode driver, so it genuinely cannot live inside a portable executable - but the user
    /// should never have to hunt for it.
    /// </para>
    /// </summary>
    /// <param name="resourceName">Embedded MSI resource name to prefer.</param>
    /// <param name="downloadUrl">Official installer URL used when nothing is bundled.</param>
    /// <param name="friendlyName">Driver name used in messages.</param>
    /// <param name="progress">Receives the stage the installation has reached.</param>
    public static async Task<DriverInstallResult> EnsureInstalledAsync(
        string resourceName,
        string downloadUrl,
        string friendlyName,
        IProgress<DriverInstallProgress>? progress = null)
    {
        if (!OperatingSystem.IsWindows())
        {
            return Failure(DriverInstallOutcome.NotSupported, friendlyName, string.Empty,
                "Driver installation is only supported on Windows.");
        }

        // Prefer an embedded MSI: it needs no network and cannot be tampered with in transit.
        if (HasEmbeddedInstaller(resourceName))
        {
            progress?.Report(new DriverInstallProgress(DriverInstallPhase.Installing, friendlyName));
            return InstallFromEmbeddedResource(resourceName, friendlyName);
        }

        var extension = Path.GetExtension(new Uri(downloadUrl).AbsolutePath);
        var tempPath = Path.Combine(
            Path.GetTempPath(),
            $"UniPad_{friendlyName}_{Guid.NewGuid():N}{extension}");

        try
        {
            progress?.Report(new DriverInstallProgress(DriverInstallPhase.Downloading, friendlyName));

            using (var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) })
            {
                client.DefaultRequestHeaders.UserAgent.ParseAdd("UniPad");

                using var response = await client
                    .GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead)
                    .ConfigureAwait(false);

                response.EnsureSuccessStatusCode();

                await using var source = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
                await using var file = File.Create(tempPath);
                await source.CopyToAsync(file).ConfigureAwait(false);
            }

            // A truncated or error-page download would otherwise reach msiexec and fail obscurely.
            if (new FileInfo(tempPath).Length < 100_000)
            {
                return Failure(DriverInstallOutcome.DownloadIncomplete, friendlyName, string.Empty,
                    $"The downloaded {friendlyName} installer looks incomplete. Please install it manually.");
            }

            progress?.Report(new DriverInstallProgress(DriverInstallPhase.InstallingElevated, friendlyName));
            return RunInstaller(tempPath, friendlyName);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Automatic {Driver} installation failed", friendlyName);

            return Failure(DriverInstallOutcome.Failed, friendlyName, ex.Message,
                $"Could not install {friendlyName} automatically ({ex.Message}). "
                + $"Please download it from {downloadUrl} and run it.");
        }
        finally
        {
            TryDelete(tempPath);
        }
    }

    /// <summary>
    /// Runs either an MSI or a vendor setup executable, elevated and as quietly as each supports.
    /// </summary>
    public static DriverInstallResult RunInstaller(string installerPath, string friendlyName)
    {
        if (!File.Exists(installerPath))
        {
            return Failure(DriverInstallOutcome.InstallerMissing, friendlyName, installerPath,
                $"Installer not found: {installerPath}");
        }

        // MSI packages go through msiexec; Nefarius ships .exe bundles that take /quiet themselves.
        return Path.GetExtension(installerPath).Equals(".msi", StringComparison.OrdinalIgnoreCase)
            ? RunMsi(installerPath, friendlyName)
            : RunSetupExecutable(installerPath, friendlyName);
    }

    private static DriverInstallResult RunSetupExecutable(string exePath, string friendlyName)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = "/quiet /norestart",
                UseShellExecute = true,
                Verb = "runas",
                CreateNoWindow = true,
            };

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return Failure(DriverInstallOutcome.Failed, friendlyName, "installer process did not start",
                    "Could not start the installer process.");
            }

            if (!process.WaitForExit(300_000))
            {
                return Failure(DriverInstallOutcome.InstallerTimedOut, friendlyName, string.Empty,
                    "Installer timed out.");
            }

            return InterpretExitCode(process.ExitCode, friendlyName);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Running {Driver} setup failed", friendlyName);
            return Failure(DriverInstallOutcome.Failed, friendlyName, ex.Message,
                $"Installation failed: {ex.Message}");
        }
    }

    private static DriverInstallResult InterpretExitCode(int exitCode, string friendlyName)
    {
        Log.Information("{Driver} installer exited with code {Code}", friendlyName, exitCode);

        return exitCode switch
        {
            0 => Success(friendlyName, restartRequired: false),
            3010 => Success(friendlyName, restartRequired: true),
            1602 or 1223 => Failure(DriverInstallOutcome.Cancelled, friendlyName, string.Empty,
                "Installation was cancelled."),
            _ => Failure(DriverInstallOutcome.InstallerFailed, friendlyName,
                exitCode.ToString(System.Globalization.CultureInfo.InvariantCulture),
                $"Installer failed with exit code {exitCode}."),
        };
    }

    /// <summary>Relaunches UniPad and asks the current instance's caller to exit.</summary>
    public static bool TryRestartApplication()
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = PortablePaths.ExecutablePath,
                UseShellExecute = true,
                WorkingDirectory = PortablePaths.ExecutableDirectory,
            };

            Process.Start(startInfo);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to restart application");
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // A leftover temp MSI is harmless.
        }
    }
}
