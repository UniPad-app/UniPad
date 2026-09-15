using Avalonia.Threading;
using Serilog;
using UniPad.Core.Input;
using UniPad.Core.Mapping;

namespace UniPad.App.Services;

/// <summary>Outcome of a bind capture session.</summary>
/// <param name="Binding">The captured binding, or null on cancel/timeout.</param>
/// <param name="TimedOut">True when the countdown expired without input.</param>
public readonly record struct CaptureResult(InputBinding? Binding, bool TimedOut);

/// <summary>
/// Listens for the next physical input and turns it into an <see cref="InputBinding"/>.
/// <para>
/// The awkward part is axis drift: many old controllers rest at values far from zero, and some
/// jitter continuously. A baseline of every axis is therefore taken the instant capture starts,
/// and an axis only qualifies once it has moved more than half of full travel away from that
/// baseline. Buttons and hats need no such treatment.
/// </para>
/// <para>
/// Three threads touch a session: the UI thread starts and cancels it, the polling thread detects
/// the input, and a worker runs the countdown. Completion therefore goes through a single guarded
/// hand-off so that whichever thread gets there first wins and the rest become no-ops.
/// </para>
/// </summary>
public sealed class BindCaptureService
{
    /// <summary>Fraction of full axis travel required to accept an axis as the captured input.</summary>
    private const float AxisTriggerFraction = 0.5f;

    private const short AxisMax = 32767;

    private readonly SdlInputBackend _input;

    /// <summary>Synthetic keyboard and mouse source, or null when the feature is disabled.</summary>
    private readonly KeyboardMouseBackend? _keyboardMouse;

    /// <summary>Guards the session fields against the three threads that reach them.</summary>
    private readonly object _gate = new();

    /// <summary>Baseline axis values per device, captured when the session starts.</summary>
    private readonly Dictionary<string, short[]> _axisBaselines = [];

    /// <summary>Button states at session start, so an already-held button is not captured.</summary>
    private readonly Dictionary<string, bool[]> _buttonBaselines = [];

    private readonly Dictionary<string, byte[]> _hatBaselines = [];

    private volatile TaskCompletionSource<CaptureResult>? _completion;
    private volatile int _secondsRemaining;
    private CancellationTokenSource? _timeout;
    private DeviceId? _restrictToDevice;

    /// <summary>Creates a capture service reading from the given backends.</summary>
    /// <param name="input">SDL backend supplying the joysticks.</param>
    /// <param name="keyboardMouse">Optional synthetic keyboard and mouse source.</param>
    public BindCaptureService(SdlInputBackend input, KeyboardMouseBackend? keyboardMouse = null)
    {
        _input = input;
        _keyboardMouse = keyboardMouse;
    }

    /// <summary>True while a capture session is running.</summary>
    public bool IsCapturing => _completion is not null;

    /// <summary>Seconds remaining in the countdown, for display on the bind button.</summary>
    public int SecondsRemaining => _secondsRemaining;

    /// <summary>Raised once per second during the countdown, always on the UI thread.</summary>
    public event Action<int>? CountdownTick;

    /// <summary>
    /// Starts a capture session. Expected to be called from the UI thread.
    /// </summary>
    /// <param name="restrictToDevice">
    /// When set, only input from this device is accepted. This prevents a second controller resting
    /// on a drifting axis from stealing the binding.
    /// </param>
    /// <param name="timeoutSeconds">Countdown length; five seconds matches yuzu.</param>
    public Task<CaptureResult> CaptureAsync(DeviceId? restrictToDevice, int timeoutSeconds = 5)
    {
        Cancel();

        TaskCompletionSource<CaptureResult> completion;
        CancellationTokenSource timeout;

        lock (_gate)
        {
            _restrictToDevice = restrictToDevice;

            completion = new TaskCompletionSource<CaptureResult>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            timeout = new CancellationTokenSource();

            _completion = completion;
            _timeout = timeout;
            _secondsRemaining = timeoutSeconds;

            TakeBaselines();
        }

        _input.Polled += OnPolled;
        _ = RunCountdownAsync(timeoutSeconds, completion, timeout.Token);

        return completion.Task;
    }

    /// <summary>Aborts the current session, resolving the task with a cancelled result.</summary>
    public void Cancel() => TryComplete(null, new CaptureResult(null, TimedOut: false));

    /// <summary>
    /// Ends the active session and resolves its task. Returns false when there was nothing to end,
    /// or when <paramref name="expected"/> refers to a session that has already been superseded.
    /// </summary>
    private bool TryComplete(TaskCompletionSource<CaptureResult>? expected, CaptureResult result)
    {
        TaskCompletionSource<CaptureResult> taken;
        CancellationTokenSource? timeout;

        lock (_gate)
        {
            var current = _completion;
            if (current is null || (expected is not null && !ReferenceEquals(current, expected)))
            {
                return false;
            }

            taken = current;
            timeout = _timeout;

            _completion = null;
            _timeout = null;
            _secondsRemaining = 0;
        }

        _input.Polled -= OnPolled;

        try
        {
            timeout?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already torn down elsewhere; nothing to do.
        }

        timeout?.Dispose();

        return taken.TrySetResult(result);
    }

