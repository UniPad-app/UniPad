using System.Diagnostics;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Serilog;
using UniPad.Core.Input;

namespace UniPad.Core.SystemServices;

/// <summary>
/// Wraps <c>HidHideCLI.exe</c> to hide physical controllers from every process except UniPad.
/// <para>
/// Without cloaking, a game sees both the physical pad and the virtual one, so every button press
/// registers twice. With it, only UniPad reads the physical device and the game sees just the
/// virtual pad.
/// </para>
/// <para>
/// Every state change is pushed to the CLI as a single batched invocation. The CLI needs elevation
/// for the device verbs, and Windows shows one UAC prompt per process launch, so batching is the
/// difference between one prompt and one-per-device.
/// </para>
/// <para>
/// A write-ahead crash-recovery journal is kept next to the config file. It is written *before*
/// the CLI runs, so a crash mid-operation still leaves a record. The journal may therefore list
/// devices that were never actually hidden; un-hiding an already-visible device is a harmless
/// no-op, so erring on the side of too many entries is correct.
/// </para>
/// </summary>
public sealed class HidHideService
{
    private readonly string _journalPath;
    private readonly HashSet<string> _hiddenPaths = [];
    private readonly object _lock = new();
    private string? _cliPath;
    private bool _cloakEnabled;
    private bool _selfWhitelisted;
    private static bool? _isElevated;

    /// <summary>Creates a service instance and resolves the CLI location.</summary>
    public HidHideService()
    {
        _journalPath = Path.Combine(PortablePaths.DataRoot, "hidhide-journal.json");
        _cliPath = DriverBootstrapper.GetHidHideCliPath();
    }

    /// <summary>True when HidHide is installed and its CLI was located.</summary>
    public bool IsAvailable => _cliPath is not null;

    /// <summary>True while cloaking is turned on.</summary>
    public bool IsCloakEnabled => _cloakEnabled;

    /// <summary>Device instance paths currently hidden by this session.</summary>
    public IReadOnlyCollection<string> HiddenPaths
    {
        get
        {
            lock (_lock)
            {
                return _hiddenPaths.ToList();
            }
        }
    }

    /// <summary>Re-checks whether HidHide has since been installed.</summary>
    public void Refresh() => _cliPath = DriverBootstrapper.GetHidHideCliPath();

    /// <summary>
    /// Removes any cloaking left behind by a previous crashed session. Call once during startup,
    /// before applying the current configuration.
    /// </summary>
    public void RecoverFromCrash()
    {
        if (!File.Exists(_journalPath))
        {
            return;
        }

        try
        {
            var json = File.ReadAllText(_journalPath);
            var stale = JsonSerializer.Deserialize<List<string>>(json) ?? [];

            if (stale.Count == 0 || !IsAvailable)
            {
                File.Delete(_journalPath);
                return;
            }

            Log.Warning("Found {Count} device(s) cloaked by a previous session; restoring", stale.Count);

            var args = new List<string>(stale.Count + 1);
            foreach (var path in stale)
            {
                args.Add($"--dev-unhide \"{path}\"");
            }

            args.Add("--cloak-off");

            if (RunCli(args))
            {
                File.Delete(_journalPath);
            }
            else
            {
                // Keep the journal so the next start tries again; losing it would leave the user
                // with a controller that silently does not work anywhere.
                Log.Warning("Crash recovery could not un-hide the stale devices; journal kept for retry");
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "HidHide crash recovery failed");
        }
    }

    /// <summary>Registers UniPad itself as an allowed application so it can still read the pads.</summary>
    public bool WhitelistSelf()
    {
        if (!IsAvailable)
        {
            return false;
        }

        var exe = PortablePaths.ExecutablePath;
        var ok = RunCli([$"--app-reg \"{exe}\""]);
        _selfWhitelisted |= ok;
        Log.Information("HidHide whitelist for {Exe}: {Result}", exe, ok ? "ok" : "failed");
        return ok;
    }

    /// <summary>Removes UniPad from the allow list. Used when cloaking is turned off entirely.</summary>
    public bool UnwhitelistSelf()
    {
        if (!IsAvailable)
        {
            return false;
        }

        var ok = RunCli([$"--app-unreg \"{PortablePaths.ExecutablePath}\""]);
        if (ok)
        {
            _selfWhitelisted = false;
        }

        return ok;
    }

