using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using SDL;
using Serilog;
using UniPad.Core.SystemServices;

namespace UniPad.Core.Input;

/// <summary>Raised when the device list changes (hot-plug).</summary>
/// <param name="Device">The affected device.</param>
/// <param name="Added">True for arrival, false for removal.</param>
public readonly record struct DeviceChangedEventArgs(InputDevice Device, bool Added);

/// <summary>
/// Owns SDL initialisation, device enumeration, hot-plug handling and the high-rate polling loop.
/// <para>
/// Threading contract: SDL is only touched from the dedicated polling thread (and from
/// <see cref="Start"/> / <see cref="Dispose"/> on the caller's thread while the loop is stopped).
/// Public collections are concurrent so the UI can read them at any time.
/// </para>
/// </summary>
public sealed unsafe class SdlInputBackend : IDisposable
{
    private readonly ConcurrentDictionary<uint, InputDevice> _byInstanceId = new();
    private readonly ConcurrentDictionary<string, InputDevice> _byDeviceId = new();

    /// <summary>
    /// Instance ids belonging to ViGEm pads we created ourselves. See section 4.6 of the design
    /// document: without this, our own virtual output is re-read as input and the signal loops.
    /// </summary>
    private readonly ConcurrentDictionary<uint, byte> _virtualInstanceIds = new();

    /// <summary>
    /// GUIDs explicitly marked as virtual. No longer populated automatically: a ViGEm pad reports
    /// the same GUID as a genuine Xbox 360 controller, so learning GUIDs from a detection guess
    /// used to blacklist the user's real hardware for the rest of the session.
    /// </summary>
    private readonly ConcurrentDictionary<string, byte> _virtualGuids = new();

    private readonly object _sdlLock = new();
    private Thread? _pollThread;
    private CancellationTokenSource? _cts;
    private volatile bool _sdlReady;
    private volatile int _pollRateHz = 1000;

    /// <summary>
    /// Claim budget for the pad currently being connected. Exactly one device may be claimed per
    /// Connect() scope, which is what stops a real controller arriving at the same moment from
    /// being written off as ours.
    /// </summary>
    private int _virtualClaimRemaining;

    /// <summary>Stopwatch timestamp after which the open claim expires.</summary>
    private long _virtualClaimDeadline;

    /// <summary>USB identity of the pad flavour we asked ViGEm for.</summary>
    private volatile ushort _virtualClaimVendor;

    /// <summary>USB product id of the pad flavour we asked ViGEm for.</summary>
    private volatile ushort _virtualClaimProduct;

    private long _pollCount;
    private double _lastLoopMicroseconds;

    /// <summary>Fires on the polling thread whenever a device arrives or disappears.</summary>
    public event Action<DeviceChangedEventArgs>? DeviceChanged;

    /// <summary>Fires once per completed poll cycle, after all snapshots are refreshed.</summary>
    public event Action? Polled;

    /// <summary>All currently connected, non-virtual devices.</summary>
    public IEnumerable<InputDevice> Devices =>
        _byDeviceId.Values.Where(d => d is { IsConnected: true, IsVirtual: false });

    /// <summary>
    /// All tracked devices including virtual pads (diagnostics only). Virtual pads are kept out of
    /// the id-keyed dictionary, so they are appended from the instance map instead.
    /// </summary>
    public IEnumerable<InputDevice> AllDevices =>
        _byDeviceId.Values.Concat(_byInstanceId.Values.Where(d => d.IsVirtual));

    /// <summary>Target polling rate in Hz. Applied on the next loop iteration.</summary>
    public int PollRateHz
    {
        get => _pollRateHz;
        set => _pollRateHz = Math.Clamp(value, 60, 2000);
    }

    /// <summary>Number of completed poll cycles since start.</summary>
    public long PollCount => Interlocked.Read(ref _pollCount);

    /// <summary>Duration of the most recent poll cycle body, in microseconds.</summary>
    public double LastLoopMicroseconds => _lastLoopMicroseconds;

    /// <summary>True once SDL has been initialised successfully.</summary>
    public bool IsReady => _sdlReady;

