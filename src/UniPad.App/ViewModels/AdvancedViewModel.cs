using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UniPad.App.Localization;
using UniPad.App.Services;
using UniPad.Core.SystemServices;
using System.Globalization;

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

        // The unit suffix and the paragraph direction both depend on the interface language, and
        // neither is a bound string, so they have to be nudged by hand when it changes.
        Strings.Instance.LanguageChanged += OnLanguageChanged;
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
    public string MouseReturnSpeedText => $"{MouseReturnSpeed:F2} {Strings.Get("unit.seconds")}";

    /// <summary>
    /// Reading direction for wrapped prose inside this page. The keyboard and mouse box itself
    /// stays left-to-right so its sliders keep their minimum on the left, but a Persian paragraph
    /// still needs its own base direction or the trailing punctuation lands on the wrong end of
    /// the line.
    /// </summary>
    public FlowDirection ParagraphFlowDirection =>
        Strings.Instance.IsRightToLeft ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;

    /// <summary>
    /// Re-reads driver installation state.
    /// <para>
    /// Both lines are composed from keys rather than written out, and the parenthetical detail -
    /// a driver error, a count of hidden devices - is isolated so it stays in one piece next to
    /// its sentence in a right-to-left interface.
    /// </para>
    /// </summary>
    public void RefreshDriverStatus()
    {
        var vigem = DriverBootstrapper.GetViGEmState();
        IsDriverMissing = vigem == DriverState.Missing || !_state.Output.IsDriverAvailable;

        DriverStatus = vigem switch
        {
            DriverState.Installed when _state.Output.IsDriverAvailable =>
                Strings.Get("status.vigemConnected"),
            DriverState.Installed =>
                $"{Strings.Get("status.vigemUnreachable")} {Strings.Isolate($"({_state.Output.DriverError})")}",
            DriverState.Missing => Strings.Get("status.vigemMissing"),
            _ => Strings.Get("status.vigemUnknown"),
        };

        var hidHide = DriverBootstrapper.GetHidHideState();
        IsHidHideMissing = hidHide != DriverState.Installed;

        HidHideStatus = hidHide switch
        {
            DriverState.Installed =>
                $"{Strings.Get("status.hidHideInstalled")} " +
                Strings.Isolate(
                    $"({_state.HidHide.HiddenPaths.Count.ToString(CultureInfo.InvariantCulture)} " +
                    $"{Strings.Get("status.hiddenDevices")})"),
            DriverState.Missing => Strings.Get("status.hidHideMissing"),
            _ => Strings.Get("status.hidHideUnknown"),
        };
    }

    /// <summary>
    /// Installs ViGEmBus: from the bundled MSI when this build has one, otherwise by downloading
    /// the pinned official release. The user never has to locate the driver themselves.
    /// </summary>
    [RelayCommand]
    private async Task InstallViGEmAsync()
    {
        var result = await InstallDriverAsync(
            DriverBootstrapper.ViGEmResourceName,
            DriverBootstrapper.ViGEmDownloadUrl,
            "ViGEmBus").ConfigureAwait(true);

        if (result.Succeeded)
        {
            // ApplyMappings staggers pad connections by 300 ms each and is documented as never
            // running on the UI thread; with several players enabled the window would freeze.
            await Task.Run(() =>
            {
                _state.Output.TryInitialiseDriver();
                _state.ApplyMappings();
            });

            // Back on the UI thread (the await above captures the context).
            RefreshDriverStatus();
        }
    }

    /// <summary>Installs HidHide, downloading the official release when nothing is bundled.</summary>
    [RelayCommand]
    private async Task InstallHidHideAsync()
    {
        var result = await InstallDriverAsync(
            DriverBootstrapper.HidHideResourceName,
            DriverBootstrapper.HidHideDownloadUrl,
            "HidHide").ConfigureAwait(true);

        if (result.Succeeded)
        {
            _state.HidHide.Refresh();
            _state.ApplyCloaking();
            RefreshDriverStatus();
        }
    }

    /// <summary>
    /// Runs an installation and narrates it in the status bar.
    /// <para>
    /// The bootstrapper reports a stage and an outcome rather than finished sentences, because it
    /// sits in the core and has no string table; its English text still travels along as the
    /// fallback and ends up in the log. Reporting keys means a line still on screen follows a
    /// later language change, and the driver name is isolated so it survives a right-to-left line.
    /// </para>
    /// </summary>
    private async Task<DriverInstallResult> InstallDriverAsync(
        string resourceName,
        string downloadUrl,
        string driverName)
    {
        var progress = new Progress<DriverInstallProgress>(report =>
        {
            var (key, english) = report.Phase switch
            {
                DriverInstallPhase.Downloading =>
                    ("msg.driverDownloading", $"Downloading {report.DriverName}..."),
                DriverInstallPhase.InstallingElevated =>
                    ("msg.driverInstallingElevated",
                        $"Installing {report.DriverName} (approve the Windows prompt)..."),
                _ => ("msg.driverInstalling", $"Installing {report.DriverName}..."),
            };

            _state.ReportStatus(key, report.DriverName, english);
        });

        var result = await DriverBootstrapper.EnsureInstalledAsync(
            resourceName,
            downloadUrl,
            driverName,
            progress).ConfigureAwait(true);

        var outcomeKey = result.Outcome switch
        {
            DriverInstallOutcome.Installed => "msg.driverInstalled",
            DriverInstallOutcome.InstalledRestartRequired => "msg.driverInstalledRestart",
            DriverInstallOutcome.Cancelled => "msg.driverCancelled",
            DriverInstallOutcome.NotSupported => "msg.driverNotSupported",
            DriverInstallOutcome.NotBundled => "msg.driverNotBundled",
            DriverInstallOutcome.DownloadIncomplete => "msg.driverDownloadIncomplete",
            DriverInstallOutcome.InstallerMissing => "msg.driverInstallerMissing",
            DriverInstallOutcome.InstallerTimedOut => "msg.driverTimedOut",
            _ => "msg.driverInstallFailed",
        };

        // The unsupported-host message names no driver, so it stands alone.
        string? detail = result.Outcome == DriverInstallOutcome.NotSupported
            ? null
            : result.Detail.Length == 0
                ? result.DriverName
                : $"{result.DriverName} - {result.Detail}";

        _state.ReportStatus(outcomeKey, detail, result.Message);
        return result;
    }

    partial void OnHidePhysicalControllersChanged(bool value)
    {
        if (_suppress)
        {
            return;
        }

        _state.Config.HidePhysicalControllers = value;
        _ = ApplyCloakingAsync();
    }

    /// <summary>
    /// Applies a cloaking change off the UI thread and through the output signature.
    /// <para>
    /// Two reasons not to call ApplyCloaking from here directly. It waits on an external process
    /// and may raise an elevation prompt, so doing it inline froze the window for as long as the
    /// user took to answer. And the cloak flag is part of the signature: changing it behind the
    /// signature's back left the recorded state stale, so the next hot-plug found a difference
    /// and rebuilt every pad to resolve it. ApplyOutput does the cloaking and records what it
    /// applied.
    /// </para>
    /// </summary>
    private async Task ApplyCloakingAsync()
    {
        try
        {
            await Task.Run(() => _state.ApplyOutput()).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _state.ReportStatus("msg.applyFailed", ex.Message, $"Apply failed: {ex.Message}");
        }
        finally
        {
            // The hidden-device count sits in the HidHide line, so it is stale until this runs.
            RefreshDriverStatus();
        }
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

    /// <summary>
    /// Writes the autostart entry, and refuses to claim a state the registry does not have.
    /// <para>
    /// The box used to stay ticked after a failed write. Since the setting is read back from the
    /// registry on the next launch, it then appeared to have un-ticked itself, which reads as a
    /// forgotten preference rather than as the refusal it actually was.
    /// </para>
    /// </summary>
    partial void OnRunAtStartupChanged(bool value)
    {
        if (_suppress)
        {
            return;
        }

        if (!StartupRegistration.SetRegistered(value))
        {
            _state.ReportStatus(
                "msg.startupEntryFailed", null, "Could not update the Windows startup entry.");

            _suppress = true;
            RunAtStartup = !value;
            _suppress = false;
            return;
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
        _state.ReportStatus(
            "msg.logLevelRestart", null, "Log level changes take effect after a restart.");
    }

    /// <summary>
    /// The master output switch. It gates evaluation inside the poll loop and submits a neutral
    /// state on its way down, so no pad is created or destroyed and nothing has to be reapplied.
    /// </summary>
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
        _state.ReportStatus(
            "msg.restartRequired", null, "This change takes effect after a restart.");
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

    /// <summary>Re-reads the captions that are formatted in code rather than bound.</summary>
    private void OnLanguageChanged()
    {
        OnPropertyChanged(nameof(MouseReturnSpeedText));
        OnPropertyChanged(nameof(ParagraphFlowDirection));

        // The two driver lines are built from keys rather than bound, so nothing would tell them
        // to change; they sit on screen until the page is rebuilt.
        RefreshDriverStatus();

        // The line is only on screen while Advanced is the selected category, but it is rebuilt
        // here as well so re-opening the page never shows a caption in the previous language.
        RefreshPollStatistics();
    }

    /// <summary>Rebuilds the poll statistics line without touching the device monitor rows.</summary>
    public void RefreshPollStatistics() => PollStatistics = FormatPollStatistics(_state.InputSourceCount);

    /// <summary>
    /// Builds the "Poll: 1000 Hz | Cycle: 18 us | Devices: 3" line.
    /// <para>
    /// Each value carries its unit and is isolated as one fragment: the separators and the spaces
    /// around them are neutral characters, so in a right-to-left line the numbers, the units and
    /// the pipes were being reordered independently of the labels they belong to.
    /// </para>
    /// </summary>
    private string FormatPollStatistics(int deviceCount) => string.Join(
        "  |  ",
        $"{Strings.Get("status.pollRate")}: " +
        Strings.Isolate($"{_state.Input.PollRateHz.ToString(CultureInfo.InvariantCulture)} {Strings.Get("unit.hz")}"),
        $"{Strings.Get("status.cycle")}: " +
        Strings.Isolate($"{_state.Input.LastLoopMicroseconds.ToString("F0", CultureInfo.InvariantCulture)} {Strings.Get("unit.microseconds")}"),
        $"{Strings.Get("status.devices")}: " +
        Strings.Isolate(deviceCount.ToString(CultureInfo.InvariantCulture)));

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

        PollStatistics = FormatPollStatistics(devices.Count);
    }
}
