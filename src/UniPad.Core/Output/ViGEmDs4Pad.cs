using Nefarius.ViGEm.Client;
using Nefarius.ViGEm.Client.Targets;
using Nefarius.ViGEm.Client.Targets.DualShock4;
using Serilog;
using UniPad.Core.Mapping;

namespace UniPad.Core.Output;

/// <summary>
/// Virtual DualShock 4 pad backed by ViGEmBus.
/// <para>
/// Windows exposes only four XInput slots, so players five to eight are emulated as DS4 pads which
/// arrive over DirectInput/HID instead. Games that read XInput exclusively will not see them - the
/// UI states this explicitly.
/// </para>
/// </summary>
public sealed class ViGEmDs4Pad : IVirtualPad
{
    private readonly ViGEmClient _client;
    private readonly IDualShock4Controller _pad;
    private readonly object _submitLock = new();
    private bool _connected;
    private bool _disposed;

    /// <inheritdoc />
    public int Slot { get; }

    /// <inheritdoc />
    public VirtualPadType Type => VirtualPadType.DualShock4;

    /// <inheritdoc />
    public bool IsConnected => _connected;

    /// <inheritdoc />
    /// <remarks>DS4 pads are not XInput devices, so no user index exists.</remarks>
    public int? UserIndex => null;

    /// <inheritdoc />
    public event Action<RumbleEventArgs>? RumbleReceived;

    /// <summary>Creates a DS4 pad for the given player slot using a shared ViGEm client.</summary>
    public ViGEmDs4Pad(ViGEmClient client, int slot)
    {
        _client = client;
        Slot = slot;

        _pad = _client.CreateDualShock4Controller();
        _pad.AutoSubmitReport = false;

        // The library marks this event obsolete in favour of AwaitRawOutputReport(), but that API
        // requires a dedicated blocking thread per pad. For rumble forwarding the event is both
        // sufficient and far cheaper, so the warning is suppressed deliberately.
#pragma warning disable CS0618
        _pad.FeedbackReceived += OnFeedbackReceived;
#pragma warning restore CS0618
    }

    private void OnFeedbackReceived(object sender, DualShock4FeedbackReceivedEventArgs e)
    {
        RumbleReceived?.Invoke(new RumbleEventArgs(e.LargeMotor, e.SmallMotor));
    }

    /// <inheritdoc />
    public void Connect()
    {
        if (_connected || _disposed)
        {
            return;
        }

        _pad.Connect();
        _connected = true;
        Log.Information("Virtual DS4 pad connected for player {Slot}", Slot + 1);
    }

    /// <inheritdoc />
    public void Disconnect()
    {
        if (!_connected)
        {
            return;
        }

        try
        {
            _pad.Disconnect();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Error disconnecting virtual DS4 pad for player {Slot}", Slot + 1);
        }

        _connected = false;
        Log.Information("Virtual DS4 pad disconnected for player {Slot}", Slot + 1);
    }

    /// <inheritdoc />
    public void Submit(in PadState state)
    {
        if (!_connected || _disposed)
        {
            return;
        }

        lock (_submitLock)
        {
            _pad.SetButtonState(DualShock4Button.Cross, state.A);
            _pad.SetButtonState(DualShock4Button.Circle, state.B);
            _pad.SetButtonState(DualShock4Button.Square, state.X);
            _pad.SetButtonState(DualShock4Button.Triangle, state.Y);
            _pad.SetButtonState(DualShock4Button.ShoulderLeft, state.LeftBumper);
            _pad.SetButtonState(DualShock4Button.ShoulderRight, state.RightBumper);
            _pad.SetButtonState(DualShock4Button.ThumbLeft, state.LeftThumb);
            _pad.SetButtonState(DualShock4Button.ThumbRight, state.RightThumb);
            _pad.SetButtonState(DualShock4Button.Share, state.Back);
            _pad.SetButtonState(DualShock4Button.Options, state.Start);

            // DS4 reports L2/R2 both as digital buttons and as analogue sliders.
            _pad.SetButtonState(DualShock4Button.TriggerLeft, state.LeftTrigger > 32);
            _pad.SetButtonState(DualShock4Button.TriggerRight, state.RightTrigger > 32);

            _pad.SetSpecialButtonsFull(state.Guide ? (byte)0x01 : (byte)0x00);

            _pad.SetDPadDirection(ResolveDPad(state));

            // DS4 axes are unsigned bytes centred on 128, and Y grows downward.
            _pad.SetAxisValue(DualShock4Axis.LeftThumbX, ToDs4Axis(state.LeftThumbX));
            _pad.SetAxisValue(DualShock4Axis.LeftThumbY, ToDs4Axis((short)(-state.LeftThumbY)));
            _pad.SetAxisValue(DualShock4Axis.RightThumbX, ToDs4Axis(state.RightThumbX));
            _pad.SetAxisValue(DualShock4Axis.RightThumbY, ToDs4Axis((short)(-state.RightThumbY)));

            _pad.SetSliderValue(DualShock4Slider.LeftTrigger, state.LeftTrigger);
            _pad.SetSliderValue(DualShock4Slider.RightTrigger, state.RightTrigger);

            _pad.SubmitReport();
        }
    }

    private static DualShock4DPadDirection ResolveDPad(in PadState state) => (state.DPadUp, state.DPadDown, state.DPadLeft, state.DPadRight) switch
    {
        (true, _, true, _) => DualShock4DPadDirection.Northwest,
        (true, _, _, true) => DualShock4DPadDirection.Northeast,
        (_, true, true, _) => DualShock4DPadDirection.Southwest,
        (_, true, _, true) => DualShock4DPadDirection.Southeast,
        (true, _, _, _) => DualShock4DPadDirection.North,
        (_, true, _, _) => DualShock4DPadDirection.South,
        (_, _, true, _) => DualShock4DPadDirection.West,
        (_, _, _, true) => DualShock4DPadDirection.East,
        _ => DualShock4DPadDirection.None,
    };

    /// <summary>Converts an SDL-range axis value to the DS4 unsigned byte encoding.</summary>
    private static byte ToDs4Axis(short value)
    {
        // -32768..32767 maps onto 0..255 with 128 as centre.
        var normalised = (value + 32768) >> 8;
        return (byte)Math.Clamp(normalised, 0, 255);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
#pragma warning disable CS0618
        _pad.FeedbackReceived -= OnFeedbackReceived;
#pragma warning restore CS0618
        Disconnect();
    }
}
