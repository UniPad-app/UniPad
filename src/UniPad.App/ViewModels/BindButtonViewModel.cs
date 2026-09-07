using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UniPad.App.Localization;
using UniPad.App.Services;
using UniPad.Core.Input;
using UniPad.Core.Mapping;

namespace UniPad.App.ViewModels;

/// <summary>
/// Drives one bind button: its caption, its capture state and its context menu actions.
/// <para>
/// Interaction matches yuzu: left click starts a five second capture, right click opens the option
/// menu, middle click clears instantly.
/// </para>
/// </summary>
public sealed partial class BindButtonViewModel : ViewModelBase
{
    private readonly PlayerConfigViewModel _owner;
    private readonly BindCaptureService _capture;

    [ObservableProperty]
    private string _displayText = Strings.Get("bind.notSet");

    [ObservableProperty]
    private bool _isCapturing;

    /// <summary>True while the physical source of this binding is actively pressed.</summary>
    [ObservableProperty]
    private bool _isActive;

    [ObservableProperty]
    private bool _isInverted;

    [ObservableProperty]
    private bool _isToggle;

    [ObservableProperty]
    private float _threshold = 0.5f;

    /// <summary>Creates a bind button view model for one logical output.</summary>
    public BindButtonViewModel(PlayerConfigViewModel owner, PadTarget target, BindCaptureService capture)
    {
        _owner = owner;
        _capture = capture;
        Target = target;
        Label = target.ToLabel();
        Refresh();
    }

    /// <summary>Which logical output this button binds.</summary>
    public PadTarget Target { get; }

    /// <summary>Static label shown next to the button.</summary>
    public string Label { get; }

    /// <summary>True when the target is an axis-capable source, enabling the invert menu item.</summary>
    public bool SupportsInvert => _owner.Mapping.GetBinding(Target).Type == BindingSourceType.Axis;

    /// <summary>Refreshes the caption and option flags from the underlying mapping.</summary>
    public void Refresh()
    {
        var binding = _owner.Mapping.GetBinding(Target);

        DisplayText = binding.IsBound
            ? binding.ToDisplayString()
            : Strings.Get("bind.notSet");

        IsInverted = binding.Invert;
        IsToggle = binding.Toggle;
        Threshold = binding.Threshold;

        OnPropertyChanged(nameof(SupportsInvert));
    }

    /// <summary>Starts a capture session and stores whatever the user presses.</summary>
    [RelayCommand]
    private async Task BeginCaptureAsync()
    {
        if (IsCapturing)
        {
            _capture.Cancel();
            return;
        }

        IsCapturing = true;
        DisplayText = $"{Strings.Get("bind.pressKey")} 5";

        void OnTick(int remaining) => DisplayText = $"{Strings.Get("bind.pressKey")} {remaining}";

        _capture.CountdownTick += OnTick;

        try
        {
            // Restricting to the player's own device avoids a drifting second controller stealing
            // the capture. When no device is assigned yet, any device is accepted.
            var result = await _capture.CaptureAsync(_owner.Mapping.Device);

            if (result.Binding is not null)
            {
                ApplyCapturedBinding(result.Binding);
            }
        }
        finally
        {
            _capture.CountdownTick -= OnTick;
            IsCapturing = false;
            Refresh();
        }
    }

    /// <summary>
    /// Stores a captured binding, and adopts the device as the player's device when none was set.
    /// </summary>
    private void ApplyCapturedBinding(InputBinding binding)
    {
        // Preserve the user's existing option flags across a re-bind.
        binding.Invert = IsInverted && binding.Type == BindingSourceType.Axis;
        binding.Toggle = IsToggle;
        binding.Threshold = Threshold;

        _owner.Mapping.SetBinding(Target, binding);

        if (_owner.Mapping.Device is null && binding.Device is not null && !binding.Device.IsSynthetic)
        {
            _owner.AdoptDevice(binding.Device);
        }

        _owner.NotifyMappingChanged();
    }

    /// <summary>Removes the binding.</summary>
    [RelayCommand]
    private void Clear()
    {
        _owner.Mapping.SetBinding(Target, null);
        _owner.NotifyMappingChanged();
        Refresh();
    }

    /// <summary>Flips the sign of an analogue source.</summary>
    [RelayCommand]
    private void ToggleInvert()
    {
        var binding = _owner.Mapping.GetBinding(Target);
        if (!binding.IsBound || binding.Type != BindingSourceType.Axis)
        {
            return;
        }

        binding.Invert = !binding.Invert;
        _owner.NotifyMappingChanged();
        Refresh();
    }

    /// <summary>Switches the source between momentary and latching behaviour.</summary>
    [RelayCommand]
    private void ToggleLatch()
    {
        var binding = _owner.Mapping.GetBinding(Target);
        if (!binding.IsBound)
        {
            return;
        }

        binding.Toggle = !binding.Toggle;
        _owner.NotifyMappingChanged();
        Refresh();
    }

    /// <summary>Cycles the axis-to-button threshold through a few useful values.</summary>
    [RelayCommand]
    private void CycleThreshold()
    {
        var binding = _owner.Mapping.GetBinding(Target);
        if (!binding.IsBound || binding.Type != BindingSourceType.Axis)
        {
            return;
        }

        // A cycling menu item is far quicker in practice than opening a dialog for one number.
        binding.Threshold = binding.Threshold switch
        {
            < 0.2f => 0.25f,
            < 0.3f => 0.5f,
            < 0.6f => 0.75f,
            < 0.8f => 0.9f,
            _ => 0.15f,
        };

        _owner.NotifyMappingChanged();
        Refresh();
    }

    /// <summary>Updates the live "currently pressed" highlight. Called from the 60 Hz UI timer.</summary>
    public void UpdateLiveState(in PadState state)
    {
        IsActive = Target switch
        {
            PadTarget.A => state.A,
            PadTarget.B => state.B,
            PadTarget.X => state.X,
            PadTarget.Y => state.Y,
            PadTarget.LeftBumper => state.LeftBumper,
            PadTarget.RightBumper => state.RightBumper,
            PadTarget.LeftTrigger => state.LeftTrigger > 32,
            PadTarget.RightTrigger => state.RightTrigger > 32,
            PadTarget.DPadUp => state.DPadUp,
            PadTarget.DPadDown => state.DPadDown,
            PadTarget.DPadLeft => state.DPadLeft,
            PadTarget.DPadRight => state.DPadRight,
            PadTarget.LStickPress => state.LeftThumb,
            PadTarget.RStickPress => state.RightThumb,
            PadTarget.LStickUp => state.LeftThumbY > 8000,
            PadTarget.LStickDown => state.LeftThumbY < -8000,
            PadTarget.LStickLeft => state.LeftThumbX < -8000,
            PadTarget.LStickRight => state.LeftThumbX > 8000,
            PadTarget.RStickUp => state.RightThumbY > 8000,
            PadTarget.RStickDown => state.RightThumbY < -8000,
            PadTarget.RStickLeft => state.RightThumbX < -8000,
            PadTarget.RStickRight => state.RightThumbX > 8000,
            PadTarget.Back => state.Back,
            PadTarget.Start => state.Start,
            PadTarget.Guide => state.Guide,
            _ => false,
        };
    }
}
