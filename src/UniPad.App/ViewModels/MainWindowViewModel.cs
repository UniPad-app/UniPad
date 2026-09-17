using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UniPad.App.Localization;
using UniPad.App.Services;
using UniPad.Core.Output;

namespace UniPad.App.ViewModels;

/// <summary>
/// Root view model. Owns the player tabs, the profile selector and the status bar, and drives the
/// 60 Hz UI refresh that powers every live preview.
/// </summary>
public sealed partial class MainWindowViewModel : ViewModelBase
{
    private readonly AppState _state;
    private readonly BindCaptureService _capture;
    private int _diagnosticsTickCounter;

    // The key and the untranslated remainder of the line currently in the status bar, kept so the
    // message can be rebuilt in the other language. Null means the text came from a core service
    // already formatted, and there is nothing to re-read.
    private string? _statusKey;
    private string _statusDetail = string.Empty;

    [ObservableProperty]
    private int _selectedTabIndex;

    /// <summary>
    /// Selected entry in the left-hand category sidebar: 0 = Controls (the player tabs),
    /// 1 = Advanced, 2 = About. Advanced and About used to be tabs; they are categories now.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsControlsCategory))]
    [NotifyPropertyChangedFor(nameof(IsAdvancedCategory))]
    [NotifyPropertyChangedFor(nameof(IsAboutCategory))]
    private int _selectedCategoryIndex;

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private string _selectedProfile = string.Empty;

    [ObservableProperty]
    private string _connectedSummary = string.Empty;

    [ObservableProperty]
    private bool _showDriverBanner;

    [ObservableProperty]
    private string _driverBannerText = string.Empty;

    [ObservableProperty]
    private bool _showHidHideBanner;

    [ObservableProperty]
    private bool _isApplying;

    /// <summary>Creates the root view model and its child tabs.</summary>
    public MainWindowViewModel(AppState state)
    {
        _state = state;
        _capture = new BindCaptureService(state.Input, state.KeyboardMouse);

        foreach (var mapping in state.Players)
        {
            Players.Add(new PlayerConfigViewModel(state, mapping, _capture));
        }

        Advanced = new AdvancedViewModel(state);
        Advanced.ThemeRequested += theme => ThemeChangeRequested?.Invoke(theme);
        About = new AboutViewModel(state);
        // After an update the new executable is already on disk; exiting hands control to it.
        About.RestartRequested += () => ExitRequested?.Invoke();

        foreach (var player in Players)
        {
            AllTabs.Add(new TabDescriptor(player.Header, player));
        }

        // Captions produced in code rather than bound - tab headers, the device picker entries and
        // the bind button captions - have to be rebuilt when the user switches language.
        Strings.Instance.LanguageChanged += OnLanguageChanged;

        SelectedProfile = state.ActiveProfileName;
        foreach (var name in state.Store.ListProfiles())
        {
            Profiles.Add(name);
        }

        state.DevicesChanged += OnDevicesChanged;
        state.StatusMessage += OnStatusMessage;
        state.StatusReported += OnStatusReported;

        RefreshBanners();
        UpdateConnectedSummary();
    }

    /// <summary>The eight player tabs.</summary>
    public ObservableCollection<PlayerConfigViewModel> Players { get; } = [];

    /// <summary>Advanced settings tab.</summary>
    public AdvancedViewModel Advanced { get; }

    /// <summary>About tab, carrying the version and the third-party licence credits.</summary>
    public AboutViewModel About { get; }

    /// <summary>Every tab shown in the strip: the eight players followed by Advanced.</summary>
    public ObservableCollection<TabDescriptor> AllTabs { get; } = [];

    /// <summary>True while the Controls category is selected, so the player tab strip is shown.</summary>
    public bool IsControlsCategory => SelectedCategoryIndex == 0;

    /// <summary>True while the Advanced category is selected.</summary>
    public bool IsAdvancedCategory => SelectedCategoryIndex == 1;

    /// <summary>True while the About category is selected.</summary>
    public bool IsAboutCategory => SelectedCategoryIndex == 2;

