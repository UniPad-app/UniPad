using Avalonia.Threading;
using Serilog;
using UniPad.Core.Input;
using UniPad.Core.Mapping;
using UniPad.Core.Output;
using UniPad.Core.Profiles;
using UniPad.Core.SystemServices;

namespace UniPad.App.Services;

/// <summary>
/// Single owner of every long-lived runtime service: the SDL backend, the keyboard and mouse
/// source, the ViGEm output manager, the profile store and the HidHide integration.
/// <para>
/// The view models talk to this class rather than instantiating services themselves, which keeps
/// the application to exactly one polling thread and one virtual bus client.
/// </para>
/// </summary>
public sealed class AppState : IDisposable
{
    private readonly List<PlayerMapping> _players;

    /// <summary>
    /// Serialises hot-plug handling. Several devices can arrive within a few milliseconds of each
    /// other, and re-applying the mappings concurrently would fight over the virtual bus.
    /// </summary>
    private readonly SemaphoreSlim _hotplugGate = new(1, 1);

    private bool _disposed;

    /// <summary>Creates and wires up the runtime services.</summary>
    public AppState()
    {
        Store = new ProfileStore();
        Config = Store.LoadConfig();

        LogSetup.Initialise(Config.VerboseLogging);

        Input = new SdlInputBackend { PollRateHz = Config.PollRateHz };

        // The keyboard and mouse are optional: a user who never binds them should not have a raw
        // input sink running at all, and the option is read once so the sink cannot appear or
        // disappear underneath a live mapping.
        if (Config.KeyboardMouseEnabled)
        {
            KeyboardMouse = new KeyboardMouseBackend();
            KeyboardMouse.Mouse.Sensitivity = Config.MouseSensitivity;
            KeyboardMouse.Mouse.ReturnSpeed = Config.MouseReturnSpeed;
            KeyboardMouse.Mouse.InvertY = Config.MouseInvertY;

            // Lets FindDevice resolve "keyboard:0" through the same call every joystick uses.
            Input.SyntheticResolver = _ => KeyboardMouse.Device;
        }

        Output = new OutputManager(Input);
        HidHide = new HidHideService();

        _players = Store.LoadProfile(Config.ActiveProfile);
        ActiveProfileName = Config.ActiveProfile;

        // The mapping evaluation runs inside the poll cycle so latency stays within one iteration.
        // The synthetic source refreshes first, so the mapping engines see the same cycle's mouse
        // delta rather than the previous one.
        Input.OnPollCycle = KeyboardMouse is null
            ? Output.ProcessCycle
            : () =>
            {
                KeyboardMouse.Poll();
                Output.ProcessCycle();
            };

        Input.DeviceChanged += OnDeviceChanged;
    }

    /// <summary>Profile and configuration persistence.</summary>
    public ProfileStore Store { get; }

    /// <summary>Global application configuration.</summary>
    public AppConfigDto Config { get; }

    /// <summary>SDL input backend.</summary>
    public SdlInputBackend Input { get; }

    /// <summary>Synthetic keyboard and mouse source, or null when the feature is disabled.</summary>
    public KeyboardMouseBackend? KeyboardMouse { get; }

    /// <summary>Virtual pad output manager.</summary>
    public OutputManager Output { get; }

    /// <summary>HidHide cloaking integration.</summary>
    public HidHideService HidHide { get; }

    /// <summary>Name of the profile currently loaded.</summary>
    public string ActiveProfileName { get; private set; }

    /// <summary>The eight player mappings, always present even when disabled.</summary>
    public IReadOnlyList<PlayerMapping> Players => _players;

    /// <summary>
    /// Raised when a device arrives or leaves, so the UI can refresh its pickers. Always raised on
    /// the UI thread.
    /// </summary>
    public event Action? DevicesChanged;

    /// <summary>
    /// Raised when a transient status message should be shown. Always raised on the UI thread.
    /// </summary>
    public event Action<string>? StatusMessage;

    /// <summary>Starts input polling, initialises the driver and applies the loaded profile.</summary>
    public void Startup()
    {
        HidHide.RecoverFromCrash();

        try
        {
            Input.Start();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Input backend failed to start");
            RaiseStatus($"Input initialisation failed: {ex.Message}");
        }

        // Let SDL finish discovering controllers before any virtual pad is created: it enumerates
        // on its own thread, so the list is still growing when Start() returns.
        WaitForDeviceEnumeration();

        // Started after SDL so that a failure here cannot prevent controllers from working.
        KeyboardMouse?.Start();

        Output.TryInitialiseDriver();
        Output.Enabled = Config.OutputEnabled;

        ReattachDevices();
        ApplyMappings();
        ApplyCloaking();
    }

    /// <summary>
    /// Waits briefly for the device list to settle. Without this, pads were created while real
    /// controllers were still arriving, which both raced the virtual-pad detection and made the
    /// first reattach work from an incomplete list.
    /// </summary>
    private void WaitForDeviceEnumeration()
    {
        var deadline = Environment.TickCount64 + 900;
        var lastCount = -1;
        var stableSince = Environment.TickCount64;

        while (Environment.TickCount64 < deadline)
        {
            var count = Input.Devices.Count();

            if (count != lastCount)
            {
                lastCount = count;
                stableSince = Environment.TickCount64;
            }
            else if (count > 0 && Environment.TickCount64 - stableSince >= 250)
            {
                break;
            }

            Thread.Sleep(50);
        }

        // One explicit rescan covers anything SDL discovered without raising an event yet.
        Input.EnumerateDevices();
    }

