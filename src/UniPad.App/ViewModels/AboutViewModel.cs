using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UniPad.App.Localization;
using UniPad.App.Services;
using UniPad.Core.SystemServices;

namespace UniPad.App.ViewModels;

/// <summary>
/// Backs the About page: build version, licence credits, the data folder, and the maintenance
/// actions that used to sit in the Advanced page's "Other" box.
/// <para>
/// The update flow is deliberately one button. It asks GitHub for the newest release, and when the
/// running build is behind it downloads the new executable and swaps it in, leaving only a restart
/// for the user. Nothing is replaced until the download has been verified, so an interrupted
/// update cannot damage the installation.
/// </para>
/// </summary>
public sealed partial class AboutViewModel : ViewModelBase
{
    private readonly AppState _state;

    // Key and untranslated remainder of the line currently in the maintenance box, kept so it can
    // be rebuilt in the other language. Every message on this page goes through a key: the update
    // messages used to be composed here as finished sentences, which left them in the language
    // they were raised in - the status strip showed "UniPad is up to date (1.2.0)" in English long
    // after the interface had switched to Persian, because the window is told to re-read only the
    // lines that still know their key.
    private string? _messageKey;
    private string? _messageDetail;

    /// <summary>
    /// Progress text for the maintenance box. Named "message" rather than "status" on purpose:
    /// a property called UpdateStatus would shadow the <see cref="UpdateStatus"/> enum inside this
    /// class and make every enum reference below fail to compile.
    /// </summary>
    [ObservableProperty]
    private string _updateMessage = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CheckForUpdatesCommand))]
    [NotifyCanExecuteChangedFor(nameof(UpdateMappingDatabaseCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private bool _restartPending;

    [ObservableProperty]
    private bool _manualDownloadAvailable;

    private string? _releaseUrl;

    /// <summary>Creates the About view model.</summary>
    public AboutViewModel(AppState state)
    {
        _state = state;

        // The bound captions on this page refresh themselves; the maintenance line does not,
        // because it was formatted once and stored.
        Strings.Instance.LanguageChanged += OnLanguageChanged;
    }

    /// <summary>Informational version of the running build.</summary>
    public string Version => UpdateService.CurrentVersion;

    /// <summary>Absolute path of the data folder, so the user can find profiles and logs.</summary>
    public string DataRoot => PortablePaths.DataRoot;

    /// <summary>
    /// Whether data lives next to the executable. Read through the string table rather than
    /// returning a literal, because it sits in a sentence with the data folder path beside it and
    /// was the one English word left in that line after the interface switched to Persian.
    /// </summary>
    public string PortableModeText =>
        Strings.Get(PortablePaths.IsPortableMode ? "about.portable" : "about.appData");

    /// <summary>Localised strings, bound from XAML.</summary>
    public Strings Text => Strings.Instance;

    /// <summary>Opens the logs folder in the shell.</summary>
    [RelayCommand]
    private void OpenLogs() => OpenPath(PortablePaths.LogsDirectory);

    /// <summary>Opens the data folder in the shell.</summary>
    [RelayCommand]
    private void OpenDataFolder() => OpenPath(PortablePaths.DataRoot);

    /// <summary>Opens the GitHub release page, used when an automatic update is not possible.</summary>
    [RelayCommand]
    private void OpenReleasePage() => UpdateService.OpenReleasePage(_releaseUrl);

    /// <summary>Checks GitHub for a newer release and installs it when one exists.</summary>
    [RelayCommand(CanExecute = nameof(CanRunMaintenance))]
    private async Task CheckForUpdatesAsync()
    {
        IsBusy = true;
        RestartPending = false;
        ManualDownloadAvailable = false;
        SetStatus("msg.updateChecking", null, "Checking GitHub for a newer release...");

        try
        {
            var found = await UpdateService.CheckAsync().ConfigureAwait(true);
            _releaseUrl = found.ReleaseUrl;

            switch (found.Status)
            {
                case UpdateStatus.UpToDate:
                    SetStatus("msg.upToDate", $"({found.CurrentVersion})",
                        $"UniPad is up to date ({found.CurrentVersion})");
                    return;

                case UpdateStatus.Failed:
                    ManualDownloadAvailable = true;
                    SetStatus("msg.updateFailed", found.Message,
                        $"Update failed: {found.Message}");
                    return;
            }

            // Percent arrives from a worker thread; Progress<T> marshals it back for us. The
            // version and the percentage travel as the detail of the line rather than being baked
            // into it, so a language switch part-way through a download still re-reads the label.
            var progress = new Progress<int>(percent =>
                SetStatus("msg.updateDownloading", $"{found.LatestVersion} - {percent}%",
                    $"Downloading version {found.LatestVersion} - {percent}%"));

            var applied = await UpdateService
                .DownloadAndInstallAsync(found, progress)
                .ConfigureAwait(true);

            if (applied.Status == UpdateStatus.Installed)
            {
                RestartPending = true;
                SetStatus("msg.updateInstalled", $"({applied.LatestVersion})",
                    $"Update installed - restart UniPad to use it ({applied.LatestVersion})");
            }
            else
            {
                ManualDownloadAvailable = true;
                SetStatus("msg.updateFailed", applied.Message,
                    $"Update failed: {applied.Message}");
            }
        }
        catch (Exception ex)
        {
            ManualDownloadAvailable = true;
            SetStatus("msg.updateFailed", ex.Message, $"Update failed: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Saves the current state, starts the newly installed build and asks this one to exit.</summary>
    [RelayCommand]
    private void RestartNow()
    {
        _state.SaveAll();

        if (UpdateService.TryRestart())
        {
            RestartRequested?.Invoke();
            return;
        }

        SetStatus("msg.restartFailed", null, "Could not relaunch UniPad; please start it again manually.");
    }

    /// <summary>
    /// Downloads the latest community controller mapping database, widening the set of pads that
    /// auto-map perfectly.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRunMaintenance))]
    private async Task UpdateMappingDatabaseAsync()
    {
        const string Url = "https://raw.githubusercontent.com/mdqinc/SDL_GameControllerDB/master/gamecontrollerdb.txt";

        IsBusy = true;

        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            var content = await client.GetStringAsync(Url).ConfigureAwait(true);

            if (content.Length < 1000)
            {
                SetStatus("msg.mappingDbInvalid", null,
                    "Downloaded mapping database looks invalid; keeping the existing one.");
                return;
            }

            await File.WriteAllTextAsync(PortablePaths.GameControllerDbFile, content).ConfigureAwait(true);
            SetStatus("msg.mappingDbUpdated", null, "Mapping database updated. Restart UniPad to apply it.");
        }
        catch (Exception ex)
        {
            SetStatus("msg.mappingDbFailed", ex.Message, $"Mapping database update failed: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Raised when the application should close so the updated build can take over.</summary>
    public event Action? RestartRequested;

    private bool CanRunMaintenance() => !IsBusy;

    /// <summary>
    /// Mirrors a localisable message into the page and the window's status strip, keeping the key
    /// so both can rebuild the line if the user switches language while it is still on screen.
    /// <para>
    /// <paramref name="detail"/> carries the part that is never translated - a version number, a
    /// path, an exception message - and <paramref name="fallback"/> is the English form handed to
    /// the log, which is written once and should not follow the interface language.
    /// </para>
    /// </summary>
    private void SetStatus(string key, string? detail, string fallback)
    {
        _messageKey = key;
        _messageDetail = detail;

        UpdateMessage = Compose();
        _state.ReportStatus(key, detail, fallback);
    }

    private string Compose() =>
        string.IsNullOrEmpty(_messageDetail)
            ? Strings.Get(_messageKey!)
            : $"{Strings.Get(_messageKey!)} {Strings.Isolate(_messageDetail)}";

    /// <summary>
    /// Rebuilds the maintenance line after a language change. The window rebuilds the copy in its
    /// own status strip from the same key, so the two never disagree.
    /// </summary>
    private void OnLanguageChanged()
    {
        if (_messageKey is not null)
        {
            UpdateMessage = Compose();
        }

        // A computed property with no backing field raises nothing by itself, so the view is
        // told explicitly that its text has changed.
        OnPropertyChanged(nameof(PortableModeText));
    }

    private void OpenPath(string path)
    {
        try
        {
            Directory.CreateDirectory(path);

            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            SetStatus("msg.openPathFailed", $"{path}: {ex.Message}", $"Could not open '{path}': {ex.Message}");
        }
    }
}