    /// <summary>
    /// Hides the given devices and enables cloaking. Devices whose instance path cannot be
    /// resolved are skipped with a warning rather than failing the whole operation.
    /// <para>
    /// The whole transition - whitelist, un-hides, hides and the cloak toggle - goes out as one
    /// CLI call, so the user sees at most one elevation prompt. When nothing needs to change the
    /// method returns without launching anything.
    /// </para>
    /// </summary>
    public void ApplyCloaking(IEnumerable<InputDevice> devices)
    {
        if (!IsAvailable)
        {
            Log.Debug("HidHide not available; skipping cloaking");
            return;
        }

        var wanted = new HashSet<string>();
        foreach (var device in devices)
        {
            var instancePath = DeviceInstanceResolver.Resolve(device);
            if (instancePath is null)
            {
                Log.Warning("Could not resolve a device instance path for {Name}; not hiding it", device.Name);
                continue;
            }

            wanted.Add(instancePath);
        }

        lock (_lock)
        {
            var toUnhide = _hiddenPaths.Except(wanted).ToList();
            var toHide = wanted.Except(_hiddenPaths).ToList();
            var wantCloak = wanted.Count > 0;
            var cloakNeedsChange = wantCloak != _cloakEnabled;

            if (toUnhide.Count == 0 && toHide.Count == 0 && !cloakNeedsChange && _selfWhitelisted)
            {
                Log.Debug("HidHide state already matches the requested configuration; nothing to do");
                return;
            }

            // Write-ahead: record the union of old and new so a crash mid-call cannot orphan a
            // hidden device.
            var union = new HashSet<string>(_hiddenPaths);
            union.UnionWith(wanted);
            WriteJournal(union);

            var args = new List<string>(toUnhide.Count + toHide.Count + 2);

            if (!_selfWhitelisted)
            {
                args.Add($"--app-reg \"{PortablePaths.ExecutablePath}\"");
            }

            foreach (var path in toUnhide)
            {
                args.Add($"--dev-unhide \"{path}\"");
            }

            foreach (var path in toHide)
            {
                args.Add($"--dev-hide \"{path}\"");
            }

            if (cloakNeedsChange)
            {
                args.Add(wantCloak ? "--cloak-on" : "--cloak-off");
            }

            if (RunCli(args))
            {
                _selfWhitelisted = true;
                _hiddenPaths.Clear();
                foreach (var path in wanted)
                {
                    _hiddenPaths.Add(path);
                }

                _cloakEnabled = wantCloak;
                WriteJournal(_hiddenPaths);

                Log.Information(
                    "HidHide cloaking {State} for {Count} device(s)",
                    wantCloak ? "enabled" : "disabled",
                    _hiddenPaths.Count);
            }
            else
            {
                // The batch is not transactional, so the real state is unknown. Leave the union in
                // the journal and treat every path as potentially hidden; the next apply or the
                // shutdown path will try to un-hide all of them.
                _hiddenPaths.Clear();
                foreach (var path in union)
                {
                    _hiddenPaths.Add(path);
                }

                Log.Warning("HidHide cloaking could not be applied; assuming a partial state");
            }
        }
    }

    /// <summary>Un-hides everything and turns cloaking off. Must run before the process exits.</summary>
    public void DisableCloaking()
    {
        if (!IsAvailable)
        {
            return;
        }

        lock (_lock)
        {
            if (_hiddenPaths.Count == 0 && !_cloakEnabled)
            {
                return;
            }

            var args = new List<string>(_hiddenPaths.Count + 1);
            foreach (var path in _hiddenPaths)
            {
                args.Add($"--dev-unhide \"{path}\"");
            }

            args.Add("--cloak-off");

            if (RunCli(args))
            {
                _hiddenPaths.Clear();
                _cloakEnabled = false;
                WriteJournal(_hiddenPaths);
                Log.Information("HidHide cloaking disabled");
            }
            else
            {
                Log.Warning("Failed to disable HidHide cloaking; journal kept for crash recovery");
            }
        }
    }

    private void WriteJournal(IReadOnlyCollection<string> paths)
    {
        try
        {
            if (paths.Count == 0)
            {
                if (File.Exists(_journalPath))
                {
                    File.Delete(_journalPath);
                }

                return;
            }

            File.WriteAllText(_journalPath, JsonSerializer.Serialize(paths.ToList()));
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to write HidHide journal");
        }
    }

    /// <summary>
    /// True when the current process already has an elevated token, in which case the CLI can be
    /// launched directly and no UAC prompt appears at all.
    /// </summary>
    private static bool IsElevated()
    {
        if (_isElevated is not null)
        {
            return _isElevated.Value;
        }

        try
        {
            if (!OperatingSystem.IsWindows())
            {
                _isElevated = false;
            }
            else
            {
                using var identity = WindowsIdentity.GetCurrent();
                _isElevated = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Could not determine process elevation; assuming standard user");
            _isElevated = false;
        }

        return _isElevated.Value;
    }

    /// <summary>Runs the CLI once with every argument group appended in order.</summary>
    private bool RunCli(IReadOnlyList<string> argumentGroups)
    {
        if (_cliPath is null || argumentGroups.Count == 0)
        {
            return false;
        }

        var builder = new StringBuilder();
        for (var i = 0; i < argumentGroups.Count; i++)
        {
            if (i > 0)
            {
                builder.Append(' ');
            }

            builder.Append(argumentGroups[i]);
        }

        return RunCli(builder.ToString());
    }

    private bool RunCli(string arguments)
    {
        if (_cliPath is null)
        {
            return false;
        }

        try
        {
            // PLATFORM: HidHideCLI is a Windows-only console tool and its device verbs need an
            // elevated token. When UniPad already runs elevated we spawn it directly, which avoids
            // the UAC prompt entirely and lets us keep the child window hidden reliably.
            var elevated = IsElevated();
            var startInfo = new ProcessStartInfo
            {
                FileName = _cliPath,
                Arguments = arguments,
                UseShellExecute = !elevated,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            };

            if (!elevated)
            {
                startInfo.Verb = "runas";
            }

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return false;
            }

            if (!process.WaitForExit(15_000))
            {
                Log.Warning("HidHideCLI timed out: {Args}", arguments);
                return false;
            }

            if (process.ExitCode != 0)
            {
                Log.Warning("HidHideCLI exited with {Code}: {Args}", process.ExitCode, arguments);
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            // A standard user cancelling the UAC prompt lands here as an OperationCanceledException
            // wrapped in a Win32Exception; it is an expected outcome, not a bug.
            Log.Warning(ex, "HidHideCLI invocation failed: {Args}", arguments);
            return false;
        }
    }
}