    /// <summary>
    /// Reconnects saved device ids to whatever is currently plugged in. Called at startup and on
    /// every hot-plug event so a controller that returns keeps its player slot.
    /// <para>
    /// Runs in two passes and tracks ownership, because a single greedy pass used to hand the same
    /// controller to two players: with identical twin adapters the second player's saved port is
    /// briefly absent during enumeration, the same-GUID fallback then matched the first player's
    /// device, and the overwritten bindings were persisted on exit. Tracking ownership also repairs
    /// a profile that was already damaged that way.
    /// </para>
    /// </summary>
    public void ReattachDevices()
    {
        // Device key to the player index that owns it.
        var owner = new Dictionary<string, int>(StringComparer.Ordinal);

        // Pass 1: exact port matches for every player, before anything speculative happens.
        foreach (var player in _players)
        {
            // The synthetic device is never enumerated and never moves ports, so there is nothing
            // to reattach and the GUID fallback below must not run for it.
            if (player.Device is null || player.Device.IsSynthetic)
            {
                continue;
            }

            var device = Input.FindDevice(player.Device);
            if (device is not { IsConnected: true })
            {
                continue;
            }

            var key = device.Id.ToString();
            if (owner.ContainsKey(key))
            {
                // An earlier player already owns this controller, so this slot is left to pass 2 -
                // that is what heals a profile where two players point at the same device.
                continue;
            }

            owner[key] = player.Index;
            player.DeviceName = device.Name;
        }

        // Pass 2: a controller that came back on a different port keeps its player, but only when
        // the device it would take is genuinely free.
        foreach (var player in _players)
        {
            if (player.Device is null || player.Device.IsSynthetic)
            {
                continue;
            }

            if (owner.TryGetValue(player.Device.ToString(), out var ownerIndex)
                && ownerIndex == player.Index)
            {
                continue;
            }

            var fallback = Input.Devices
                .Where(d => d.Id.Guid == player.Device.Guid && !owner.ContainsKey(d.Id.ToString()))
                .OrderBy(d => d.Id.Port)
                .FirstOrDefault();

            if (fallback is null)
            {
                // Nothing suitable is plugged in. The saved id is deliberately kept, so the player
                // reattaches by itself the moment its controller comes back.
                continue;
            }

            Log.Information(
                "Player {Player} device moved from port {Old} to {New}",
                player.Index + 1, player.Device.Port, fallback.Id.Port);

            RemapPlayerDevice(player, fallback.Id);
            player.DeviceName = fallback.Name;
            owner[fallback.Id.ToString()] = player.Index;
        }
    }

    /// <summary>
    /// Rewrites every binding of a player to point at a new device id, preserving the mapping when
    /// the same controller reappears on a different port.
    /// </summary>
    private static void RemapPlayerDevice(PlayerMapping player, DeviceId newDevice)
    {
        var oldDevice = player.Device;
        player.Device = newDevice;

        if (oldDevice is null)
        {
            return;
        }

        foreach (var binding in player.Bindings.Values)
        {
            if (binding.Device == oldDevice)
            {
                binding.Device = newDevice;
            }
        }
    }

