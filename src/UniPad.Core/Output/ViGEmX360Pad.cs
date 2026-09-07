using Nefarius.ViGEm.Client;
using Nefarius.ViGEm.Client.Targets;
using Nefarius.ViGEm.Client.Targets.Xbox360;
using Serilog;
using UniPad.Core.Mapping;

namespace UniPad.Core.Output;

/// <summary>
/// Virtual Xbox 360 pad backed by ViGEmBus. Presented to Windows through XInput, which is what
/// almost every controller-aware PC game reads.
/// </summary>
public sealed class ViGEmX360Pad : IVirtualPad
{
    private readonly ViGEmClient _client;
    private readonly IXbox360Controller _pad;
    private readonly object _submitLock = new();
    private bool _connected;
    private bool _disposed;

    /// <inheritdoc />
    public int Slot { get; }

    /// <inheritdoc />
    public VirtualPadType Type => VirtualPadType.Xbox360;

    /// <inheritdoc />
    public bool IsConnected => _connected;

    /// <inheritdoc />
    public int? UserIndex { get; private set; }

    /// <inheritdoc />
    public event Action<RumbleEventArgs>? RumbleReceived;

    /// <summary>Creates a pad for the given player slot using a shared ViGEm client.</summary>
    public ViGEmX360Pad(ViGEmClient client, int slot)
    {
        _client = client;
        Slot = slot;

        _pad = _client.CreateXbox360Controller();

        // Batch mode: we set every field and then submit once per poll cycle.
        _pad.AutoSubmitReport = false;

        _pad.FeedbackReceived += OnFeedbackReceived;
    }

    private void OnFeedbackReceived(object sender, Xbox360FeedbackReceivedEventArgs e)
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

        // The user index only becomes available after Windows has assigned an XInput slot, which
        // can lag the connect by a few dozen milliseconds.
        TryResolveUserIndex();

        Log.Information("Virtual X360 pad connected for player {Slot} (XInput index {Index})",
            Slot + 1, UserIndex?.ToString() ?? "pending");
    }

    /// <summary>Re-reads the XInput slot number. Called after connect and on demand from the UI.</summary>
    public void TryResolveUserIndex()
    {
        try
        {
            UserIndex = _pad.UserIndex;
        }
        catch (Exception ex)
        {
            // Xbox360UserIndexNotReportedException is expected right after connecting.
            Log.Debug(ex, "XInput user index not reported yet for player {Slot}", Slot + 1);
            UserIndex = null;
        }
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
            Log.Warning(ex, "Error disconnecting virtual X360 pad for player {Slot}", Slot + 1);
        }

        _connected = false;
        UserIndex = null;
        Log.Information("Virtual X360 pad disconnected for player {Slot}", Slot + 1);
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
            _pad.SetButtonState(Xbox360Button.A, state.A);
            _pad.SetButtonState(Xbox360Button.B, state.B);
            _pad.SetButtonState(Xbox360Button.X, state.X);
            _pad.SetButtonState(Xbox360Button.Y, state.Y);
            _pad.SetButtonState(Xbox360Button.LeftShoulder, state.LeftBumper);
            _pad.SetButtonState(Xbox360Button.RightShoulder, state.RightBumper);
            _pad.SetButtonState(Xbox360Button.LeftThumb, state.LeftThumb);
            _pad.SetButtonState(Xbox360Button.RightThumb, state.RightThumb);
            _pad.SetButtonState(Xbox360Button.Back, state.Back);
            _pad.SetButtonState(Xbox360Button.Start, state.Start);
            _pad.SetButtonState(Xbox360Button.Guide, state.Guide);
            _pad.SetButtonState(Xbox360Button.Up, state.DPadUp);
            _pad.SetButtonState(Xbox360Button.Down, state.DPadDown);
            _pad.SetButtonState(Xbox360Button.Left, state.DPadLeft);
            _pad.SetButtonState(Xbox360Button.Right, state.DPadRight);

            _pad.SetAxisValue(Xbox360Axis.LeftThumbX, state.LeftThumbX);
            _pad.SetAxisValue(Xbox360Axis.LeftThumbY, state.LeftThumbY);
            _pad.SetAxisValue(Xbox360Axis.RightThumbX, state.RightThumbX);
            _pad.SetAxisValue(Xbox360Axis.RightThumbY, state.RightThumbY);

            _pad.SetSliderValue(Xbox360Slider.LeftTrigger, state.LeftTrigger);
            _pad.SetSliderValue(Xbox360Slider.RightTrigger, state.RightTrigger);

            _pad.SubmitReport();
        }
    }

    /// <summary>Sends a short buzz so the user can identify which physical pad drives this slot.</summary>
    public void Identify()
    {
        // Rumble on a virtual pad travels the other way (game to pad), so identification is done
        // by the OutputManager routing a pulse to the mapped physical device instead.
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _pad.FeedbackReceived -= OnFeedbackReceived;
        Disconnect();
    }
}
