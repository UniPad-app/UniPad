namespace UniPad.Core.Input;

/// <summary>How a device is being read by the SDL backend.</summary>
public enum DeviceReadMode
{
    /// <summary>
    /// SDL has a community mapping for this VID/PID, so semantic gamepad names are available and
    /// auto-mapping is exact.
    /// </summary>
    Gamepad,

    /// <summary>
    /// No mapping exists - typical for retro adapters, no-name clones and flight sticks. Only raw
    /// button/axis/hat indices are available and the user must bind manually (or accept the
    /// heuristic auto-map).
    /// </summary>
    RawJoystick,
}

/// <summary>
/// A single physical input device tracked by <see cref="SdlInputBackend"/>.
/// <para>
/// The <see cref="Handle"/> and <see cref="GamepadHandle"/> fields hold the native SDL pointers.
/// They are only touched by the polling thread; consumers read <see cref="Snapshot"/> instead.
/// </para>
/// </summary>
public sealed class InputDevice
{
    /// <summary>Stable persistable identity.</summary>
    public required DeviceId Id { get; init; }

    /// <summary>Live SDL instance id. Changes across reconnects, never persisted.</summary>
    public uint InstanceId { get; set; }

    /// <summary>Human readable device name reported by SDL.</summary>
    public string Name { get; set; } = "Unknown Device";

    /// <summary>Whether SDL exposes this device through the semantic gamepad API.</summary>
    public DeviceReadMode ReadMode { get; set; } = DeviceReadMode.RawJoystick;

    /// <summary>Native <c>SDL_Joystick*</c>.</summary>
    public IntPtr Handle { get; set; }

    /// <summary>Native <c>SDL_Gamepad*</c>, only set in <see cref="DeviceReadMode.Gamepad"/> mode.</summary>
    public IntPtr GamepadHandle { get; set; }

    /// <summary>Number of analogue axes reported by SDL.</summary>
    public int AxisCount { get; set; }

    /// <summary>Number of digital buttons reported by SDL.</summary>
    public int ButtonCount { get; set; }

    /// <summary>Number of hat switches reported by SDL.</summary>
    public int HatCount { get; set; }

    /// <summary>USB vendor id, useful for HidHide matching.</summary>
    public ushort VendorId { get; set; }

    /// <summary>USB product id, useful for HidHide matching.</summary>
    public ushort ProductId { get; set; }

    /// <summary>Device serial number when the driver exposes one; often null.</summary>
    public string? Serial { get; set; }

    /// <summary>SDL device path (HID path on Windows), used to correlate with SetupAPI entries.</summary>
    public string? Path { get; set; }

    /// <summary>True when SDL reports rumble capability.</summary>
    public bool SupportsRumble { get; set; }

    /// <summary>True when this device is a ViGEm pad created by UniPad itself.</summary>
    public bool IsVirtual { get; set; }

    /// <summary>True while the device is present and opened successfully.</summary>
    public bool IsConnected { get; set; }

    /// <summary>Live raw state, refreshed by the polling loop.</summary>
    public InputSnapshot Snapshot { get; set; } = new(0, 0, 0);

    /// <summary>Timestamp of the last successful poll, for stale-device detection.</summary>
    public long LastPollTicks { get; set; }

    /// <summary>Display label shown in device pickers.</summary>
    public string DisplayName => $"{Name} ({Id.Port})";

    /// <summary>Short capability summary shown in the debug view.</summary>
    public string CapabilitySummary =>
        $"{AxisCount} axes, {ButtonCount} buttons, {HatCount} hats, " +
        $"{(ReadMode == DeviceReadMode.Gamepad ? "SDL mapped" : "raw")}" +
        $"{(SupportsRumble ? ", rumble" : string.Empty)}";
}
