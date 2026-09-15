using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UniPad.App.Localization;
using UniPad.App.Services;
using UniPad.Core.SystemServices;

namespace UniPad.App.ViewModels;

/// <summary>One row in the diagnostics device list.</summary>
public sealed partial class DeviceDiagnosticsRow : ViewModelBase
{
    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private string _identifier = string.Empty;

    [ObservableProperty]
    private string _capabilities = string.Empty;

    [ObservableProperty]
    private string _axisValues = string.Empty;

    [ObservableProperty]
    private string _buttonValues = string.Empty;

    [ObservableProperty]
    private string _hatValues = string.Empty;
}

/// <summary>
/// View model for the Advanced page: global options, driver status, keyboard and mouse tuning, and
/// the raw device monitor that makes diagnosing an unknown controller possible.
/// <para>
/// Data folder, update and maintenance actions deliberately live on the About page instead: this
/// page is for settings that change how input is read and emitted.
/// </para>
/// </summary>
public sealed partial class AdvancedViewModel : ViewModelBase
{
    private readonly AppState _state;

    [ObservableProperty]
    private bool _hidePhysicalControllers;

    [ObservableProperty]
    private bool _startMinimizedToTray;

    [ObservableProperty]
    private bool _minimizeToTrayOnClose;

    [ObservableProperty]
    private bool _runAtStartup;

    [ObservableProperty]
    private bool _verboseLogging;

    [ObservableProperty]
    private bool _outputEnabled;

    [ObservableProperty]
    private int _selectedPollRate;

    [ObservableProperty]
    private string _selectedTheme = "Dark";

    [ObservableProperty]
    private string _selectedLanguage = "en";

    [ObservableProperty]
    private string _driverStatus = string.Empty;

    [ObservableProperty]
    private string _hidHideStatus = string.Empty;

    [ObservableProperty]
    private bool _isDriverMissing;

    [ObservableProperty]
    private bool _isHidHideMissing;

    [ObservableProperty]
    private string _pollStatistics = string.Empty;