    /// <summary>
    /// Optional callback invoked for every poll cycle after snapshots refresh. The mapping engine
    /// hooks in here so that input to output latency stays inside a single loop iteration.
    /// </summary>
    public Action? OnPollCycle { get; set; }

    /// <summary>Initialises SDL and starts the polling thread. Idempotent.</summary>
    public void Start()
    {
        if (_pollThread is not null)
        {
            return;
        }

        InitialiseSdl();

        _cts = new CancellationTokenSource();
        _pollThread = new Thread(() => PollLoop(_cts.Token))
        {
            Name = "UniPad.InputPoll",
            IsBackground = true,
            Priority = ThreadPriority.AboveNormal,
        };
        _pollThread.Start();

        Log.Information("Input polling thread started at {Rate} Hz", _pollRateHz);
    }

    private void InitialiseSdl()
    {
        if (_sdlReady)
        {
            return;
        }

        // ---- Hints must be applied before SDL_Init ----

        // CRITICAL: without background events SDL delivers nothing while a game holds focus,
        // which would make the whole application useless.
        SetHint("SDL_JOYSTICK_ALLOW_BACKGROUND_EVENTS", "1");

        // Enable HIDAPI rumble for DualShock/DualSense so the feedback path works over USB and BT.
        SetHint("SDL_JOYSTICK_HIDAPI_PS4_RUMBLE", "1");
        SetHint("SDL_JOYSTICK_HIDAPI_PS5_RUMBLE", "1");

        // Do not light up the Switch home LED - it confuses users about which app owns the pad.
        SetHint("SDL_JOYSTICK_HIDAPI_SWITCH_HOME_LED", "0");

        // Enhanced reports expose gyro/touchpad data on modern pads.
        SetHint("SDL_JOYSTICK_ENHANCED_REPORTS", "1");

        // Let SDL run its own device thread; reduces enumeration stalls on Windows.
        SetHint("SDL_JOYSTICK_THREAD", "1");

        // Raw input backend handles more oddball adapters than the legacy DirectInput path,
        // but we keep DirectInput enabled as a fallback for very old devices.
        SetHint("SDL_JOYSTICK_RAWINPUT", "1");
        SetHint("SDL_JOYSTICK_DIRECTINPUT", "1");

        SetHint("SDL_APP_NAME", "UniPad");

        var flags = SDL_InitFlags.SDL_INIT_JOYSTICK
                    | SDL_InitFlags.SDL_INIT_GAMEPAD
                    | SDL_InitFlags.SDL_INIT_EVENTS;

        if (!SDL3.SDL_Init(flags))
        {
            var error = SDL3.SDL_GetError();
            Log.Error("SDL_Init failed: {Error}", error);
            throw new InvalidOperationException($"SDL_Init failed: {error}");
        }

        // Community mapping database dramatically widens the set of pads that auto-map perfectly.
        TryLoadMappingDatabase();

        _sdlReady = true;
        Log.Information("SDL initialised");

        EnumerateDevices();
    }

    private static void SetHint(string name, string value)
    {
        if (!SDL3.SDL_SetHint(name, value))
        {
            Log.Debug("SDL hint {Hint} could not be set", name);
        }
    }

    private static void TryLoadMappingDatabase()
    {
        var path = PortablePaths.GameControllerDbFile;
        if (!File.Exists(path))
        {
            Log.Debug("gamecontrollerdb.txt not present at {Path}; relying on built-in mappings", path);
            return;
        }

        var added = SDL3.SDL_AddGamepadMappingsFromFile(path);
        if (added < 0)
        {
            Log.Warning("Failed to load mapping database: {Error}", SDL3.SDL_GetError());
        }
        else
        {
            Log.Information("Loaded {Count} controller mappings from {Path}", added, path);
        }
    }

    /// <summary>Rescans SDL's joystick list and opens anything new.</summary>
    public void EnumerateDevices()
    {
        if (!_sdlReady)
        {
            return;
        }

        lock (_sdlLock)
        {
            var count = 0;
            var ids = SDL3.SDL_GetJoysticks(&count);
            if (ids is null)
            {
                Log.Warning("SDL_GetJoysticks returned null: {Error}", SDL3.SDL_GetError());
                return;
            }

            try
            {
                for (var i = 0; i < count; i++)
                {
                    OpenDevice((uint)ids[i]);
                }
            }
            finally
            {
                SDL3.SDL_free((void*)ids);
            }
        }
    }