    /// <summary>Available profile names.</summary>
    public ObservableCollection<string> Profiles { get; } = [];

    /// <summary>Localised strings, bound from XAML.</summary>
    public Strings Text => Strings.Instance;

    /// <summary>Raised when the user selects a different theme.</summary>
    public event Action<string>? ThemeChangeRequested;

    /// <summary>Raised when the window should be hidden to the notification area.</summary>
    public event Action? HideRequested;

    /// <summary>Raised when the window should be restored from the notification area.</summary>
    public event Action? ShowRequested;

    /// <summary>Raised when the application should exit.</summary>
    public event Action? ExitRequested;

    /// <summary>
    /// Refreshes every live preview. Driven by a 60 Hz dispatcher timer rather than the 1000 Hz
    /// input loop, because rendering at input rate would waste an enormous amount of CPU.
    /// </summary>
    public void OnUiTick()
    {
        // Only the visible player tab needs preview updates, and only while Controls is selected.
        if (IsControlsCategory && (uint)SelectedTabIndex < (uint)Players.Count)
        {
            Players[SelectedTabIndex].UpdateLivePreview();
        }

        // Diagnostics involve string formatting, so run them at roughly 6 Hz instead of 60.
        if (++_diagnosticsTickCounter >= 10)
        {
            _diagnosticsTickCounter = 0;

            // Advanced is a sidebar category now; About needs no live data.
            if (IsAdvancedCategory)
            {
                Advanced.UpdateDiagnostics();
            }

            UpdateConnectedSummary();
        }
    }

    private void OnDevicesChanged()
    {
        // Marshalled onto the UI thread by the caller.
        foreach (var player in Players)
        {
            player.RefreshDevices();
            player.RefreshStatus();
        }

        Advanced.RefreshDriverStatus();
        RefreshBanners();
        UpdateConnectedSummary();
    }

    /// <summary>
    /// Shows an already-formatted message coming from a core service.
    /// <para>
    /// There is no key behind it, so it keeps its own language if the interface language changes.
    /// It is isolated because a Latin sentence in a right-to-left interface otherwise has its
    /// trailing full stop resolved to the paragraph direction and moved to the far end of the line.
    /// </para>
    /// </summary>
    private void OnStatusMessage(string message)
    {
        _statusKey = null;
        _statusDetail = string.Empty;
        StatusText = Strings.Isolate(message);
    }

    /// <summary>
    /// Shows a status message that arrived with its key intact, so it can be rebuilt in the other
    /// language later. This is the same storage the window's own messages use.
    /// </summary>
    private void OnStatusReported(AppState.StatusReport report)
    {
        SetStatus(report.Key, report.Detail.Length == 0 ? null : report.Detail);
    }

    /// <summary>
    /// Shows a localised status message and remembers the key behind it.
    /// <para>
    /// <paramref name="detail"/> carries the part that is never translated - a count, a profile
    /// name, an exception message - and is isolated so the bidirectional algorithm keeps it next
    /// to the label it belongs to instead of reordering it on its own.
    /// </para>
    /// </summary>
    private void SetStatus(string key, string? detail = null)
    {
        _statusKey = key;
        _statusDetail = detail ?? string.Empty;
        StatusText = ComposeStatus();
    }

    private string ComposeStatus() =>
        _statusDetail.Length == 0
            ? Strings.Get(_statusKey!)
            : $"{Strings.Get(_statusKey!)} {Strings.Isolate(_statusDetail)}";

