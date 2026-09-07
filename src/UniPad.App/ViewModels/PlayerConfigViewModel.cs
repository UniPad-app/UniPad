using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UniPad.App.Localization;
using UniPad.App.Services;
using UniPad.Core.Input;
using UniPad.Core.Mapping;
using UniPad.Core.Output;

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
    private float _leftDeadzone;

    [ObservableProperty]
    private float _leftRange;

    [ObservableProperty]
    private float _rightDeadzone;

    [ObservableProperty]
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

        PullFromMapping();
        RefreshDevices();
    }

    /// <summary>The underlying runtime mapping this view model edits in place.</summary>
    public PlayerMapping Mapping { get; }

    /// <summary>One-based player number used in captions.</summary>
    public int PlayerNumber => Mapping.Index + 1;

    /// <summary>Tab header text.</summary>
    public string Header => $"{Strings.Get("tab.player")} {PlayerNumber}";

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
    public void RefreshDevices()
    {
        var previous = Mapping.Device;

        Devices.Clear();
        Devices.Add(new DeviceOption(null, Strings.Get("player.any")));

        foreach (var device in _state.Input.Devices.OrderBy(d => d.Name).ThenBy(d => d.Id.Port))
        {
            Devices.Add(new DeviceOption(device.Id, device.DisplayName));
        }

        // Keep a saved-but-absent device visible so the user understands why nothing works.
        if (previous is not null && Devices.All(d => d.Id != previous))
        {
            var label = Mapping.DeviceName is null
                ? previous.ToString()
                : $"{Mapping.DeviceName} ({Strings.Get("player.notConnected")})";

            Devices.Add(new DeviceOption(previous, label));
        }

        _suppressPropagation = true;
        SelectedDevice = Devices.FirstOrDefault(d => d.Id == previous) ?? Devices[0];
        _suppressPropagation = false;

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

        _state.ApplyMappings();
    }

    // ---- Property change handlers push edits into the runtime mapping ----

    partial void OnIsEnabledChanged(bool value)
    {
        if (_suppressPropagation)
        {
            return;
        }

        Mapping.Enabled = value;
        _state.ApplyMappings();
        _state.ApplyCloaking();
        UpdateStatusText();
    }

    partial void OnSelectedDeviceChanged(DeviceOption? value)
    {
        if (_suppressPropagation)
        {
            return;
        }

        var newDevice = value?.Id;
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
        _state.ApplyMappings();
        _state.ApplyCloaking();
        UpdateStatusText();
    }

    partial void OnOutputTypeChanged(VirtualPadType value)
    {
        if (_suppressPropagation)
        {
            return;
        }

        Mapping.OutputType = value;
        _state.ApplyMappings();
        UpdateStatusText();
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
    [RelayCommand]
    private void AutoMap()
    {
        var device = _state.Input.FindDevice(Mapping.Device);
        if (device is null)
        {
            // No device chosen yet: use the first connected one so the button is never a dead end.
            device = _state.Input.Devices.FirstOrDefault();
            if (device is null)
            {
                _state.ReportStatus(Strings.Get("msg.noDevice"));
                return;
            }
        }

        var result = AutoMapper.Apply(Mapping, device);
        Mapping.Enabled = true;

        PullFromMapping();
        RefreshDevices();
        _state.ApplyMappings();
        _state.ApplyCloaking();
        _state.ReportStatus(result.Message);
    }

    /// <summary>Clears every binding of this player.</summary>
    [RelayCommand]
    private void ClearAll()
    {
        Mapping.ClearBindings();
        RefreshAllBinds();
        _state.ApplyMappings();
    }

    /// <summary>Restores default tuning values without touching the bindings.</summary>
    [RelayCommand]
    private void RestoreDefaults()
    {
        LeftDeadzone = 0.15f;
        LeftRange = 0.95f;
        RightDeadzone = 0.15f;
        RightRange = 0.95f;
        VibrationEnabled = true;
        VibrationStrength = 100;
        EmulateStickWithDpad = true;
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
            { IsConnected: true } => $"{device.Name} — {device.CapabilitySummary}",
            null when Mapping.Device is not null => $"{Mapping.DeviceName ?? Mapping.Device.ToString()} — {Strings.Get("player.notConnected")}",
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