    /// <summary>
    /// Opens a device by SDL instance id, assigning it a stable <see cref="DeviceId"/> and
    /// reusing the existing record when the same physical device reconnects.
    /// </summary>
    private void OpenDevice(uint instanceId)
    {
        if (_byInstanceId.ContainsKey(instanceId))
        {
            return;
        }

        var sdlId = (SDL_JoystickID)instanceId;
        var guidText = GetGuidString(sdlId);
        var name = SDL3.SDL_GetJoystickNameForID(sdlId) ?? "Unknown Device";

        var joystick = SDL3.SDL_OpenJoystick(sdlId);
        if (joystick is null)
        {
            Log.Warning("SDL_OpenJoystick failed for {Name} ({Guid}): {Error}", name, guidText, SDL3.SDL_GetError());
            return;
        }

        var vendorId = SDL3.SDL_GetJoystickVendor(joystick);
        var productId = SDL3.SDL_GetJoystickProduct(joystick);

        // Ownership is settled before anything else, because a pad of ours must not consume a port
        // ordinal: the ordinals are exactly what a saved profile refers to, so letting a virtual
        // pad take port 0 would push a real controller to port 1 and break its bindings.
        if (_virtualGuids.ContainsKey(guidText) || TryClaimVirtualDevice(vendorId, productId))
        {
            var ours = new InputDevice
            {
                Id = new DeviceId(guidText, 0),
                InstanceId = instanceId,
                Name = name,
                Handle = (IntPtr)joystick,
                VendorId = vendorId,
                ProductId = productId,
                IsVirtual = true,
                IsConnected = true,
            };

            // Tracked by instance id only, so it stays invisible to pickers and to port allocation.
            _byInstanceId[instanceId] = ours;
            _virtualInstanceIds[instanceId] = 1;

            Log.Information(
                "Ignoring our own virtual pad {Name} (instance {Instance}) to prevent a feedback loop",
                name, instanceId);
            return;
        }

        // Allocate the lowest free port ordinal among real devices sharing this GUID.
        var port = AllocatePort(guidText);
        var deviceId = new DeviceId(guidText, port);

        var isGamepad = SDL3.SDL_IsGamepad(sdlId);
        SDL_Gamepad* gamepad = null;
        if (isGamepad)
        {
            gamepad = SDL3.SDL_OpenGamepad(sdlId);
            if (gamepad is null)
            {
                Log.Debug("Gamepad open failed for {Name}, falling back to raw joystick mode", name);
                isGamepad = false;
            }
        }

        var axisCount = SDL3.SDL_GetNumJoystickAxes(joystick);
        var buttonCount = SDL3.SDL_GetNumJoystickButtons(joystick);
        var hatCount = SDL3.SDL_GetNumJoystickHats(joystick);

        // Reuse the record for a returning device so bindings keep working after a reconnect.
        if (!_byDeviceId.TryGetValue(deviceId.ToString(), out var device))
        {
            device = new InputDevice { Id = deviceId };
            _byDeviceId[deviceId.ToString()] = device;
        }

        device.InstanceId = instanceId;
        device.Name = name;
        device.Handle = (IntPtr)joystick;
        device.GamepadHandle = (IntPtr)gamepad;
        device.ReadMode = isGamepad ? DeviceReadMode.Gamepad : DeviceReadMode.RawJoystick;
        device.AxisCount = Math.Max(axisCount, 0);
        device.ButtonCount = Math.Max(buttonCount, 0);
        device.HatCount = Math.Max(hatCount, 0);
        device.VendorId = vendorId;
        device.ProductId = productId;
        device.Serial = SDL3.SDL_GetJoystickSerial(joystick);
        device.Path = SDL3.SDL_GetJoystickPath(joystick);
        device.SupportsRumble = DetectRumbleSupport(joystick);
        device.IsVirtual = false;
        device.IsConnected = true;

        // Resize the snapshot only when the topology actually changed.
        if (device.Snapshot.Axes.Length != device.AxisCount
            || device.Snapshot.Buttons.Length != device.ButtonCount
            || device.Snapshot.Hats.Length != device.HatCount)
        {
            device.Snapshot = new InputSnapshot(device.AxisCount, device.ButtonCount, device.HatCount);
        }
        else
        {
            device.Snapshot.Reset();
            device.Snapshot.HasRestingValues = false;
        }

        _byInstanceId[instanceId] = device;

        Log.Information(
            "Device connected: {Name} [{Id}] {Caps}",
            device.Name, deviceId, device.CapabilitySummary);

        // Read once immediately so the resting baseline is available before any bind capture.
        ReadDevice(device);
        device.Snapshot.CaptureRestingValues();

        DeviceChanged?.Invoke(new DeviceChangedEventArgs(device, true));
    }