    /// <summary>
    /// Rebuilds every caption that was produced in code after a language change.
    /// <para>
    /// The bound captions refresh themselves now, but strings that were formatted once and stored
    /// would otherwise keep the previous language: the tab headers, the entries of each device
    /// picker, the "[not set]" text on every bind button, the banner text, the status summary and
    /// the status line itself. The tab collection is rebuilt rather than mutated per item because
    /// <see cref="TabDescriptor"/> is an immutable record.
    /// </para>
    /// </summary>
    private void OnLanguageChanged()
    {
        var selected = SelectedTabIndex;

        AllTabs.Clear();
        foreach (var player in Players)
        {
            AllTabs.Add(new TabDescriptor(player.Header, player));
        }

        SelectedTabIndex = Math.Clamp(selected, 0, AllTabs.Count - 1);

        foreach (var player in Players)
        {
            // RefreshDevices also refreshes that player's status line.
            player.RefreshDevices();
            player.RefreshAllBinds();
        }

        RefreshBanners();
        UpdateConnectedSummary();

        // The message sitting in the status bar was formatted when it was raised, so it is rebuilt
        // here whenever it came from a key rather than from a core service.
        if (_statusKey is not null)
        {
            StatusText = ComposeStatus();
        }
    }

    private void RefreshBanners()
    {
        ShowDriverBanner = !_state.Output.IsDriverAvailable;
        DriverBannerText = _state.Output.DriverError is { Length: > 0 } error
            ? $"{Strings.Get("status.driverMissing")} {Strings.Isolate($"({error})")}"
            : Strings.Get("status.driverMissing");

        ShowHidHideBanner = !_state.HidHide.IsAvailable
                            && !_state.Config.HidHideBannerDismissed;
    }

    private void UpdateConnectedSummary()
    {
        var connected = _state.Input.Devices.Count();
        var active = Players.Count(p => p.IsEnabled && _state.Output.IsPadConnected(p.Mapping.Index));

        // Both counts are isolated: "1/8" was being split around the slash in a right-to-left line.
        ConnectedSummary =
            $"{Strings.Get("status.devices")}: " +
            Strings.Isolate(connected.ToString(CultureInfo.InvariantCulture)) +
            "  |  " +
            $"{Strings.Get("status.controllers")}: " +
            Strings.Isolate($"{active}/{OutputManager.MaxPlayers}");
    }

    /// <summary>Saves the current profile.</summary>
    [RelayCommand]
    private void SaveProfile()
    {
        _state.SaveAll();
        SetStatus("msg.profileSaved");
    }

    /// <summary>Creates a new profile from the current state and switches to it.</summary>
    [RelayCommand]
    private void NewProfile()
    {
        var baseName = "Profile";
        var index = 1;
        while (Profiles.Contains($"{baseName} {index}", StringComparer.OrdinalIgnoreCase))
        {
            index++;
        }

        var name = $"{baseName} {index}";
        _state.Store.SaveProfile(name, _state.Players);
        Profiles.Add(name);
        SelectedProfile = name;
        SetStatus("msg.profileCreated", name);
    }

    /// <summary>Deletes the selected profile, unless it is the default one.</summary>
    [RelayCommand]
    private void DeleteProfile()
    {
        var name = SelectedProfile;
        if (!_state.Store.DeleteProfile(name))
        {
            SetStatus("msg.profileProtected");
            return;
        }

        Profiles.Remove(name);
        SelectedProfile = Profiles.FirstOrDefault() ?? "Default";
        SetStatus("msg.profileDeleted", name);
    }

    /// <summary>Auto-detects and maps every connected controller in one action.</summary>
    /// <remarks>
    /// AutoDetectAll applies the mappings and the cloaking itself, so it carries the same "not from
    /// the UI thread" contract as ApplyMappings and is pushed onto a worker here. The XInput slot
    /// numbers are then re-read after a short delay, because Windows has not assigned them yet
    /// when the pads have only just been plugged in - without that second pass the slot caption
    /// stayed empty until the tab was left and revisited.
    /// </remarks>
    [RelayCommand]
    private async Task AutoDetectAllAsync()
    {
        IsApplying = true;
        SetStatus("msg.applying");

        try
        {
            var count = await Task.Run(_state.AutoDetectAll);

            foreach (var player in Players)
            {
                // Devices before values: PullFromMapping resolves the picker selection against
                // the device list, which the detection pass has just changed.
                player.RefreshDevices();
                player.PullFromMapping();
            }

            await Task.Delay(350);
            _state.Output.RefreshUserIndices();

            foreach (var player in Players)
            {
                player.RefreshStatus();
            }

            UpdateConnectedSummary();

            if (count > 0)
            {
                SetStatus("msg.autoMapped", $"({count.ToString(CultureInfo.InvariantCulture)})");
            }
            else
            {
                SetStatus("msg.noDevice");
            }
        }
        catch (Exception ex)
        {
            SetStatus("msg.applyFailed", ex.Message);
        }
        finally
        {
            IsApplying = false;
        }
    }

