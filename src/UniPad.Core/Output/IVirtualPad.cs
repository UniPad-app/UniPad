using UniPad.Core.Mapping;

namespace UniPad.Core.Output;

/// <summary>Rumble amplitudes reported back by the game.</summary>
/// <param name="LargeMotor">Low frequency / heavy motor amplitude, 0..255.</param>
/// <param name="SmallMotor">High frequency / light motor amplitude, 0..255.</param>
public readonly record struct RumbleEventArgs(byte LargeMotor, byte SmallMotor);

/// <summary>
/// Abstraction over a virtual pad so the output backend can be replaced without touching the rest
/// of the application. ViGEmBus is archived upstream, so keeping this seam is a deliberate hedge.
/// </summary>
public interface IVirtualPad : IDisposable
{
    /// <summary>Player slot this pad represents, 0..7.</summary>
    int Slot { get; }

    /// <summary>Which pad flavour is emulated.</summary>
    VirtualPadType Type { get; }

    /// <summary>True while the pad is plugged into the virtual bus.</summary>
    bool IsConnected { get; }

    /// <summary>
    /// XInput user index reported by Windows, or null when unknown/not applicable. Shown in the UI
    /// so the user can tell which physical controller became which player.
    /// </summary>
    int? UserIndex { get; }

    /// <summary>Plugs the pad in.</summary>
    void Connect();

    /// <summary>Unplugs the pad.</summary>
    void Disconnect();

    /// <summary>
    /// Pushes one complete report. The implementation must batch all field writes and issue a
    /// single submit so the game never observes a half-updated state.
    /// </summary>
    void Submit(in PadState state);

    /// <summary>Raised when the game sends rumble to this pad.</summary>
    event Action<RumbleEventArgs>? RumbleReceived;
}
