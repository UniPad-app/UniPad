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

    /// <summary>GUIDs known to belong to virtual pads (broader net than instance ids).</summary>
    private readonly ConcurrentDictionary<string, byte> _virtualGuids = new();

    private readonly object _sdlLock = new();
    private Thread? _pollThread;
    private CancellationTokenSource? _cts;
    private volatile bool _sdlReady;
    private volatile int _pollRateHz = 1000;

    /// <summary>Set while a virtual pad is being plugged in, so arrivals are treated as ours.</summary>
    private volatile bool _expectingVirtualDevice;

    private long _pollCount;
    private double _lastLoopMicroseconds;

    /// <summary>Fires on the polling thread whenever a device arrives or disappears.</summary>
    public event Action<DeviceChangedEventArgs>? DeviceChanged;

    /// <summary>Fires once per completed poll cycle, after all snapshots are refreshed.</summary>
    public event Action? Polled;

    /// <summary>All currently connected, non-virtual devices.</summary>
    public IEnumerable<InputDevice> Devices =>
        _byDeviceId.Values.Where(d => d is { IsConnected: true, IsVirtual: false });

    /// <summary>All tracked devices including virtual pads (diagnostics only).</summary>
    public IEnumerable<InputDevice> AllDevices => _byDeviceId.Values;

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
        var isVirtualPad = _expectingVirtualDevice || _virtualGuids.ContainsKey(guidText);

        // Allocate the lowest free port ordinal among devices sharing this GUID.
        var port = AllocatePort(guidText);
        var deviceId = new DeviceId(guidText, port);

        var joystick = SDL3.SDL_OpenJoystick(sdlId);
        if (joystick is null)
        {
            Log.Warning("SDL_OpenJoystick failed for {Name} ({Guid}): {Error}", name, guidText, SDL3.SDL_GetError());
            return;
        }

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
        device.VendorId = SDL3.SDL_GetJoystickVendor(joystick);
        device.ProductId = SDL3.SDL_GetJoystickProduct(joystick);
        device.Serial = SDL3.SDL_GetJoystickSerial(joystick);
        device.Path = SDL3.SDL_GetJoystickPath(joystick);
        device.SupportsRumble = DetectRumbleSupport(joystick);
        device.IsVirtual = isVirtualPad;
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

        if (isVirtualPad)
        {
            _virtualInstanceIds[instanceId] = 1;
            _virtualGuids[guidText] = 1;
            Log.Information("Ignoring virtual pad {Name} ({Id}) to prevent feedback loop", name, deviceId);
        }
        else
        {
            Log.Information(
                "Device connected: {Name} [{Id}] {Caps}",
                device.Name, deviceId, device.CapabilitySummary);
        }

        // Read once immediately so the resting baseline is available before any bind capture.
        ReadDevice(device);
        device.Snapshot.CaptureRestingValues();

        if (!isVirtualPad)
        {
            DeviceChanged?.Invoke(new DeviceChangedEventArgs(device, true));
        }
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

    /// <summary>Looks up a tracked device by its stable id.</summary>
    public InputDevice? FindDevice(DeviceId? id) =>
        id is null ? null : _byDeviceId.GetValueOrDefault(id.ToString());

    /// <summary>
    /// Marks the window during which newly arriving joysticks are assumed to be our own virtual
    /// pads. Wrap ViGEm <c>Connect()</c> calls in this scope.
    /// </summary>
    public IDisposable ExpectVirtualDevice() => new VirtualDeviceScope(this);

    /// <summary>Explicitly blacklists a GUID as belonging to a virtual pad.</summary>
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
        private readonly SdlInputBackend _backend;

        public VirtualDeviceScope(SdlInputBackend backend)
        {
            _backend = backend;
            _backend._expectingVirtualDevice = true;
        }

        public void Dispose()
        {
            // Give SDL a moment to surface the arrival event for the pad we just plugged in.
            Thread.Sleep(500);
            _backend._expectingVirtualDevice = false;
        }
    }
}
