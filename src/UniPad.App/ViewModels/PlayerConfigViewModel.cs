using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UniPad.App.Localization;
using UniPad.App.Services;
using UniPad.Core.Input;
using UniPad.Core.Mapping;
using UniPad.Core.Output;
using System.Globalization;

namespace UniPad.App.ViewModels;

/// <summary>
/// Carries an emulated pad state to a view. Declared as a delegate with an <c>in</c> parameter
/// rather than <c>Action&lt;PadState&gt;</c> so the struct is passed by reference and the 60 Hz
/// preview does not box once per frame.
/// </summary>
/// <param name="state">The state currently being sent to the game.</param>
public delegate void PadStatePreviewHandler(in PadState state);

/// <summary>One entry in the input device picker.</summary>
/// <param name="Id">Stable device id, or null for the "Any / none" entry.</param>
/// <param name="Display">Text shown in the combo box.</param>
public sealed record DeviceOption(DeviceId? Id, string Display)
{
    /// <inheritdoc />
    public override string ToString() => Display;
}

/// <summary>
/// View model for one player tab: device selection, the full binding grid, stick tuning and the
/// live preview values.
/// </summary>
public sealed partial class PlayerConfigViewModel : ViewModelBase
{
    /// <summary>
    /// How long to wait before re-reading the XInput slot number after pads were connected.
    /// <para>
    /// ViGEm's user index is not available the instant <c>Connect()</c> returns - Windows assigns
    /// it a few dozen milliseconds later - so the refresh that runs as part of ApplyMappings finds
    /// nothing and the slot caption stays empty. A second, delayed read is what fills it in.
    /// </para>
    /// </summary>
    private const int SlotResolveDelayMs = 350;

    /// <summary>
    /// Window over which repeated apply requests are collapsed into one.
    /// <para>
    /// A recycled control writes its stale value into a newly attached view model and the correct
    /// value immediately after, so requests arrive in pairs a few milliseconds apart. Coalescing
    /// them means the single apply that runs sees the final state, which the signature comparison
    /// in AppState then recognises as unchanged - no pad is torn down and nothing is re-cloaked.
    /// </para>
    /// </summary>
    private const int ApplyCoalesceMs = 250;

    /// <summary>Cancels the pending coalesced apply when a newer request arrives.</summary>
    private CancellationTokenSource? _pendingApply;

    private readonly AppState _state;
    private readonly BindCaptureService _capture;
    private readonly Dictionary<PadTarget, BindButtonViewModel> _bindLookup = [];
    private bool _suppressPropagation;

    [ObservableProperty]
    private bool _isEnabled;

    [ObservableProperty]
    private DeviceOption? _selectedDevice;

    [ObservableProperty]
    private VirtualPadType _outputType;

    [ObservableProperty]
    private bool _emulateStickWithDpad;

    [ObservableProperty]
    private bool _vibrationEnabled;