    /// <summary>Clears every binding of every player.</summary>
    [RelayCommand]
    private void ClearAll()
    {
        _state.ClearAll();

        foreach (var player in Players)
        {
            player.PullFromMapping();
        }

        SetStatus("msg.allCleared");
    }

    /// <summary>Saves and closes.</summary>
    [RelayCommand]
    private void Ok()
    {
        _state.SaveAll();
        HideRequested?.Invoke();
    }

    /// <summary>Closes without saving the profile.</summary>
    [RelayCommand]
    private void Cancel() => HideRequested?.Invoke();

    /// <summary>
    /// Applies changes and keeps the window open.
    /// <para>
    /// Plugging in virtual pads is deliberately staggered and HidHide has to wait on an external
    /// process, so the whole transition can take several seconds. It therefore runs on a worker
    /// thread; doing it inline would freeze the window long enough for Windows to mark it as not
    /// responding. Saving happens first and on the UI thread, because it reads view-model state.
    /// </para>
    /// </summary>
    [RelayCommand]
    private async Task ApplyAsync()
    {
        _state.SaveAll();

        IsApplying = true;
        SetStatus("msg.applying");

        try
        {
            await Task.Run(() =>
            {
                _state.ApplyMappings();
                _state.ApplyCloaking();
            });

            SetStatus("msg.profileSaved");
        }
        catch (Exception ex)
        {
            SetStatus("msg.applyFailed", ex.Message);
        }
        finally
        {
            IsApplying = false;

            // Pad connection state changed underneath us, so the per-player status lines and the
            // summary in the status bar are both stale. The user index is re-read first: pads that
            // were plugged in moments ago do not have one yet when ApplyMappings returns.
            _state.Output.RefreshUserIndices();

            foreach (var player in Players)
            {
                player.RefreshStatus();
            }

            UpdateConnectedSummary();
        }
    }

    /// <summary>Hides the HidHide recommendation banner permanently.</summary>
    [RelayCommand]
    private void DismissHidHideBanner()
    {
        _state.Config.HidHideBannerDismissed = true;
        ShowHidHideBanner = false;
    }

    /// <summary>Toggles the master output switch, used by the tray menu and the hotkey.</summary>
    [RelayCommand]
    private void ToggleOutput()
    {
        Advanced.OutputEnabled = !Advanced.OutputEnabled;
        SetStatus(Advanced.OutputEnabled ? "msg.outputEnabled" : "msg.outputDisabled");
    }

    /// <summary>Reloads the active profile from disk, discarding unsaved changes.</summary>
    [RelayCommand]
    private void ReloadProfile()
    {
        _state.LoadProfile(SelectedProfile);

        foreach (var player in Players)
        {
            player.RefreshDevices();
            player.PullFromMapping();
        }
    }

    /// <summary>Requests application exit.</summary>
    [RelayCommand]
    private void Exit() => ExitRequested?.Invoke();

    /// <summary>Restores the window from the notification area. Bound to the tray menu.</summary>
    [RelayCommand]
    private void ShowWindow() => ShowRequested?.Invoke();

    /// <summary>Hides the window to the notification area.</summary>
    [RelayCommand]
    private void HideWindow() => HideRequested?.Invoke();

    partial void OnSelectedProfileChanged(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value == _state.ActiveProfileName)
        {
            return;
        }

        _state.LoadProfile(value);

        foreach (var player in Players)
        {
            player.RefreshDevices();
            player.PullFromMapping();
        }
    }
}

/// <summary>One entry in the top-level tab strip.</summary>
/// <param name="Header">Tab caption.</param>
/// <param name="Content">View model rendered inside the tab.</param>
public sealed record TabDescriptor(string Header, object Content);