    /// <summary>
    /// Decides whether an arriving joystick is the pad we are in the middle of plugging in.
    /// <para>
    /// Three conditions must hold together, because each one alone has a false positive that costs
    /// the user a controller: we must be inside a Connect() scope, that scope must still have its
    /// single claim unspent, and the USB identity must match the flavour we asked ViGEm for. The
    /// previous bare time window plus GUID blacklist wrote a real controller off for the rest of
    /// the session - and since identical twin adapters share one GUID, writing off one wrote off
    /// both of them.
    /// </para>
    /// </summary>
    private bool TryClaimVirtualDevice(ushort vendorId, ushort productId)
    {
        if (Volatile.Read(ref _virtualClaimRemaining) <= 0)
        {
            return false;
        }

        if (Stopwatch.GetTimestamp() > Interlocked.Read(ref _virtualClaimDeadline))
        {
            return false;
        }

        if (vendorId != _virtualClaimVendor || productId != _virtualClaimProduct)
        {
            return false;
        }

        if (Interlocked.Decrement(ref _virtualClaimRemaining) < 0)
        {
            Interlocked.Increment(ref _virtualClaimRemaining);
            return false;
        }

        return true;
    }

    private static bool DetectRumbleSupport(SDL_Joystick* joystick)
    {
        // SDL has no direct capability query for rumble in the joystick API; a zero-amplitude
        // request is a harmless probe that returns false when the driver has no motors.
        return SDL3.SDL_RumbleJoystick(joystick, 0, 0, 0);
    }

    private int AllocatePort(string guid)
    {
        var used = _byDeviceId.Values
            .Where(d => d.Id.Guid == guid && d.IsConnected)
            .Select(d => d.Id.Port)
            .ToHashSet();

        // Prefer resurrecting a previously known but currently disconnected slot.
        var known = _byDeviceId.Values
            .Where(d => d.Id.Guid == guid && !d.IsConnected)
            .Select(d => d.Id.Port)
            .Where(p => !used.Contains(p))
            .ToList();

        if (known.Count > 0)
        {
            return known.Min();
        }

        var port = 0;
        while (used.Contains(port))
        {
            port++;
        }

        return port;
    }

    private static string GetGuidString(SDL_JoystickID instanceId)
    {
        var guid = SDL3.SDL_GetJoystickGUIDForID(instanceId);
        var buffer = stackalloc byte[64];
        SDL3.SDL_GUIDToString(guid, buffer, 64);
        return Marshal.PtrToStringUTF8((IntPtr)buffer) ?? string.Empty;
    }

    private void CloseDevice(uint instanceId)
    {
        if (!_byInstanceId.TryRemove(instanceId, out var device))
        {
            return;
        }

        if (device.GamepadHandle != IntPtr.Zero)
        {
            SDL3.SDL_CloseGamepad((SDL_Gamepad*)device.GamepadHandle);
            device.GamepadHandle = IntPtr.Zero;
        }

        if (device.Handle != IntPtr.Zero)
        {
            SDL3.SDL_CloseJoystick((SDL_Joystick*)device.Handle);
            device.Handle = IntPtr.Zero;
        }

        device.IsConnected = false;
        device.Snapshot.Reset();
        _virtualInstanceIds.TryRemove(instanceId, out _);

        if (!device.IsVirtual)
        {
            Log.Information("Device disconnected: {Name} [{Id}]", device.Name, device.Id);
            DeviceChanged?.Invoke(new DeviceChangedEventArgs(device, false));
        }
    }

