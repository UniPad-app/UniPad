using System.Text;
using Avalonia.Threading;
using Serilog;
using UniPad.Core.Input;
using UniPad.Core.Mapping;
using UniPad.Core.Output;
using UniPad.Core.Profiles;
using UniPad.Core.SystemServices;
using UniPad.Core.Network;
using System.Globalization;

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

    /// <summary>Serialises signature comparison and application of the output state.</summary>
    private readonly object _applyGate = new();

    /// <summary>
    /// Signature of the output state as it was last actually applied. Null until the first apply.
    /// </summary>
    private string? _appliedSignature;

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
        }

        Phones = new PhoneServer(Config.PhoneServerPort) { PreferredAddress = Config.PhoneServerAddress };
        Phones.PlayerOf = PhonePlayerOf;
        Phones.Released = ReleasePhoneSlot;
        Phones.ClientsChanged += SyncPhoneNames;
        // A phone coming online or leaving changes which pads can exist, so the output is
        // re-applied (unforced, so it is free when nothing changed). QueueApply also raises
        // DevicesChanged, which puts the phone into every player's device picker.
        Phones.ClientsChanged += QueueApply;

        // Phones resolve to their own device, everything else synthetic to the keyboard.
        Input.SyntheticResolver = id => id.IsPhone ? Phones.Find(id) : KeyboardMouse?.Device;

        // Lets bind capture hear phones even before a player has a device assigned.
        Input.SyntheticDevices = () => Phones.Devices;

        Output = new OutputManager(Input);
        HidHide = new HidHideService();
        PlayerProfiles = new PlayerProfileLibrary();

        _players = Store.LoadProfile(Config.ActiveProfile);
        ActiveProfileName = Config.ActiveProfile;

        // The mapping evaluation runs inside the poll cycle so latency stays within one iteration.
        // The synthetic source refreshes first, so the mapping engines see the same cycle's mouse
        // delta rather than the previous one.
        Input.OnPollCycle = () =>
        {
            KeyboardMouse?.Poll();
            Phones.Poll();
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

    /// <summary>Phone controller server.</summary>
    public PhoneServer Phones { get; }

    /// <summary>
    /// Number of sources a player can be mapped to: every SDL device, plus the synthetic keyboard
    /// and mouse while it is running.
    /// <para>
    /// The count lives here because two places report it - the summary in the status strip and the
    /// diagnostics line on the Advanced page - and they had drifted apart: one included the
    /// keyboard and mouse and the other did not, so the same word appeared twice on one line with
    /// two different numbers behind it.
    /// </para>
    /// </summary>
    public int InputSourceCount =>
        Input.Devices.Count()
        + (KeyboardMouse is { IsRunning: true } ? 1 : 0)
        + Phones.Devices.Count();

    /// <summary>Virtual pad output manager.</summary>
    public OutputManager Output { get; }

    /// <summary>HidHide cloaking integration.</summary>
    public HidHideService HidHide { get; }
    
    /// <summary>Shared list of single-player profiles.</summary>
    public PlayerProfileLibrary PlayerProfiles { get; }

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
    /// Raised on the UI thread with the indices of players whose phone was moved to another
    /// player, so their tabs can re-read the mapping.
    /// </summary>
    public event Action<IReadOnlyList<int>>? PlayersReassigned;

    /// <summary>
    /// Raised when a transient status message should be shown. Always raised on the UI thread.
    /// </summary>
    public event Action<string>? StatusMessage;

    /// <summary>
    /// A status line described by what it says rather than by finished text.
    /// </summary>
    /// <param name="Key">Localisation key of the sentence.</param>
    /// <param name="Detail">
    /// The part that is never translated - a profile name, a device name, an exception message -
    /// or an empty string when the sentence stands alone.
    /// </param>
    /// <param name="Fallback">
    /// Ready-made English text for a consumer that has no string table, used for logging.
    /// </param>
    public readonly record struct StatusReport(string Key, string Detail, string Fallback);

    /// <summary>
    /// Raised alongside <see cref="StatusMessage"/> for messages that carry a localisation key.
    /// <para>
    /// The plain string event formats its text once, so a message already on screen keeps the
    /// language it was raised in. Subscribers to this event can hold the key instead and rebuild
    /// the line whenever the user switches language. Always raised on the UI thread.
    /// </para>
    /// </summary>
    public event Action<StatusReport>? StatusReported;

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
            RaiseStatus(new StatusReport(
                "msg.inputInitFailed",
                ex.Message,
                $"Input initialisation failed: {ex.Message}"));
        }

        // Let SDL finish discovering controllers before any virtual pad is created: it enumerates
        // on its own thread, so the list is still growing when Start() returns.
        WaitForDeviceEnumeration();

        // Started after SDL so that a failure here cannot prevent controllers from working.
        KeyboardMouse?.Start();
        try
        {
        Phones.Start();

        if (Phones.Port != Config.PhoneServerPort)
        {
        var port = Phones.Port.ToString(CultureInfo.InvariantCulture);
        RaiseStatus(new StatusReport(
        "msg.phonePortChanged", port, $"Port was busy, phone server is using port {port}"));
        }
        }
        catch (Exception ex)
        {
        Log.Error(ex, "Phone server failed to start");
        RaiseStatus(new StatusReport(
        "msg.phoneServerFailed", ex.Message, $"Phone server failed: {ex.Message}"));
        }

        Output.TryInitialiseDriver();
        Output.Enabled = Config.OutputEnabled;

        ReattachDevices();

        // Forced, so the first apply also records the baseline signature every later comparison
        // is made against.
        ApplyOutput(force: true);
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

            // The bindings are rewritten, so the slot is held out of the poll loop meanwhile.
            using (Output.BeginMappingEdit(player.Index))
            {
                RemapPlayerDevice(player, fallback.Id);
            }

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

                // Unforced: the signature covers device presence, so a controller that genuinely
                // arrived changes it and the pads are rebuilt, while a spurious notification about
                // hardware already accounted for costs nothing.
                ApplyOutput();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Hot-plug handling failed");
                RaiseStatus(new StatusReport(
                    "msg.deviceSetupFailed",
                    ex.Message,
                    $"Device setup failed: {ex.Message}"));
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

    /// <summary>
    /// Raises a keyed status report on the UI thread, falling back to the plain string event when
    /// nothing is listening for keys.
    /// </summary>
    private void RaiseStatus(StatusReport report)
    {
        var keyed = StatusReported;
        if (keyed is not null)
        {
            Dispatcher.UIThread.Post(() => keyed.Invoke(report));
            return;
        }

        RaiseStatus(report.Fallback);
    }

    /// <summary>
    /// Applies the mappings and the cloaking, but only when something that affects the output has
    /// actually changed.
    /// </summary>
    /// <remarks>
    /// The interface calls this whenever a bound property changes, and a recycled control can write
    /// a stale value into a freshly attached view model before the correct one arrives. Without a
    /// comparison, that echo tore a working pad down and rebuilt it - the connect sound and the
    /// short delay on returning to an active player's tab - and re-ran HidHide, which makes Windows
    /// play the same sound on its own. Binding edits land here too and are skipped entirely, since
    /// the mapping engine reads the bindings live and no pad has to be touched for them.
    /// <para>
    /// Blocks while pads are connected in slot order and while HidHide waits on an external
    /// process. Must not be called from the UI thread.
    /// </para>
    /// </remarks>
    /// <param name="force">
    /// True to apply regardless of the comparison, for the explicit Apply action and for anything
    /// that replaces the player list wholesale.
    /// </param>
    /// <returns>True when the output was actually re-applied.</returns>
    public bool ApplyOutput(bool force = false)
    {
        lock (_applyGate)
        {
            // Every path that changes a player's device or enabled state ends up here.
            Phones.RefreshPlayers();

            var signature = ComposeOutputSignature();

            if (!force && string.Equals(signature, _appliedSignature, StringComparison.Ordinal))
            {
                return false;
            }

            ApplyMappings();
            ApplyCloaking();

            // Recomputed rather than reused: applying changes which pads are connected, and that
            // is part of what the signature describes.
            _appliedSignature = ComposeOutputSignature();
            return true;
        }
    }

    /// <summary>
    /// Describes everything that decides which virtual pads exist and which devices are cloaked.
    /// </summary>
    /// <remarks>
    /// Pad connection state and physical device presence are both included, so a pad that died or a
    /// controller that was plugged in changes the signature and is never mistaken for "nothing to
    /// do". Binding contents are deliberately absent: they never require a pad to be recreated, and
    /// neither does the master output switch - it gates evaluation inside the poll loop and submits
    /// a neutral state on its way down, so nothing is created or destroyed when it flips. Leaving it
    /// in only ever made the recorded signature disagree with reality, and the next hot-plug then
    /// rebuilt every pad to resolve a difference nobody had asked for.
    /// </remarks>
    private string ComposeOutputSignature()
    {
        var builder = new StringBuilder();

        builder.Append(Config.HidePhysicalControllers ? '1' : '0');
        builder.Append(Output.IsDriverAvailable ? '1' : '0');

        foreach (var player in _players)
        {
            builder.Append('|')
                   .Append(player.Index).Append(':')
                   .Append(player.Enabled ? '1' : '0').Append(':')
                   .Append((int)player.OutputType).Append(':')
                   .Append(player.Device?.ToString() ?? "-").Append(':')
                   .Append(Output.IsPadConnected(player.Index) ? '1' : '0').Append(':')
                   .Append(Input.FindDevice(player.Device) is { IsConnected: true } ? '1' : '0');
        }

        return builder.ToString();
    }

    /// <summary>Pushes the current player mappings into the output manager.</summary>
    /// <remarks>
    /// Blocks while pads are connected in slot order. Must not be called from the UI thread.
    /// Prefer <see cref="ApplyOutput"/>, which skips the work when nothing relevant changed.
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
        Config.PhoneServerAddress = Phones.PreferredAddress;

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

        lock (_players)
        {
            _players.Clear();
            _players.AddRange(loaded);
        }

        ActiveProfileName = name;
        Config.ActiveProfile = name;

        ReattachDevices();

        // Forced: the player objects were replaced, so the pads must be rebuilt even when the new
        // profile happens to describe the same devices.
        ApplyOutput(force: true);

        RaiseStatus(new StatusReport("msg.profileLoaded", name, $"Profile '{name}' loaded."));
    }

    /// <summary>Replaces one player's mapping, typically after a cancellable edit session.</summary>
    public void UpdatePlayer(PlayerMapping updated)
    {
        lock (_players)
        {
            var index = _players.FindIndex(p => p.Index == updated.Index);
            if (index >= 0)
            {
                _players[index] = updated;
            }
        }
    }

    /// <summary>Clears every binding of every player.</summary>
    public void ClearAll()
    {
        foreach (var player in _players)
        {
            // The poll loop reads these dictionaries, so each slot is detached while it is rewritten.
            using (Output.BeginMappingEdit(player.Index))
            {
                player.ClearBindings();
                player.Enabled = false;
                player.Device = null;
                player.DeviceName = null;
            }
        }

        ApplyOutput(force: true);
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

            // AutoMapper rewrites the whole binding dictionary, so the slot is held out of the
            // poll loop for the duration.
            AutoMapResult result;
            using (Output.BeginMappingEdit(player.Index))
            {
                result = AutoMapper.Apply(player, devices[i]);
            }

            if (result.Success)
            {
                player.Enabled = true;
                assigned++;
            }
        }

        ApplyOutput(force: true);
        return assigned;
    }

    /// <summary>
    /// Raises a status message from a view model.
    /// </summary>
    /// <remarks>
    /// Kept for text that has no key behind it, such as a message produced by a core service.
    /// Prefer the keyed overload wherever a key exists, so the line follows a language change.
    /// </remarks>
    public void ReportStatus(string message) => RaiseStatus(message);

    /// <summary>Raises a localisable status message from a view model.</summary>
    /// <param name="key">Localisation key of the sentence.</param>
    /// <param name="detail">Untranslated remainder, or null when the sentence stands alone.</param>
    /// <param name="fallback">
    /// English text used when no keyed subscriber is attached; defaults to the key itself, which
    /// only shows up in a context with no user interface.
    /// </param>
    public void ReportStatus(string key, string? detail, string? fallback = null) =>
        RaiseStatus(new StatusReport(key, detail ?? string.Empty, fallback ?? key));

    /// <summary>
    /// The player that uses a phone: the first enabled player whose device is that phone, or -1.
    /// Called from network and worker threads.
    /// </summary>
    private int PhonePlayerOf(DeviceId id)
    {
        lock (_players)
        {
            return _players.FirstOrDefault(p => p.Enabled && p.Device == id)?.Index ?? -1;
        }
    }

    /// <summary>
    /// Gives a phone to a player, chosen by the user from the device picker. A phone drives one
    /// player only, so any other player holding it is released. Called on the UI thread.
    /// </summary>
    public void AssignPhone(PlayerMapping target, DeviceId phone)
    {
        var released = new List<int>();

        lock (_players)
        {
            foreach (var other in _players)
            {
                if (other.Index == target.Index || other.Device != phone) continue;

                using (Output.BeginMappingEdit(other.Index))
                {
                    other.ClearBindings();
                    other.RestoreDefaults();    // Reset sliders, vibration, output type, etc. to factory values
                    other.ProfileName = null;   // Same as ClearAll: the player no longer matches any profile
                    other.Enabled = false;
                    other.Device = null;
                    other.DeviceName = null;
                }

                released.Add(other.Index);
            }

            using (Output.BeginMappingEdit(target.Index))
            {
                if (target.Device is { IsPhone: true } && target.HasAnyBinding)
                {
                    // Phone to phone: same layout, so the user's custom bindings are kept.
                    RemapPlayerDevice(target, phone);
                }
                else
                {
                    // A controller's raw indices mean nothing on a phone: write the phone layout.
                    PhoneLayout.ApplyTo(target, phone);
                    target.Device = phone;
                }

                target.DeviceName = Phones.Find(phone)?.Name;
                target.Enabled = true;
                if (target.Index >= OutputManager.XInputSlotCount)
                {
                    target.OutputType = VirtualPadType.DualShock4;
                }
            }
        }

        var handler = PlayersReassigned;
        if (released.Count > 0 && handler is not null)
        {
            Dispatcher.UIThread.Post(() => handler.Invoke(released));
        }
    }

    /// <summary>Frees the slot of a kicked phone.</summary>
    private void ReleasePhoneSlot(PhoneClient phone)
    {
        int? releasedIndex = null;

        lock (_players)
        {
            var player = _players.FirstOrDefault(p => p.Device == phone.Device.Id);
            if (player is null)
            {
                return;
            }

            using (Output.BeginMappingEdit(player.Index))
            {
                player.ClearBindings();
                player.RestoreDefaults();   // Reset sliders, vibration, output type, etc. to factory values
                player.ProfileName = null;  // Same as ClearAll: the player no longer matches any profile
                player.Enabled = false;
                player.Device = null;
                player.DeviceName = null;
            }

            releasedIndex = player.Index;
        }

        QueueApply();

        // Notify the player's tab so the sliders and device list refresh with the new values
        var handler = PlayersReassigned;
        if (releasedIndex is int index && handler is not null)
        {
            Dispatcher.UIThread.Post(() => handler.Invoke(new[] { index }));
        }
    }

    /// <summary>Keeps the player's device caption in step with a phone rename.</summary>
    private void SyncPhoneNames()
    {
        lock (_players)
        {
            foreach (var phone in Phones.Clients)
            {
                var player = _players.FirstOrDefault(p => p.Device == phone.Device.Id);
                if (player is not null) player.DeviceName = phone.Device.Name;
            }
        }
    }

    /// <summary>Applies the output off-thread, serialised with hot-plug handling.</summary>
    private void QueueApply()
    {
        if (_disposed)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await _hotplugGate.WaitAsync().ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
                return; // shutting down
            }

            try
            {
                if (!_disposed)
                {
                    ApplyOutput();
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Applying phone slot failed");
            }
            finally
            {
                try { _hotplugGate.Release(); } catch (ObjectDisposedException) { /* shutting down */ }
            }

            if (!_disposed)
            {
                RaiseDevicesChanged();
            }
        });
    }

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
        // Closing the sockets raises ClientsChanged; nothing may be queued while shutting down.
        Phones.ClientsChanged -= QueueApply;
        Phones.ClientsChanged -= SyncPhoneNames;
        Phones.Dispose();
        Output.Dispose();
        KeyboardMouse?.Dispose();
        Input.Dispose();
        _hotplugGate.Dispose();

        LogSetup.Shutdown();
    }
}