    /// <summary>
    /// Handles a hot-plug notification. Runs on the SDL polling thread, which dictates everything
    /// about the shape of this method.
    /// <para>
    /// Re-applying the mappings staggers pad connections by design and HidHide blocks on an
    /// external process, so doing that work inline would stall the poll loop for seconds - every
    /// other controller in the session would go dead while one is being plugged in. The work is
    /// therefore handed to a worker, serialised so that several devices arriving at once cannot
    /// re-apply concurrently.
    /// </para>
    /// <para>
    /// The notification events are marshalled to the UI thread because their subscribers mutate
    /// observable collections.
    /// </para>
    /// </summary>
    private void OnDeviceChanged(DeviceChangedEventArgs args)
    {
        if (!args.Added)
        {
            RaiseDevicesChanged();
            return;
        }

        _ = Task.Run(async () =>
        {
            await _hotplugGate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_disposed)
                {
                    return;
                }

                ReattachDevices();
                ApplyMappings();
                ApplyCloaking();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Hot-plug handling failed");
                RaiseStatus($"Device setup failed: {ex.Message}");
            }
            finally
            {
                _hotplugGate.Release();
            }

            RaiseDevicesChanged();
        });
    }

    /// <summary>
    /// Raises <see cref="DevicesChanged"/> on the UI thread. Safe to call from any thread; when
    /// already on the UI thread the invocation is simply deferred to the next dispatcher pass.
    /// </summary>
    private void RaiseDevicesChanged()
    {
        var handler = DevicesChanged;
        if (handler is null)
        {
            return;
        }

        Dispatcher.UIThread.Post(() => handler.Invoke());
    }

    /// <summary>Raises <see cref="StatusMessage"/> on the UI thread from any calling thread.</summary>
    private void RaiseStatus(string message)
    {
        var handler = StatusMessage;
        if (handler is null)
        {
            return;
        }

        Dispatcher.UIThread.Post(() => handler.Invoke(message));
    }

    /// <summary>Pushes the current player mappings into the output manager.</summary>
    /// <remarks>
    /// Blocks while pads are connected in slot order. Must not be called from the UI thread.
    /// </remarks>
    public void ApplyMappings()
    {
        if (!Output.IsDriverAvailable && _players.Any(p => p.Enabled))
        {
            // Retry once: the driver may have been installed since startup.
            Output.TryInitialiseDriver();
        }

        Output.ApplyMappings(_players);
    }

    /// <summary>Applies or removes HidHide cloaking according to the current configuration.</summary>
    /// <remarks>
    /// Blocks on an external process and may raise a UAC prompt. Must not be called from the UI
    /// thread.
    /// </remarks>
    public void ApplyCloaking()
    {
        HidHide.Refresh();

        if (!Config.HidePhysicalControllers)
        {
            HidHide.DisableCloaking();
            return;
        }

        // Synthetic sources are excluded deliberately: cloaking the keyboard and mouse would hide
        // them from every other application, including the desktop itself.
        var assigned = _players
            .Where(p => p is { Enabled: true, Device: not null } && !p.Device!.IsSynthetic)
            .Select(p => Input.FindDevice(p.Device))
            .Where(d => d is { IsConnected: true })
            .Select(d => d!)
            .DistinctBy(d => d.Id.ToString())
            .ToList();

        HidHide.ApplyCloaking(assigned);
    }

    /// <summary>Saves the active profile and the global configuration.</summary>
    public void SaveAll()
    {
        Store.SaveProfile(ActiveProfileName, _players);
        Config.ActiveProfile = ActiveProfileName;
        Config.PollRateHz = Input.PollRateHz;
        Config.OutputEnabled = Output.Enabled;

        if (KeyboardMouse is not null)
        {
            Config.MouseSensitivity = KeyboardMouse.Mouse.Sensitivity;
            Config.MouseReturnSpeed = KeyboardMouse.Mouse.ReturnSpeed;
            Config.MouseInvertY = KeyboardMouse.Mouse.InvertY;
        }

        Store.SaveConfig(Config);
    }

    /// <summary>Loads a different profile and applies it immediately.</summary>
    public void LoadProfile(string name)
    {
        var loaded = Store.LoadProfile(name);

        _players.Clear();
        _players.AddRange(loaded);
        ActiveProfileName = name;
        Config.ActiveProfile = name;

        ReattachDevices();
        ApplyMappings();
        ApplyCloaking();

        RaiseStatus($"Profile '{name}' loaded.");
    }

    /// <summary>Replaces one player's mapping, typically after a cancellable edit session.</summary>
    public void UpdatePlayer(PlayerMapping updated)
    {
        var index = _players.FindIndex(p => p.Index == updated.Index);
        if (index >= 0)
        {
            _players[index] = updated;
        }
    }

    /// <summary>Clears every binding of every player.</summary>
    public void ClearAll()
    {
        foreach (var player in _players)
        {
            player.ClearBindings();
            player.Enabled = false;
            player.Device = null;
            player.DeviceName = null;
        }

        ApplyMappings();
    }

    /// <summary>
    /// Runs auto-mapping for every connected device, assigning them to consecutive player slots.
    /// </summary>
    /// <remarks>
    /// Only real controllers take part. Handing a player the keyboard without being asked would be
    /// a surprise, so the keyboard is opted into per player from the device picker instead.
    /// <para>
    /// Devices are ordered by GUID and then numerically by port. Ordering by the id string alone
    /// would sort port 10 before port 2, which is exactly the sort of detail that makes twin
    /// adapters land in the wrong player slots.
    /// </para>
    /// </remarks>
    public int AutoDetectAll()
    {
        var devices = Input.Devices
            .OrderBy(d => d.Id.Guid, StringComparer.Ordinal)
            .ThenBy(d => d.Id.Port)
            .ToList();

        var assigned = 0;

        for (var i = 0; i < _players.Count && i < devices.Count; i++)
        {
            var player = _players[i];
            var result = AutoMapper.Apply(player, devices[i]);

            if (result.Success)
            {
                player.Enabled = true;
                assigned++;
            }
        }

        ApplyMappings();
        ApplyCloaking();
        return assigned;
    }

    /// <summary>Raises a status message from a view model.</summary>
    public void ReportStatus(string message) => RaiseStatus(message);

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        try
        {
            SaveAll();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to save state during shutdown");
        }

        // Unsubscribe first so a device leaving during shutdown cannot queue more work.
        Input.DeviceChanged -= OnDeviceChanged;

        // Order matters: stop cloaking before releasing devices so nothing stays hidden.
        HidHide.DisableCloaking();
        Output.Dispose();
        KeyboardMouse?.Dispose();
        Input.Dispose();
        _hotplugGate.Dispose();

        LogSetup.Shutdown();
    }
}