    private void PollLoop(CancellationToken token)
    {
        var stopwatch = Stopwatch.StartNew();
        var frequency = Stopwatch.Frequency;

        while (!token.IsCancellationRequested)
        {
            var cycleStart = stopwatch.ElapsedTicks;
            var intervalTicks = frequency / Math.Max(_pollRateHz, 1);

            try
            {
                lock (_sdlLock)
                {
                    DrainEvents();
                    SDL3.SDL_UpdateJoysticks();

                    foreach (var device in _byInstanceId.Values)
                    {
                        if (device.IsConnected && !device.IsVirtual)
                        {
                            ReadDevice(device);
                        }
                    }
                }

                Interlocked.Increment(ref _pollCount);
                OnPollCycle?.Invoke();
                Polled?.Invoke();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Unhandled exception in polling loop");
                // Back off briefly so a persistent fault does not spin the CPU.
                Thread.Sleep(50);
            }

            var elapsed = stopwatch.ElapsedTicks - cycleStart;
            _lastLoopMicroseconds = elapsed * 1_000_000.0 / frequency;
            PreciseWait(stopwatch, cycleStart + intervalTicks, frequency);
        }

        Log.Information("Polling thread exiting after {Count} cycles", PollCount);
    }

    /// <summary>
    /// Hybrid wait: coarse sleeps while there is plenty of time left, then a spin for the final
    /// stretch. Thread.Sleep alone has ~15 ms granularity which would cap the rate around 60 Hz.
    /// </summary>
    private static void PreciseWait(Stopwatch stopwatch, long targetTicks, long frequency)
    {
        var oneMillisecondTicks = frequency / 1000;

        while (true)
        {
            var remaining = targetTicks - stopwatch.ElapsedTicks;
            if (remaining <= 0)
            {
                return;
            }

            if (remaining > oneMillisecondTicks * 2)
            {
                Thread.Sleep(1);
            }
            else
            {
                Thread.SpinWait(40);
            }
        }
    }

    private void DrainEvents()
    {
        SDL3.SDL_PumpEvents();

        SDL_Event evt;
        while (SDL3.SDL_PollEvent(&evt))
        {
            switch ((SDL_EventType)evt.type)
            {
                case SDL_EventType.SDL_EVENT_JOYSTICK_ADDED:
                    OpenDevice((uint)evt.jdevice.which);
                    break;

                case SDL_EventType.SDL_EVENT_JOYSTICK_REMOVED:
                    CloseDevice((uint)evt.jdevice.which);
                    break;

                default:
                    // All other event types are irrelevant; state is read by polling instead.
                    break;
            }
        }
    }

    private static void ReadDevice(InputDevice device)
    {
        if (device.Handle == IntPtr.Zero)
        {
            return;
        }

        var joystick = (SDL_Joystick*)device.Handle;
        var snapshot = device.Snapshot;

        for (var i = 0; i < snapshot.Axes.Length; i++)
        {
            snapshot.Axes[i] = SDL3.SDL_GetJoystickAxis(joystick, i);
        }

        for (var i = 0; i < snapshot.Buttons.Length; i++)
        {
            snapshot.Buttons[i] = SDL3.SDL_GetJoystickButton(joystick, i);
        }

        for (var i = 0; i < snapshot.Hats.Length; i++)
        {
            snapshot.Hats[i] = SDL3.SDL_GetJoystickHat(joystick, i);
        }

        snapshot.Revision++;
        device.LastPollTicks = Stopwatch.GetTimestamp();
    }

    /// <summary>
    /// Resolves synthetic device ids such as the keyboard. Set by the application layer, because SDL
    /// itself knows nothing about these devices.
    /// </summary>
    public Func<DeviceId, InputDevice?>? SyntheticResolver { get; set; }

