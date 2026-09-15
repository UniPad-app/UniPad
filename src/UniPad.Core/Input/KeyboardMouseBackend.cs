using System.Diagnostics;
using System.Runtime.InteropServices;
using Serilog;

namespace UniPad.Core.Input;

/// <summary>Tuning for the mouse-to-stick conversion. Shared by every player using the mouse.</summary>
public sealed class MouseSettings
{
    /// <summary>Multiplier on raw mouse counts. 1.0 suits a typical 800 DPI mouse.</summary>
    public float Sensitivity { get; set; } = 1.0f;

    /// <summary>
    /// Time constant in seconds for the stick returning to centre once the mouse stops. Small
    /// values feel snappy but jittery; large values feel like a heavy analogue stick.
    /// </summary>
    public float ReturnSpeed { get; set; } = 0.08f;

    /// <summary>Flips vertical mouse movement.</summary>
    public bool InvertY { get; set; }
}

/// <summary>
/// Presents the keyboard and mouse as one synthetic <see cref="InputDevice"/>.
/// <para>
/// Buttons are indexed by Windows virtual-key code, which lets keyboard keys and mouse buttons
/// share a single array, plus two pseudo-buttons for the wheel. Axis 0 and 1 carry mouse movement
/// already converted into stick deflection. Because the device looks exactly like any other
/// joystick from the outside, the mapping engine, the profile format and the bind capture logic
/// all work on it unchanged.
/// </para>
/// <para>
/// Input arrives through Raw Input on a dedicated message thread. <c>RIDEV_INPUTSINK</c> is what
/// makes this work at all: without it nothing is delivered while a game holds the foreground. Raw
/// Input observes rather than intercepts, so keystrokes still reach the game - see the double
/// input note in the README.
/// </para>
/// </summary>
public sealed class KeyboardMouseBackend : IDisposable
{
    /// <summary>
    /// Counts-to-deflection factor. Calibrated so that, at sensitivity 1.0 and the default return
    /// speed, a steady 1500 counts per second reaches full deflection.
    /// </summary>
    private const float CountScale = 0.0085f;

    /// <summary>How long a wheel tick stays "pressed" so a binding can react to it.</summary>
    private const long WheelHoldMilliseconds = 60;

    private const short AxisMax = 32767;

    /// <summary>
    /// Key states written by the raw input thread and read by the poll thread. Individual bool
    /// writes are atomic and a one-cycle-late key is imperceptible, so no lock is taken here.
    /// </summary>
    private readonly bool[] _keys = new bool[KeyNames.ButtonCount];

    /// <summary>Guards the movement accumulators, which are read and cleared as a pair.</summary>
    private readonly object _mouseLock = new();

    private int _pendingDx;
    private int _pendingDy;
    private long _wheelUpUntil;
    private long _wheelDownUntil;

    private float _accumulatedX;
    private float _accumulatedY;
    private long _lastPollTimestamp;

    private Thread? _thread;
    private uint _threadId;
    private IntPtr _window;
    private IntPtr _previousWndProc;
    private WndProcDelegate? _wndProc;
    private volatile bool _running;

    /// <summary>Creates the backend. Nothing is hooked until <see cref="Start"/> is called.</summary>
    public KeyboardMouseBackend()
    {
        Device = new InputDevice
        {
            Id = DeviceId.Keyboard,
            Name = "Keyboard & Mouse",
            ReadMode = DeviceReadMode.RawJoystick,
            AxisCount = 2,
            ButtonCount = KeyNames.ButtonCount,
            HatCount = 0,
            SupportsRumble = false,
            IsVirtual = false,
            IsConnected = false,
            Snapshot = new InputSnapshot(2, KeyNames.ButtonCount, 0),
        };
    }

    /// <summary>The synthetic device exposed to the rest of the application.</summary>
    public InputDevice Device { get; }

    /// <summary>Mouse conversion tuning.</summary>
    public MouseSettings Mouse { get; } = new();

    /// <summary>True once the raw input sink is live.</summary>
    public bool IsRunning => Device.IsConnected;

    /// <summary>Starts the raw input thread. Idempotent.</summary>
    public void Start()
    {
        if (_thread is not null || !OperatingSystem.IsWindows())
        {
            return;
        }

        _running = true;
        _thread = new Thread(ThreadMain)
        {
            Name = "UniPad.RawInput",
            IsBackground = true,
        };
        _thread.Start();
    }