    [ObservableProperty]
    private int _vibrationStrength;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LeftDeadzoneText))]
    private float _leftDeadzone;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LeftRangeText))]
    private float _leftRange;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RightDeadzoneText))]
    private float _rightDeadzone;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RightRangeText))]
    private float _rightRange;

    // ---- Live preview values, refreshed at 60 Hz ----

    [ObservableProperty]
    private double _leftStickX;

    [ObservableProperty]
    private double _leftStickY;

    [ObservableProperty]
    private double _rightStickX;

    [ObservableProperty]
    private double _rightStickY;

    [ObservableProperty]
    private double _leftTriggerFill;

    [ObservableProperty]
    private double _rightTriggerFill;

    [ObservableProperty]
    private string _deviceStatus = string.Empty;

    [ObservableProperty]
    private string _slotStatus = string.Empty;

    /// <summary>Creates a view model for the given player slot.</summary>
    public PlayerConfigViewModel(AppState state, PlayerMapping mapping, BindCaptureService capture)
    {
        _state = state;
        _capture = capture;
        Mapping = mapping;

        foreach (var target in PadTargetInfo.All)
        {
            var vm = new BindButtonViewModel(this, target, capture);
            _bindLookup[target] = vm;
        }

        // Devices first: PullFromMapping resolves SelectedDevice against the Devices collection,
        // so with an empty list it would select nothing and leave the picker blank.
        RefreshDevices();
        PullFromMapping();

        // The four slider captions are built in code, so they have to be rebuilt by hand when the
        // language changes; the bindings themselves cannot see it.
        Strings.Instance.LanguageChanged += OnLanguageChanged;
    }

    /// <summary>The underlying runtime mapping this view model edits in place.</summary>
    public PlayerMapping Mapping { get; }

    /// <summary>One-based player number used in captions.</summary>
    public int PlayerNumber => Mapping.Index + 1;

    /// <summary>Tab header text.</summary>
    public string Header => $"{Strings.Get("tab.player")} {PlayerNumber}";

    /// <summary>Caption above the left stick dead zone slider, label and value in one string.</summary>
    public string LeftDeadzoneText => FormatPercent("bind.deadzone", LeftDeadzone);

    /// <summary>Caption above the left stick range slider.</summary>
    public string LeftRangeText => FormatPercent("bind.range", LeftRange);

    /// <summary>Caption above the right stick dead zone slider.</summary>
    public string RightDeadzoneText => FormatPercent("bind.deadzone", RightDeadzone);

    /// <summary>Caption above the right stick range slider.</summary>
    public string RightRangeText => FormatPercent("bind.range", RightRange);

    /// <summary>
    /// Builds a "label: 15%" caption as a single string.
    /// <para>
    /// Composed here rather than from several XAML runs: separate runs are reordered independently
    /// under the bidirectional algorithm, which pushed the per cent sign to the far end of the line
    /// and left the digits behind. One string keeps the number and its sign together. The value is
    /// formatted with the invariant culture so the digits stay Latin in both languages, matching
    /// the key names shown on the bind buttons.
    /// </para>
    /// </summary>
    private static string FormatPercent(string key, float value) =>
        string.Format(CultureInfo.InvariantCulture, "{0}: {1:0}%", Strings.Get(key), value * 100f);

    private void OnLanguageChanged()
    {
        OnPropertyChanged(nameof(Header));
        OnPropertyChanged(nameof(LeftDeadzoneText));
        OnPropertyChanged(nameof(LeftRangeText));
        OnPropertyChanged(nameof(RightDeadzoneText));
        OnPropertyChanged(nameof(RightRangeText));
    }

    /// <summary>Available input devices plus the "none" entry.</summary>
    public ObservableCollection<DeviceOption> Devices { get; } = [];

    /// <summary>Selectable output pad types.</summary>
    public VirtualPadType[] OutputTypes { get; } = [VirtualPadType.Xbox360, VirtualPadType.DualShock4];

    /// <summary>
    /// True for players five to eight, where only DS4 output is possible. Bound to a warning label.
    /// </summary>
    public bool IsBeyondXInputLimit => Mapping.Index >= OutputManager.XInputSlotCount;

    /// <summary>
    /// Raised once per UI tick with the state the game is receiving, so the view can push it into
    /// the controller diagram. Raised on the UI thread only.
    /// </summary>
    public event PadStatePreviewHandler? PreviewStateUpdated;

    // ---- Bind button accessors, one property per target so XAML can bind directly ----

    /// <summary>A button binding.</summary>
    public BindButtonViewModel BindA => _bindLookup[PadTarget.A];
    /// <summary>B button binding.</summary>
    public BindButtonViewModel BindB => _bindLookup[PadTarget.B];
    /// <summary>X button binding.</summary>
    public BindButtonViewModel BindX => _bindLookup[PadTarget.X];
    /// <summary>Y button binding.</summary>
    public BindButtonViewModel BindY => _bindLookup[PadTarget.Y];
    /// <summary>Left bumper binding.</summary>
    public BindButtonViewModel BindLeftBumper => _bindLookup[PadTarget.LeftBumper];
    /// <summary>Right bumper binding.</summary>
    public BindButtonViewModel BindRightBumper => _bindLookup[PadTarget.RightBumper];
    /// <summary>Left trigger binding.</summary>
    public BindButtonViewModel BindLeftTrigger => _bindLookup[PadTarget.LeftTrigger];
    /// <summary>Right trigger binding.</summary>
    public BindButtonViewModel BindRightTrigger => _bindLookup[PadTarget.RightTrigger];
    /// <summary>D-Pad up binding.</summary>
    public BindButtonViewModel BindDPadUp => _bindLookup[PadTarget.DPadUp];
    /// <summary>D-Pad down binding.</summary>
    public BindButtonViewModel BindDPadDown => _bindLookup[PadTarget.DPadDown];
    /// <summary>D-Pad left binding.</summary>
    public BindButtonViewModel BindDPadLeft => _bindLookup[PadTarget.DPadLeft];
    /// <summary>D-Pad right binding.</summary>
    public BindButtonViewModel BindDPadRight => _bindLookup[PadTarget.DPadRight];
    /// <summary>Left stick up binding.</summary>
    public BindButtonViewModel BindLStickUp => _bindLookup[PadTarget.LStickUp];
    /// <summary>Left stick down binding.</summary>
    public BindButtonViewModel BindLStickDown => _bindLookup[PadTarget.LStickDown];
    /// <summary>Left stick left binding.</summary>
    public BindButtonViewModel BindLStickLeft => _bindLookup[PadTarget.LStickLeft];
    /// <summary>Left stick right binding.</summary>
    public BindButtonViewModel BindLStickRight => _bindLookup[PadTarget.LStickRight];
    /// <summary>Left stick click binding.</summary>
    public BindButtonViewModel BindLStickPress => _bindLookup[PadTarget.LStickPress];
    /// <summary>Left stick modifier binding.</summary>
    public BindButtonViewModel BindLStickModifier => _bindLookup[PadTarget.LStickModifier];
    /// <summary>Right stick up binding.</summary>
    public BindButtonViewModel BindRStickUp => _bindLookup[PadTarget.RStickUp];
    /// <summary>Right stick down binding.</summary>
    public BindButtonViewModel BindRStickDown => _bindLookup[PadTarget.RStickDown];
    /// <summary>Right stick left binding.</summary>
    public BindButtonViewModel BindRStickLeft => _bindLookup[PadTarget.RStickLeft];
    /// <summary>Right stick right binding.</summary>
    public BindButtonViewModel BindRStickRight => _bindLookup[PadTarget.RStickRight];
    /// <summary>Right stick click binding.</summary>
    public BindButtonViewModel BindRStickPress => _bindLookup[PadTarget.RStickPress];
    /// <summary>Right stick modifier binding.</summary>
    public BindButtonViewModel BindRStickModifier => _bindLookup[PadTarget.RStickModifier];
    /// <summary>Back button binding.</summary>
    public BindButtonViewModel BindBack => _bindLookup[PadTarget.Back];
    /// <summary>Start button binding.</summary>
    public BindButtonViewModel BindStart => _bindLookup[PadTarget.Start];
    /// <summary>Guide button binding.</summary>
    public BindButtonViewModel BindGuide => _bindLookup[PadTarget.Guide];

    /// <summary>Copies values out of the runtime mapping into the bound properties.</summary>
    public void PullFromMapping()
    {
        _suppressPropagation = true;

        try
        {
            IsEnabled = Mapping.Enabled;
            OutputType = Mapping.OutputType;
            EmulateStickWithDpad = Mapping.EmulateStickWithDpad;
            VibrationEnabled = Mapping.Vibration.Enabled;
            VibrationStrength = Mapping.Vibration.Strength;
            LeftDeadzone = Mapping.LeftStick.Deadzone;
            LeftRange = Mapping.LeftStick.Range;
            RightDeadzone = Mapping.RightStick.Deadzone;
            RightRange = Mapping.RightStick.Range;

            SelectedDevice = Devices.FirstOrDefault(d => d.Id == Mapping.Device)
                             ?? Devices.FirstOrDefault();
        }
        finally
        {
            _suppressPropagation = false;
        }

        RefreshAllBinds();
        UpdateStatusText();
    }

    /// <summary>Rebuilds the device picker from the currently connected devices.</summary>
    /// <remarks>
    /// The entire rebuild is guarded, not just the final re-selection. Devices is the ItemsSource
    /// of a ComboBox whose SelectedItem is bound two-way, and clearing the collection makes the
    /// control coerce its own selection to null and push that null straight back into
    /// SelectedDevice. With the guard raised only around the last assignment, that null reached
    /// OnSelectedDeviceChanged, wiped Mapping.Device and tore the pad down - and because the
    /// re-selection was suppressed, nothing ever put the device back. Auto Map therefore looked
    /// applied, with the tick set and every bind filled in, while ApplyMappings saw a null device
    /// and created no pad at all: no connect sound, no XInput slot.
    /// </remarks>
    public void RefreshDevices()
    {
        var previous = Mapping.Device;

        _suppressPropagation = true;

        try
        {
            Devices.Clear();
            Devices.Add(new DeviceOption(null, Strings.Get("player.any")));

            // A single combined entry rather than separate keyboard and mouse ones: a player slot
            // holds exactly one device, so splitting them would make it impossible to use both at
            // once. When no mouse is attached its axes simply stay at rest and only the keys do
            // anything.
            if (_state.KeyboardMouse is not null)
            {
                Devices.Add(new DeviceOption(DeviceId.Keyboard, Strings.Get("player.keyboardMouse")));
            }

            // The ordinal in DisplayName counts within one GUID, so two sticks on the same adapter can
            // both be the first of their own kind and read identically in the picker. Numbering is done
            // here instead, where the whole list is visible: a name that occurs once is shown bare, and
            // one that repeats is numbered in a deterministic order so the label stays attached to the
            // same stick for as long as it is plugged in.
            var connected = _state.Input.Devices
                .OrderBy(d => d.Name, StringComparer.CurrentCulture)
                .ThenBy(d => d.Id.Port)
                .ThenBy(d => d.Id.Guid, StringComparer.Ordinal)
                .ToList();

            var occurrences = connected
                .GroupBy(d => d.Name, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

            var seen = new Dictionary<string, int>(StringComparer.Ordinal);

            foreach (var device in connected)
            {
                var label = device.Name;

                if (occurrences[device.Name] > 1)
                {
                    var ordinal = seen.TryGetValue(device.Name, out var lastOrdinal) ? lastOrdinal + 1 : 1;
                    seen[device.Name] = ordinal;
                    label = $"{device.Name} #{ordinal.ToString(CultureInfo.InvariantCulture)}";
                }

                Devices.Add(new DeviceOption(device.Id, label));
            }

            // Keep a saved-but-absent device visible so the user understands why nothing works.
            if (previous is not null && Devices.All(d => d.Id != previous))
            {
                var label = Mapping.DeviceName is null
                    ? previous.ToString()
                    : $"{Mapping.DeviceName} ({Strings.Get("player.notConnected")})";

                Devices.Add(new DeviceOption(previous, label));
            }

            SelectedDevice = Devices.FirstOrDefault(d => d.Id == previous) ?? Devices[0];
        }
        finally
        {
            _suppressPropagation = false;
        }

        UpdateStatusText();
    }

    /// <summary>Refreshes every bind button caption.</summary>
    public void RefreshAllBinds()
    {
        foreach (var bind in _bindLookup.Values)
        {
            bind.Refresh();
        }
    }

    /// <summary>Called by a bind button when a capture assigned a device to an empty player.</summary>
    public void AdoptDevice(DeviceId device)
    {
        Mapping.Device = device;
        Mapping.DeviceName = _state.Input.FindDevice(device)?.Name;
        RefreshDevices();
    }

    /// <summary>Propagates a binding or setting change to the output manager.</summary>
    public void NotifyMappingChanged()
    {
        if (_suppressPropagation)
        {
            return;
        }

        RequestApplyOutput();
    }

    /// <summary>
    /// Requests an output apply, collapsing requests that arrive within
    /// <see cref="ApplyCoalesceMs"/> of each other into one.
    /// </summary>
    private void RequestApplyOutput()
    {
        var previous = _pendingApply;
        _pendingApply = new CancellationTokenSource();

        previous?.Cancel();
        previous?.Dispose();

        _ = ApplyOutputAsync(ApplyCoalesceMs, force: false, _pendingApply.Token);
    }

    /// <summary>Applies the output immediately, for an action the user asked for explicitly.</summary>
    private Task ApplyOutputNowAsync(bool force = false)
    {
        _pendingApply?.Cancel();
        return ApplyOutputAsync(0, force, CancellationToken.None);
    }

    /// <summary>
    /// Pushes the current state into the output manager off the UI thread, then refreshes the
    /// captions.
    /// </summary>
    /// <remarks>
    /// ApplyOutput staggers pad connections and waits for each virtual pad to be claimed by the
    /// input side, and the cloaking step blocks on an external process; both are documented as not
    /// callable from the UI thread. The slot caption is refreshed twice - once when the pads are in
    /// place and once after a short delay - because Windows only reports the XInput index some tens
    /// of milliseconds after Connect() returns.
    /// </remarks>
    private async Task ApplyOutputAsync(int delayMs, bool force, CancellationToken token)
    {
        try
        {
            if (delayMs > 0)
            {
                await Task.Delay(delayMs, token);
            }

            var applied = await Task.Run(() => _state.ApplyOutput(force), token);

            RefreshStatus();

            if (!applied)
            {
                // Nothing was touched, so no slot number can have changed either.
                return;
            }

            await Task.Delay(SlotResolveDelayMs, token);

            _state.Output.RefreshUserIndices();
            RefreshStatus();
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer request, which reports for both.
        }
        catch (Exception ex)
        {
            _state.ReportStatus("msg.applyFailed", ex.Message, $"Apply failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Runs an edit that rewrites this player's bindings with the slot detached from the poll loop.
    /// </summary>
    /// <remarks>
    /// The mapping engine reads the binding dictionary on the polling thread, so rewriting it from
    /// the interface without detaching the slot first can be observed mid-resize.
    /// </remarks>
    public void EditMapping(Action edit)
    {
        using (_state.Output.BeginMappingEdit(Mapping.Index))
        {
            edit();
        }
    }

    // ---- Property change handlers push edits into the runtime mapping ----

    partial void OnIsEnabledChanged(bool value)
    {
        // The comparison is the point: a recycled CheckBox writes its stale IsChecked into this
        // freshly attached view model and the correct one straight after, and without this guard
        // that pair disposed the pad and rebuilt it - the connect sound heard on returning from an
        // inactive player's tab to an active one.
        if (_suppressPropagation || Mapping.Enabled == value)
        {
            return;
        }

        Mapping.Enabled = value;
        UpdateStatusText();
        RequestApplyOutput();
    }

    partial void OnSelectedDeviceChanged(DeviceOption? value)
    {
        if (_suppressPropagation)
        {
            return;
        }

        // A null selection is never something the user can pick: the "Any / none" entry is a real
        // DeviceOption whose Id happens to be null. A null therefore only ever arrives from the
        // ComboBox coercing its own selection - while its items are being rebuilt, or while the
        // tab host swaps this view's data context and the previously selected item is briefly not
        // in the new list - and acting on it unplugs a pad that was working. That is what made the
        // connect sound replay, with a short delay, every time an active player's tab was
        // revisited. The control writes the correct value immediately afterwards, so dropping this
        // one notification loses nothing.
        if (value is null)
        {
            return;
        }

        var newDevice = value.Id;
        if (Mapping.Device == newDevice)
        {
            return;
        }

        // Re-point every existing binding at the new device so the user does not have to redo them.
        var oldDevice = Mapping.Device;
        Mapping.Device = newDevice;
        Mapping.DeviceName = newDevice is null ? null : _state.Input.FindDevice(newDevice)?.Name;

        if (oldDevice is not null && newDevice is not null)
        {
            foreach (var binding in Mapping.Bindings.Values)
            {
                if (binding.Device == oldDevice)
                {
                    binding.Device = newDevice;
                }
            }
        }

        RefreshAllBinds();
        UpdateStatusText();
        RequestApplyOutput();
    }

    partial void OnOutputTypeChanged(VirtualPadType value)
    {
        if (_suppressPropagation || Mapping.OutputType == value)
        {
            return;
        }

        Mapping.OutputType = value;
        UpdateStatusText();
        RequestApplyOutput();
    }

    partial void OnEmulateStickWithDpadChanged(bool value)
    {
        if (!_suppressPropagation)
        {
            Mapping.EmulateStickWithDpad = value;
        }
    }

    partial void OnVibrationEnabledChanged(bool value)
    {
        if (!_suppressPropagation)
        {
            Mapping.Vibration.Enabled = value;
        }
    }

    partial void OnVibrationStrengthChanged(int value)
    {
        if (!_suppressPropagation)
        {
            Mapping.Vibration.Strength = Math.Clamp(value, 0, 100);
        }
    }

    partial void OnLeftDeadzoneChanged(float value)
    {
        if (!_suppressPropagation)
        {
            Mapping.LeftStick.Deadzone = Math.Clamp(value, 0f, 0.95f);
        }
    }

    partial void OnLeftRangeChanged(float value)
    {
        if (!_suppressPropagation)
        {
            Mapping.LeftStick.Range = Math.Clamp(value, 0.1f, 1.5f);
        }
    }

    partial void OnRightDeadzoneChanged(float value)
    {
        if (!_suppressPropagation)
        {
            Mapping.RightStick.Deadzone = Math.Clamp(value, 0f, 0.95f);
        }
    }

    partial void OnRightRangeChanged(float value)
    {
        if (!_suppressPropagation)
        {
            Mapping.RightStick.Range = Math.Clamp(value, 0.1f, 1.5f);
        }
    }

    /// <summary>Runs automatic mapping against the selected device.</summary>
    /// <remarks>
    /// The generated command is still called AutoMapCommand: the source generator drops the Async
    /// suffix, so the XAML binding is unaffected.
    /// </remarks>
    [RelayCommand]
    private async Task AutoMapAsync()
    {
        var device = _state.Input.FindDevice(Mapping.Device);
        if (device is null)
        {
            // No device chosen yet: use the first connected one so the button is never a dead end.
            device = _state.Input.Devices.FirstOrDefault();
            if (device is null)
            {
                _state.ReportStatus("msg.noDevice", null, "No device connected.");
                return;
            }
        }

        // AutoMapper rewrites the whole binding dictionary, which the poll loop reads.
        AutoMapResult result;
        using (_state.Output.BeginMappingEdit(Mapping.Index))
        {
            result = AutoMapper.Apply(Mapping, device);
        }

        Mapping.Enabled = true;

        // The list is rebuilt before the values are pulled, not after: PullFromMapping resolves
        // SelectedDevice against Devices, and a device auto-mapping just assigned is not in that
        // collection yet. The old order left the picker showing the wrong entry until the next
        // refresh.
        RefreshDevices();
        PullFromMapping();

        // The core layer reports which path it took rather than a finished sentence, because it
        // has no string table; its own Message is English and goes to the log. Reporting the key
        // rather than the text lets the line follow a later language change.
        var key = result.Outcome switch
        {
            AutoMapOutcome.KeyboardMouse => "msg.autoMapKeyboard",
            AutoMapOutcome.SdlDatabase => "msg.autoMapSdl",
            AutoMapOutcome.Guessed => "msg.autoMapGuessed",
            _ => "msg.autoMapFailed",
        };

        // The keyboard layout message names no device, so it is shown on its own.
        var detail = result.Outcome == AutoMapOutcome.KeyboardMouse ? null : result.DeviceName;
        _state.ReportStatus(key, detail, result.Message);

        await ApplyOutputNowAsync();
    }

    /// <summary>Clears every binding of this player and returns its settings to factory values.</summary>
    /// <remarks>
    /// Clearing the bindings alone left a half-reset player: the sliders, the rumble setting and
    /// stick emulation kept whatever they had been changed to, so "clear all" did not actually
    /// return the tab to the state it started in. Both halves happen inside one edit session, so
    /// the poll loop never observes a player with its bindings gone but its tuning not yet reset.
    /// </remarks>
    [RelayCommand]
    private void ClearAll()
    {
        EditMapping(() =>
        {
            Mapping.ClearBindings();
            Mapping.RestoreDefaults();
        });

        // Pulls rather than only refreshing the binds: the tuning values changed too, and they are
        // bound to the sliders and their captions.
        PullFromMapping();
        RequestApplyOutput();
    }

    /// <summary>Restores the default dead zone and range of both sticks.</summary>
    /// <remarks>
    /// Deliberately narrower than its name once suggested. It used to reset vibration, vibration
    /// strength and stick emulation as well, none of which the button names, so someone correcting
    /// a dead zone had their rumble configuration quietly changed underneath them. The full reset
    /// lives on Clear All.
    /// </remarks>
    [RelayCommand]
    private void RestoreDefaults()
    {
        LeftDeadzone = StickSettings.DefaultDeadzone;
        LeftRange = StickSettings.DefaultRange;
        RightDeadzone = StickSettings.DefaultDeadzone;
        RightRange = StickSettings.DefaultRange;
    }

    /// <summary>Buzzes the assigned controller so the user can tell which one this player is.</summary>
    [RelayCommand]
    private void Identify() => _state.Output.IdentifyPlayer(Mapping.Index);

    /// <summary>Refreshes the device list on demand.</summary>
    [RelayCommand]
    private void Refresh()
    {
        _state.Input.EnumerateDevices();
        RefreshDevices();
    }

    /// <summary>
    /// Pulls the emulated pad state and updates every live preview property. Called from the 60 Hz
    /// dispatcher timer, never from the polling thread.
    /// </summary>
    public void UpdateLivePreview()
    {
        var state = _state.Output.GetState(Mapping.Index);

        LeftStickX = state.LeftThumbX / 32767.0;
        LeftStickY = state.LeftThumbY / 32767.0;
        RightStickX = state.RightThumbX / 32767.0;
        RightStickY = state.RightThumbY / 32767.0;
        LeftTriggerFill = state.LeftTrigger / 255.0;
        RightTriggerFill = state.RightTrigger / 255.0;

        foreach (var bind in _bindLookup.Values)
        {
            bind.UpdateLiveState(in state);
        }
        PreviewStateUpdated?.Invoke(in state);
    }

    private void UpdateStatusText()
    {
        var device = _state.Input.FindDevice(Mapping.Device);

        DeviceStatus = device switch
        {
            // The synthetic device has no meaningful axis or button count to report, so it gets a
            // plain caption instead of the hardware capability summary.
            { IsConnected: true } when device.Id.IsSynthetic => device.Name,
            { IsConnected: true } => $"{device.Name}  {device.CapabilitySummary}",
            null when Mapping.Device is not null =>
                $"{Mapping.DeviceName ?? Mapping.Device.ToString()}  {Strings.Get("player.notConnected")}",
            _ => string.Empty,
        };

        var userIndex = _state.Output.GetUserIndex(Mapping.Index);
        SlotStatus = Mapping.OutputType == VirtualPadType.Xbox360 && userIndex is not null
            ? $"{Strings.Get("status.xinputSlot")} {userIndex + 1}"
            : string.Empty;
    }

    /// <summary>Refreshes the status strings; called when output slots may have changed.</summary>
    public void RefreshStatus() => UpdateStatusText();
}