    /// <summary>
    /// Looks up a tracked device by its stable id, falling back to the synthetic resolver so that a
    /// keyboard binding resolves through exactly the same call as a joystick one.
    /// </summary>
    public InputDevice? FindDevice(DeviceId? id)
    {
        if (id is null)
        {
            return null;
        }

        var device = _byDeviceId.GetValueOrDefault(id.ToString());
        return device ?? (id.IsSynthetic ? SyntheticResolver?.Invoke(id) : null);
    }

    /// <summary>
    /// Marks the window during which one arriving joystick of the given identity is assumed to be
    /// our own virtual pad. Wrap ViGEm <c>Connect()</c> calls in this scope.
    /// </summary>
    /// <param name="vendorId">USB vendor id ViGEm presents for this pad flavour.</param>
    /// <param name="productId">USB product id ViGEm presents for this pad flavour.</param>
    public IDisposable ExpectVirtualDevice(ushort vendorId, ushort productId) =>
        new VirtualDeviceScope(this, vendorId, productId);

    /// <summary>Convenience overload for the Xbox 360 pad identity.</summary>
    public IDisposable ExpectVirtualDevice() => ExpectVirtualDevice(0x045E, 0x028E);

    /// <summary>Explicitly marks a GUID as belonging to a virtual pad.</summary>
    /// <remarks>
    /// Only for deliberate, user-driven exclusions. Never call this from arrival detection: ViGEm
    /// pads share their GUID with genuine controllers of the same flavour.
    /// </remarks>
    public void BlacklistVirtualGuid(string guid)
    {
        if (!string.IsNullOrWhiteSpace(guid))
        {
            _virtualGuids[guid] = 1;
        }
    }

    /// <summary>Sends a rumble command to a physical device, used by the feedback router.</summary>
    public void Rumble(DeviceId deviceId, ushort lowFrequency, ushort highFrequency, uint durationMs)
    {
        var device = FindDevice(deviceId);
        if (device is not { IsConnected: true } || device.Handle == IntPtr.Zero || !device.SupportsRumble)
        {
            return;
        }

        lock (_sdlLock)
        {
            SDL3.SDL_RumbleJoystick((SDL_Joystick*)device.Handle, lowFrequency, highFrequency, durationMs);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        try
        {
            _cts?.Cancel();
            _pollThread?.Join(2000);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Error stopping polling thread");
        }

        _cts?.Dispose();
        _cts = null;
        _pollThread = null;

        if (!_sdlReady)
        {
            return;
        }

        lock (_sdlLock)
        {
            foreach (var instanceId in _byInstanceId.Keys.ToList())
            {
                CloseDevice(instanceId);
            }

            SDL3.SDL_Quit();
            _sdlReady = false;
        }

        Log.Information("SDL shut down");
    }

    private sealed class VirtualDeviceScope : IDisposable
    {
        /// <summary>Upper bound on how long a single claim can stay open.</summary>
        private const int TimeoutMs = 1500;

        private readonly SdlInputBackend _backend;

        public VirtualDeviceScope(SdlInputBackend backend, ushort vendorId, ushort productId)
        {
            _backend = backend;

            lock (backend._sdlLock)
            {
                // Flush arrivals that are already queued, so a controller the user plugged in a
                // moment ago is opened as itself instead of being claimed by this scope.
                if (backend._sdlReady)
                {
                    backend.DrainEvents();
                }

                backend._virtualClaimVendor = vendorId;
                backend._virtualClaimProduct = productId;

                Interlocked.Exchange(
                    ref backend._virtualClaimDeadline,
                    Stopwatch.GetTimestamp() + (Stopwatch.Frequency * TimeoutMs / 1000));

                Interlocked.Exchange(ref backend._virtualClaimRemaining, 1);
            }
        }

        public void Dispose()
        {
            // Wait for the pad to actually appear rather than sleeping blindly: on a healthy system
            // this returns within a few tens of milliseconds instead of always costing half a
            // second per pad, and it never blocks longer than the scope's own timeout.
            var deadline = Interlocked.Read(ref _backend._virtualClaimDeadline);

            while (Volatile.Read(ref _backend._virtualClaimRemaining) > 0
                   && Stopwatch.GetTimestamp() < deadline)
            {
                Thread.Sleep(10);
            }

            Interlocked.Exchange(ref _backend._virtualClaimRemaining, 0);
        }
    }
}