    /// <summary>
    /// Refreshes the synthetic snapshot. Called once per input poll cycle, before the mapping
    /// engines run, and never from the raw input thread.
    /// </summary>
    public void Poll()
    {
        var now = Stopwatch.GetTimestamp();
        var previous = _lastPollTimestamp;
        _lastPollTimestamp = now;

        // Clamped so that a paused debugger or a stalled loop cannot slam the stick to centre
        // with a single enormous decay step.
        var delta = previous == 0
            ? 0f
            : (float)((now - previous) / (double)Stopwatch.Frequency);
        delta = Math.Clamp(delta, 0f, 0.1f);

        var snapshot = Device.Snapshot;
        Array.Copy(_keys, snapshot.Buttons, KeyNames.ButtonCount);

        var nowMs = Environment.TickCount64;
        snapshot.Buttons[KeyNames.WheelUp] = nowMs < Interlocked.Read(ref _wheelUpUntil);
        snapshot.Buttons[KeyNames.WheelDown] = nowMs < Interlocked.Read(ref _wheelDownUntil);

        int dx, dy;
        lock (_mouseLock)
        {
            dx = _pendingDx;
            dy = _pendingDy;
            _pendingDx = 0;
            _pendingDy = 0;
        }

        // Movement is integrated rather than sampled: a mouse reports in bursts, so reading the
        // instantaneous delta would produce a stick that flickers between full and zero. The
        // accumulator rises while the mouse moves and decays exponentially once it stops, which
        // is what makes camera control feel continuous.
        var scale = CountScale * Math.Clamp(Mouse.Sensitivity, 0.05f, 10f);
        _accumulatedX += dx * scale;
        _accumulatedY += (Mouse.InvertY ? -dy : dy) * scale;

        var decay = MathF.Exp(-delta / Math.Clamp(Mouse.ReturnSpeed, 0.01f, 1f));
        _accumulatedX *= decay;
        _accumulatedY *= decay;

        // Clamp radially, not per axis, so a fast diagonal flick cannot exceed full deflection.
        var magnitude = MathF.Sqrt((_accumulatedX * _accumulatedX) + (_accumulatedY * _accumulatedY));
        if (magnitude > 1f)
        {
            _accumulatedX /= magnitude;
            _accumulatedY /= magnitude;
        }

        // Positive Y means the mouse moved down, matching the positive-down convention the rest
        // of the auto-mapper already uses for stick axes.
        snapshot.Axes[KeyNames.MouseAxisX] = (short)(Math.Clamp(_accumulatedX, -1f, 1f) * AxisMax);
        snapshot.Axes[KeyNames.MouseAxisY] = (short)(Math.Clamp(_accumulatedY, -1f, 1f) * AxisMax);

        snapshot.Revision++;
        Device.LastPollTicks = now;
    }

    private void ThreadMain()
    {
        try
        {
            _threadId = GetCurrentThreadId();

            if (!CreateSinkWindow() || !RegisterForRawInput())
            {
                return;
            }

            Device.IsConnected = true;
            Log.Information("Keyboard and mouse raw input sink started");

            while (_running && GetMessageW(out var message, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref message);
                DispatchMessageW(ref message);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Raw input thread failed");
        }
        finally
        {
            Device.IsConnected = false;
            Array.Clear(_keys);
            Device.Snapshot.Reset();
            DestroySinkWindow();
        }
    }

    /// <summary>
    /// Creates a message-only window to receive <c>WM_INPUT</c>. The predefined STATIC class is
    /// subclassed instead of registering a class of our own, which avoids marshalling a WNDCLASSEX
    /// and makes class-name collisions impossible.
    /// </summary>
    private bool CreateSinkWindow()
    {
        _window = CreateWindowExW(
            0, "STATIC", "UniPadRawInput", 0, 0, 0, 0, 0,
            HwndMessage, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);

        if (_window == IntPtr.Zero)
        {
            Log.Warning(
                "Could not create the raw input sink window (error {Error})",
                Marshal.GetLastWin32Error());
            return false;
        }

        _wndProc = HandleMessage;
        _previousWndProc = SetWindowLongPtrW(
            _window, GwlpWndProc, Marshal.GetFunctionPointerForDelegate(_wndProc));

        return true;
    }

    private void DestroySinkWindow()
    {
        if (_window == IntPtr.Zero)
        {
            return;
        }

        if (_previousWndProc != IntPtr.Zero)
        {
            SetWindowLongPtrW(_window, GwlpWndProc, _previousWndProc);
            _previousWndProc = IntPtr.Zero;
        }

        DestroyWindow(_window);
        _window = IntPtr.Zero;
        _wndProc = null;
    }

    private bool RegisterForRawInput()
    {
        Span<RawInputDevice> devices =
        [
            new()
            {
                UsagePage = GenericDesktopPage,
                Usage = KeyboardUsage,
                Flags = RidevInputSink,
                Target = _window,
            },
            new()
            {
                UsagePage = GenericDesktopPage,
                Usage = MouseUsage,
                Flags = RidevInputSink,
                Target = _window,
            },
        ];

        if (RegisterRawInputDevices(ref devices[0], (uint)devices.Length, (uint)Marshal.SizeOf<RawInputDevice>()))
        {
            return true;
        }

        Log.Warning("RegisterRawInputDevices failed (error {Error})", Marshal.GetLastWin32Error());
        return false;
    }

    private IntPtr HandleMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam)
    {
        if (message == WmInput)
        {
            try
            {
                ReadRawInput(lParam);
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "Malformed raw input packet ignored");
            }
        }