    [ObservableProperty]
    private bool _keyboardMouseEnabled;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MouseSensitivityText))]
    private double _mouseSensitivity = 1.0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MouseReturnSpeedText))]
    private double _mouseReturnSpeed = 0.08;

    [ObservableProperty]
    private bool _mouseInvertY;

    private bool _suppress;

    /// <summary>Creates the advanced settings view model.</summary>
    public AdvancedViewModel(AppState state)
    {
        _state = state;

        _suppress = true;
        HidePhysicalControllers = state.Config.HidePhysicalControllers;
        StartMinimizedToTray = state.Config.StartMinimizedToTray;
        MinimizeToTrayOnClose = state.Config.MinimizeToTrayOnClose;
        RunAtStartup = StartupRegistration.IsRegistered();
        VerboseLogging = state.Config.VerboseLogging;
        OutputEnabled = state.Output.Enabled;
        SelectedPollRate = state.Input.PollRateHz;
        SelectedTheme = state.Config.Theme;
        SelectedLanguage = state.Config.Language;
        KeyboardMouseEnabled = state.Config.KeyboardMouseEnabled;
        MouseSensitivity = state.Config.MouseSensitivity;
        MouseReturnSpeed = state.Config.MouseReturnSpeed;
        MouseInvertY = state.Config.MouseInvertY;
        _suppress = false;

        RefreshDriverStatus();
    }

    /// <summary>Selectable polling rates.</summary>
    public int[] PollRates { get; } = [125, 250, 500, 1000];

    /// <summary>Selectable themes.</summary>
    public string[] Themes { get; } = ["Dark", "Light"];

    /// <summary>Selectable language codes.</summary>
    public string[] Languages { get; } = ["en", "fa"];

    /// <summary>Live raw values for every connected device.</summary>
    public ObservableCollection<DeviceDiagnosticsRow> DeviceRows { get; } = [];

    /// <summary>Sensitivity formatted for the caption next to its slider.</summary>
    public string MouseSensitivityText => MouseSensitivity.ToString("F2");

    /// <summary>Return time formatted for the caption next to its slider.</summary>
    public string MouseReturnSpeedText => $"{MouseReturnSpeed:F2} s";

    /// <summary>Re-reads driver installation state.</summary>
    public void RefreshDriverStatus()
    {
        var vigem = DriverBootstrapper.GetViGEmState();
        IsDriverMissing = vigem == DriverState.Missing || !_state.Output.IsDriverAvailable;

        DriverStatus = vigem switch
        {
            DriverState.Installed when _state.Output.IsDriverAvailable => "ViGEmBus: installed and connected",
            DriverState.Installed => $"ViGEmBus: installed but unreachable ({_state.Output.DriverError})",
            DriverState.Missing => "ViGEmBus: not installed",
            _ => "ViGEmBus: state unknown",
        };

        var hidHide = DriverBootstrapper.GetHidHideState();
        IsHidHideMissing = hidHide != DriverState.Installed;
        HidHideStatus = hidHide switch
        {
            DriverState.Installed => $"HidHide: installed ({_state.HidHide.HiddenPaths.Count} device(s) hidden)",
            DriverState.Missing => "HidHide: not installed (optional but recommended)",
            _ => "HidHide: state unknown",
        };
    }

    /// <summary>
    /// Installs ViGEmBus: from the bundled MSI when this build has one, otherwise by downloading
    /// the pinned official release. The user never has to locate the driver themselves.
    /// </summary>
    [RelayCommand]
    private async Task InstallViGEmAsync()
    {
        var progress = new Progress<string>(_state.ReportStatus);

        var result = await DriverBootstrapper.EnsureInstalledAsync(
            DriverBootstrapper.ViGEmResourceName,
            DriverBootstrapper.ViGEmDownloadUrl,
            "ViGEmBus",
            progress).ConfigureAwait(true);

        _state.ReportStatus(result.Message);

        if (result.Succeeded)
        {
            _state.Output.TryInitialiseDriver();
            _state.ApplyMappings();
            RefreshDriverStatus();
        }
    }

    /// <summary>Installs HidHide, downloading the official release when nothing is bundled.</summary>
    [RelayCommand]
    private async Task InstallHidHideAsync()
    {
        var progress = new Progress<string>(_state.ReportStatus);

        var result = await DriverBootstrapper.EnsureInstalledAsync(
            DriverBootstrapper.HidHideResourceName,
            DriverBootstrapper.HidHideDownloadUrl,
            "HidHide",
            progress).ConfigureAwait(true);

        _state.ReportStatus(result.Message);

        if (result.Succeeded)
        {
            _state.HidHide.Refresh();
            _state.ApplyCloaking();
            RefreshDriverStatus();
        }
    }

    partial void OnHidePhysicalControllersChanged(bool value)
    {
        if (_suppress)
        {
            return;
        }

        _state.Config.HidePhysicalControllers = value;
        _state.ApplyCloaking();
        RefreshDriverStatus();
    }

    partial void OnStartMinimizedToTrayChanged(bool value)
    {
        if (!_suppress)
        {
            _state.Config.StartMinimizedToTray = value;
        }
    }

    partial void OnMinimizeToTrayOnCloseChanged(bool value)
    {
        if (!_suppress)
        {
            _state.Config.MinimizeToTrayOnClose = value;
        }
    }

    partial void OnRunAtStartupChanged(bool value)
    {
        if (_suppress)
        {
            return;
        }

        if (!StartupRegistration.SetRegistered(value))
        {
            _state.ReportStatus("Could not update the Windows startup entry.");
        }

        _state.Config.RunAtStartup = value;
    }

    partial void OnVerboseLoggingChanged(bool value)
    {
        if (_suppress)
        {
            return;
        }

        _state.Config.VerboseLogging = value;
        _state.ReportStatus("Log level changes take effect after a restart.");
    }

    partial void OnOutputEnabledChanged(bool value)
    {
        if (!_suppress)
        {
            _state.Output.Enabled = value;
        }
    }

    partial void OnSelectedPollRateChanged(int value)
    {
        if (_suppress)
        {
            return;
        }

        _state.Input.PollRateHz = value;
        _state.Config.PollRateHz = value;
    }

    partial void OnSelectedThemeChanged(string value)
    {
        if (_suppress)
        {
            return;
        }

        _state.Config.Theme = value;
        ThemeRequested?.Invoke(value);
    }

    partial void OnSelectedLanguageChanged(string value)
    {
        if (_suppress)
        {
            return;
        }

        _state.Config.Language = value;
        Strings.Instance.Language = value;
    }

    /// <summary>
    /// The raw-input sink is created at startup, so turning the source on or off only takes effect
    /// after a restart. The setting is still stored immediately.
    /// </summary>
    partial void OnKeyboardMouseEnabledChanged(bool value)
    {
        if (_suppress)
        {
            return;
        }

        _state.Config.KeyboardMouseEnabled = value;
        _state.ReportStatus(Strings.Get("msg.restartRequired"));
    }

    partial void OnMouseSensitivityChanged(double value)
    {
        if (_suppress)
        {
            return;
        }

        var clamped = (float)Math.Clamp(value, 0.1, 5.0);
        _state.Config.MouseSensitivity = clamped;

        if (_state.KeyboardMouse is { } backend)
        {
            backend.Mouse.Sensitivity = clamped;
        }
    }

    partial void OnMouseReturnSpeedChanged(double value)
    {
        if (_suppress)
        {
            return;
        }

        var clamped = (float)Math.Clamp(value, 0.02, 0.4);
        _state.Config.MouseReturnSpeed = clamped;

        if (_state.KeyboardMouse is { } backend)
        {
            backend.Mouse.ReturnSpeed = clamped;
        }
    }

    partial void OnMouseInvertYChanged(bool value)
    {
        if (_suppress)
        {
            return;
        }

        _state.Config.MouseInvertY = value;

        if (_state.KeyboardMouse is { } backend)
        {
            backend.Mouse.InvertY = value;
        }
    }

    /// <summary>Raised when the user picks a different theme.</summary>
    public event Action<string>? ThemeRequested;

    /// <summary>
    /// Refreshes the raw device monitor. Called from the UI timer at a reduced rate, since string
    /// formatting for every axis of every device is not free.
    /// </summary>
    public void UpdateDiagnostics()
    {
        var devices = _state.Input.Devices.ToList();

        // The synthetic keyboard and mouse is not an SDL device, so it is appended by hand.
        if (_state.KeyboardMouse is { IsRunning: true } keyboardMouse)
        {
            devices.Add(keyboardMouse.Device);
        }

        // Add or remove rows only when the device set actually changed.
        while (DeviceRows.Count > devices.Count)
        {
            DeviceRows.RemoveAt(DeviceRows.Count - 1);
        }

        while (DeviceRows.Count < devices.Count)
        {
            DeviceRows.Add(new DeviceDiagnosticsRow());
        }

        for (var i = 0; i < devices.Count; i++)
        {
            var device = devices[i];
            var row = DeviceRows[i];
            var snapshot = device.Snapshot;

            row.Name = device.Name;
            row.Identifier = device.Id.ToString();
            row.Capabilities = device.CapabilitySummary;

            row.AxisValues = snapshot.Axes.Length == 0
                ? "-"
                : string.Join("  ", snapshot.Axes.Select((v, idx) => $"{idx}:{v,6}"));

            var pressed = new List<string>();
            for (var b = 0; b < snapshot.Buttons.Length; b++)
            {
                if (snapshot.Buttons[b])
                {
                    pressed.Add(b.ToString());
                }
            }

            row.ButtonValues = pressed.Count == 0 ? "-" : string.Join(", ", pressed);

            row.HatValues = snapshot.Hats.Length == 0
                ? "-"
                : string.Join("  ", snapshot.Hats.Select((v, idx) => $"{idx}:0x{v:X2}"));
        }

        PollStatistics =
            $"{Strings.Get("status.pollRate")}: {_state.Input.PollRateHz} Hz  |  " +
            $"cycle {_state.Input.LastLoopMicroseconds:F0} us  |  " +
            $"{Strings.Get("status.devices")}: {devices.Count}";
    }
}
