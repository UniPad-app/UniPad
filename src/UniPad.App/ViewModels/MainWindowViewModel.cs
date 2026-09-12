using System.Collections.ObjectModel;
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
        _capture = new BindCaptureService(state.Input);

        foreach (var mapping in state.Players)
        {
            Players.Add(new PlayerConfigViewModel(state, mapping, _capture));
        }

        Advanced = new AdvancedViewModel(state);
        Advanced.ThemeRequested += theme => ThemeChangeRequested?.Invoke(theme);
        About = new AboutViewModel(state);

        foreach (var player in Players)
        {
            AllTabs.Add(new TabDescriptor(player.Header, player));
        }

        // Tab headers are plain strings captured at construction time, so they have to be rebuilt
        // when the user switches language.
        Strings.Instance.LanguageChanged += RebuildTabHeaders;

        SelectedProfile = state.ActiveProfileName;
        foreach (var name in state.Store.ListProfiles())
        {
            Profiles.Add(name);
        }

        state.DevicesChanged += OnDevicesChanged;
        state.StatusMessage += OnStatusMessage;

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

    private void OnStatusMessage(string message) => StatusText = message;

    /// <summary>
    /// Replaces every tab header in place after a language change. The collection is rebuilt rather
    /// than mutated per item because <see cref="TabDescriptor"/> is an immutable record.
    /// </summary>
    private void RebuildTabHeaders()
    {
        var selected = SelectedTabIndex;

        AllTabs.Clear();
        foreach (var player in Players)
        {
            AllTabs.Add(new TabDescriptor(player.Header, player));
        }

        SelectedTabIndex = Math.Clamp(selected, 0, AllTabs.Count - 1);
        UpdateConnectedSummary();
    }

    private void RefreshBanners()
    {
        ShowDriverBanner = !_state.Output.IsDriverAvailable;
        DriverBannerText = _state.Output.DriverError is { Length: > 0 } error
            ? $"{Strings.Get("status.driverMissing")} ({error})"
            : Strings.Get("status.driverMissing");

        ShowHidHideBanner = !_state.HidHide.IsAvailable
                            && !_state.Config.HidHideBannerDismissed;
    }

    private void UpdateConnectedSummary()
    {
        var connected = _state.Input.Devices.Count();
        var active = Players.Count(p => p.IsEnabled && _state.Output.IsPadConnected(p.Mapping.Index));

        ConnectedSummary =
            $"{Strings.Get("status.devices")}: {connected}  |  " +
            $"{Strings.Get("status.controllers")}: {active}/{OutputManager.MaxPlayers}";
    }

    /// <summary>Saves the current profile.</summary>
    [RelayCommand]
    private void SaveProfile()
    {
        _state.SaveAll();
        StatusText = Strings.Get("msg.profileSaved");
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
        StatusText = $"Profile '{name}' created.";
    }

    /// <summary>Deletes the selected profile, unless it is the default one.</summary>
    [RelayCommand]
    private void DeleteProfile()
    {
        var name = SelectedProfile;
        if (!_state.Store.DeleteProfile(name))
        {
            StatusText = "The default profile cannot be deleted.";
            return;
        }

        Profiles.Remove(name);
        SelectedProfile = Profiles.FirstOrDefault() ?? "Default";
        StatusText = $"Profile '{name}' deleted.";
    }

    /// <summary>Auto-detects and maps every connected controller in one action.</summary>
    [RelayCommand]
    private void AutoDetectAll()
    {
        var count = _state.AutoDetectAll();

        foreach (var player in Players)
        {
            player.PullFromMapping();
            player.RefreshDevices();
        }

        StatusText = count > 0
            ? $"{Strings.Get("msg.autoMapped")} ({count})"
            : Strings.Get("msg.noDevice");
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

        StatusText = "All mappings cleared.";
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
        StatusText = "Applying...";

        try
        {
            await Task.Run(() =>
            {
                _state.ApplyMappings();
                _state.ApplyCloaking();
            });

            StatusText = Strings.Get("msg.profileSaved");
        }
        catch (Exception ex)
        {
            StatusText = $"Apply failed: {ex.Message}";
        }
        finally
        {
            IsApplying = false;

            // Pad connection state changed underneath us, so the per-player status lines and the
            // summary in the status bar are both stale.
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
        StatusText = Advanced.OutputEnabled ? "Output enabled." : "Output disabled.";
    }

    /// <summary>Reloads the active profile from disk, discarding unsaved changes.</summary>
    [RelayCommand]
    private void ReloadProfile()
    {
        _state.LoadProfile(SelectedProfile);

        foreach (var player in Players)
        {
            player.PullFromMapping();
            player.RefreshDevices();
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
            player.PullFromMapping();
            player.RefreshDevices();
        }
    }
}

/// <summary>One entry in the top-level tab strip.</summary>
/// <param name="Header">Tab caption.</param>
/// <param name="Content">View model rendered inside the tab.</param>
public sealed record TabDescriptor(string Header, object Content);