    private async Task RunCountdownAsync(
        int seconds,
        TaskCompletionSource<CaptureResult> completion,
        CancellationToken token)
    {
        try
        {
            for (var remaining = seconds; remaining > 0; remaining--)
            {
                _secondsRemaining = remaining;

                // The loop continues on a worker after the first delay, so the tick has to be
                // marshalled: its subscribers update bind-button captions.
                var tick = remaining;
                Dispatcher.UIThread.Post(() => CountdownTick?.Invoke(tick));

                await Task.Delay(1000, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }

        TryComplete(completion, new CaptureResult(null, TimedOut: true));
    }

    /// <summary>
    /// Snapshots the current state of every candidate device. Everything the capture logic later
    /// compares against comes from here. Called under <see cref="_gate"/>.
    /// </summary>
    private void TakeBaselines()
    {
        _axisBaselines.Clear();
        _buttonBaselines.Clear();
        _hatBaselines.Clear();

        foreach (var device in CandidateDevices())
        {
            var key = device.Id.ToString();
            var snapshot = device.Snapshot;

            var axes = new short[snapshot.Axes.Length];
            Array.Copy(snapshot.Axes, axes, axes.Length);
            _axisBaselines[key] = axes;

            var buttons = new bool[snapshot.Buttons.Length];
            Array.Copy(snapshot.Buttons, buttons, buttons.Length);
            _buttonBaselines[key] = buttons;

            var hats = new byte[snapshot.Hats.Length];
            Array.Copy(snapshot.Hats, hats, hats.Length);
            _hatBaselines[key] = hats;
        }
    }

    /// <summary>
    /// Devices the current session will listen to. The synthetic keyboard is appended explicitly
    /// because it is not part of the SDL enumeration.
    /// </summary>
    private IEnumerable<InputDevice> CandidateDevices()
    {
        if (_restrictToDevice is not null)
        {
            var restricted = _restrictToDevice.IsSynthetic
                ? _keyboardMouse?.Device
                : _input.FindDevice(_restrictToDevice);

            return restricted is { IsConnected: true } ? [restricted] : [];
        }

        var synthetic = _keyboardMouse?.Device;
        return synthetic is { IsConnected: true }
            ? _input.Devices.Append(synthetic)
            : _input.Devices;
    }

    /// <summary>
    /// Runs on the polling thread once per cycle and looks for the first qualifying change.
    /// </summary>
    private void OnPolled()
    {
        if (_completion is null)
        {
            return;
        }

        foreach (var device in CandidateDevices())
        {
            var binding = TryDetect(device);
            if (binding is null)
            {
                continue;
            }

            if (TryComplete(null, new CaptureResult(binding, TimedOut: false)))
            {
                Log.Debug("Captured binding: {Binding}", binding.ToParamString());
            }

            return;
        }
    }

    private InputBinding? TryDetect(InputDevice device)
    {
        var key = device.Id.ToString();
        var snapshot = device.Snapshot;

        // ---- Buttons: a transition from released to pressed ----
        if (_buttonBaselines.TryGetValue(key, out var buttonBaseline))
        {
            var count = Math.Min(snapshot.Buttons.Length, buttonBaseline.Length);
            for (var i = 0; i < count; i++)
            {
                if (snapshot.Buttons[i] && !buttonBaseline[i])
                {
                    return InputBinding.ForButton(device.Id, i);
                }
            }
        }

        // ---- Hats: a newly set direction bit ----
        if (_hatBaselines.TryGetValue(key, out var hatBaseline))
        {
            var count = Math.Min(snapshot.Hats.Length, hatBaseline.Length);
            for (var i = 0; i < count; i++)
            {
                var newBits = (byte)(snapshot.Hats[i] & ~hatBaseline[i]);
                if (newBits == 0)
                {
                    continue;
                }

                // Report a single direction even when a diagonal was pressed.
                var mask = (newBits & InputBinding.HatUp) != 0 ? InputBinding.HatUp
                    : (newBits & InputBinding.HatDown) != 0 ? InputBinding.HatDown
                    : (newBits & InputBinding.HatLeft) != 0 ? InputBinding.HatLeft
                    : InputBinding.HatRight;

                return InputBinding.ForHat(device.Id, i, mask);
            }
        }

        // ---- Axes: movement of more than half full travel from the baseline ----
        // The mouse axes are assigned by the auto-map defaults and never by capture: simply moving
        // the mouse towards the bind button would otherwise grab the binding before the user has
        // pressed anything at all.
        if (!device.Id.IsSynthetic && _axisBaselines.TryGetValue(key, out var axisBaseline))
        {
            var count = Math.Min(snapshot.Axes.Length, axisBaseline.Length);
            var threshold = (int)(AxisMax * AxisTriggerFraction);

            for (var i = 0; i < count; i++)
            {
                var delta = snapshot.Axes[i] - axisBaseline[i];
                if (Math.Abs(delta) < threshold)
                {
                    continue;
                }

                var direction = delta > 0 ? AxisDirection.Positive : AxisDirection.Negative;
                return InputBinding.ForAxis(device.Id, i, direction);
            }
        }

        return null;
    }
}