        // The original procedure still has to run: WM_INPUT needs DefWindowProc for cleanup.
        return CallWindowProcW(_previousWndProc, window, message, wParam, lParam);
    }

    private unsafe void ReadRawInput(IntPtr handle)
    {
        var headerSize = (uint)sizeof(RawInputHeader);
        var size = 0u;

        if (GetRawInputData(handle, RidInput, null, ref size, headerSize) != 0 || size == 0 || size > 1024)
        {
            return;
        }

        var buffer = stackalloc byte[(int)size];
        if (GetRawInputData(handle, RidInput, buffer, ref size, headerSize) != size)
        {
            return;
        }

        var header = (RawInputHeader*)buffer;
        var body = buffer + headerSize;

        switch (header->Type)
        {
            case RimTypeMouse:
                ReadMouse((RawMouse*)body);
                break;

            case RimTypeKeyboard:
                ReadKeyboard((RawKeyboard*)body);
                break;
        }
    }

    private unsafe void ReadMouse(RawMouse* mouse)
    {
        // Absolute movement comes from tablets, touch digitisers and remote desktop sessions.
        // Converting it would need the previous absolute position and still would not behave like
        // a mouse for camera control, so it is deliberately ignored.
        if ((mouse->Flags & MouseMoveAbsolute) == 0 && (mouse->LastX != 0 || mouse->LastY != 0))
        {
            lock (_mouseLock)
            {
                _pendingDx += mouse->LastX;
                _pendingDy += mouse->LastY;
            }
        }

        var flags = mouse->ButtonFlags;

        SetMouseButton(flags, RiMouseLeftDown, RiMouseLeftUp, KeyNames.MouseLeft);
        SetMouseButton(flags, RiMouseRightDown, RiMouseRightUp, KeyNames.MouseRight);
        SetMouseButton(flags, RiMouseMiddleDown, RiMouseMiddleUp, KeyNames.MouseMiddle);
        SetMouseButton(flags, RiMouseButton4Down, RiMouseButton4Up, KeyNames.MouseX1);
        SetMouseButton(flags, RiMouseButton5Down, RiMouseButton5Up, KeyNames.MouseX2);

        if ((flags & RiMouseWheel) == 0)
        {
            return;
        }

        // A wheel tick is instantaneous, so it is latched for a few milliseconds to give a
        // binding something to see.
        var expiry = Environment.TickCount64 + WheelHoldMilliseconds;
        if ((short)mouse->ButtonData > 0)
        {
            Interlocked.Exchange(ref _wheelUpUntil, expiry);
        }
        else
        {
            Interlocked.Exchange(ref _wheelDownUntil, expiry);
        }
    }

    private void SetMouseButton(ushort flags, ushort downFlag, ushort upFlag, int index)
    {
        if ((flags & downFlag) != 0)
        {
            _keys[index] = true;
        }

        if ((flags & upFlag) != 0)
        {
            _keys[index] = false;
        }
    }

    private unsafe void ReadKeyboard(RawKeyboard* keyboard)
    {
        var vk = keyboard->VKey;

        // 0xFF is the placeholder half of an extended key sequence and carries no state.
        if (vk >= 0xFF)
        {
            return;
        }

        var extended = (keyboard->Flags & RiKeyE0) != 0;
        var released = (keyboard->Flags & RiKeyBreak) != 0;

        _keys[Disambiguate(vk, keyboard->MakeCode, extended)] = !released;
    }

    /// <summary>
    /// Raw input reports the neutral VK for the modifiers, so left and right are separated using
    /// the scan code and the extended-key flag. Without this, binding right shift would silently
    /// bind left shift too.
    /// </summary>
    private static ushort Disambiguate(ushort vk, ushort makeCode, bool extended) => vk switch
    {
        VkShift => (ushort)MapVirtualKeyW(makeCode, MapvkVscToVkEx),
        VkControl => extended ? (ushort)0xA3 : (ushort)0xA2,
        VkMenu => extended ? (ushort)0xA5 : (ushort)0xA4,
        _ => vk,
    };

    /// <inheritdoc />
    public void Dispose()
    {
        if (_thread is null)
        {
            return;
        }

        _running = false;
        PostThreadMessageW(_threadId, WmQuit, IntPtr.Zero, IntPtr.Zero);

        if (!_thread.Join(1500))
        {
            Log.Warning("Raw input thread did not stop in time");
        }

        _thread = null;
        Log.Information("Keyboard and mouse raw input sink stopped");
    }

    // ---- Win32 interop ----

    private const uint WmInput = 0x00FF;
    private const uint WmQuit = 0x0012;
    private const int GwlpWndProc = -4;
    private const uint RidInput = 0x10000003;
    private const uint RimTypeMouse = 0;
    private const uint RimTypeKeyboard = 1;
    private const uint RidevInputSink = 0x00000100;
    private const ushort GenericDesktopPage = 0x01;
    private const ushort MouseUsage = 0x02;
    private const ushort KeyboardUsage = 0x06;
    private const ushort MouseMoveAbsolute = 0x0001;
    private const ushort RiMouseLeftDown = 0x0001;
    private const ushort RiMouseLeftUp = 0x0002;
    private const ushort RiMouseRightDown = 0x0004;
    private const ushort RiMouseRightUp = 0x0008;
    private const ushort RiMouseMiddleDown = 0x0010;
    private const ushort RiMouseMiddleUp = 0x0020;
    private const ushort RiMouseButton4Down = 0x0040;
    private const ushort RiMouseButton4Up = 0x0080;
    private const ushort RiMouseButton5Down = 0x0100;
    private const ushort RiMouseButton5Up = 0x0200;
    private const ushort RiMouseWheel = 0x0400;
    private const ushort RiKeyBreak = 0x01;
    private const ushort RiKeyE0 = 0x02;
    private const ushort VkShift = 0x10;
    private const ushort VkControl = 0x11;
    private const ushort VkMenu = 0x12;
    private const uint MapvkVscToVkEx = 3;

    private static readonly IntPtr HwndMessage = new(-3);

    private delegate IntPtr WndProcDelegate(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct RawInputDevice
    {
        public ushort UsagePage;
        public ushort Usage;
        public uint Flags;
        public IntPtr Target;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawInputHeader
    {
        public uint Type;
        public uint Size;
        public IntPtr Device;
        public IntPtr WParam;
    }

    /// <summary>Mirrors RAWMOUSE, including the two bytes of padding after usFlags.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct RawMouse
    {
        public ushort Flags;
        public ushort Padding;
        public ushort ButtonFlags;
        public ushort ButtonData;
        public uint RawButtons;
        public int LastX;
        public int LastY;
        public uint ExtraInformation;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawKeyboard
    {
        public ushort MakeCode;
        public ushort Flags;
        public ushort Reserved;
        public ushort VKey;
        public uint Message;
        public uint ExtraInformation;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMessage
    {
        public IntPtr Window;
        public uint Value;
        public IntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public int X;
        public int Y;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(
        uint exStyle, string className, string windowName, uint style,
        int x, int y, int width, int height,
        IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyWindow(IntPtr window);

    /// <summary>64-bit only; the project publishes win-x64 exclusively.</summary>
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtrW(IntPtr window, int index, IntPtr value);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CallWindowProcW(
        IntPtr previous, IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterRawInputDevices(ref RawInputDevice devices, uint count, uint size);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern unsafe uint GetRawInputData(
        IntPtr rawInput, uint command, byte* data, ref uint size, uint headerSize);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetMessageW(out NativeMessage message, IntPtr window, uint filterMin, uint filterMax);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref NativeMessage message);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DispatchMessageW(ref NativeMessage message);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool PostThreadMessageW(uint threadId, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint MapVirtualKeyW(uint code, uint mapType);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
}
