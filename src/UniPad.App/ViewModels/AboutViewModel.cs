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
    public AboutViewModel(AppState state) => _state = state;

    /// <summary>Informational version of the running build.</summary>
    public string Version => UpdateService.CurrentVersion;

    /// <summary>Absolute path of the data folder, so the user can find profiles and logs.</summary>
    public string DataRoot => PortablePaths.DataRoot;

    /// <summary>Whether data lives next to the executable.</summary>
    public string PortableModeText => PortablePaths.IsPortableMode ? "Portable" : "AppData";

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
        SetStatus(Strings.Get("msg.updateChecking"));

        try
        {
            var found = await UpdateService.CheckAsync().ConfigureAwait(true);
            _releaseUrl = found.ReleaseUrl;

            switch (found.Status)
            {
                case UpdateStatus.UpToDate:
                    SetStatus($"{Strings.Get("msg.upToDate")} ({found.CurrentVersion})");
                    return;

                case UpdateStatus.Failed:
                    ManualDownloadAvailable = true;
                    SetStatus($"{Strings.Get("msg.updateFailed")}: {found.Message}");
                    return;
            }

            // Percent arrives from a worker thread; Progress<T> marshals it back for us.
            var progress = new Progress<int>(percent =>
                SetStatus($"{Strings.Get("msg.updateDownloading")} {found.LatestVersion} - {percent}%"));

            var applied = await UpdateService
                .DownloadAndInstallAsync(found, progress)
                .ConfigureAwait(true);

            if (applied.Status == UpdateStatus.Installed)
            {
                RestartPending = true;
                SetStatus($"{Strings.Get("msg.updateInstalled")} ({applied.LatestVersion})");
            }
            else
            {
                ManualDownloadAvailable = true;
                SetStatus($"{Strings.Get("msg.updateFailed")}: {applied.Message}");
            }
        }
        catch (Exception ex)
        {
            ManualDownloadAvailable = true;
            SetStatus($"{Strings.Get("msg.updateFailed")}: {ex.Message}");
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

        SetStatus("Could not relaunch UniPad; please start it again manually.");
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
                SetStatus("Downloaded mapping database looks invalid; keeping the existing one.");
                return;
            }

            await File.WriteAllTextAsync(PortablePaths.GameControllerDbFile, content).ConfigureAwait(true);
            SetStatus("Mapping database updated. Restart UniPad to apply it.");
        }
        catch (Exception ex)
        {
            SetStatus($"Mapping database update failed: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Raised when the application should close so the updated build can take over.</summary>
    public event Action? RestartRequested;

    private bool CanRunMaintenance() => !IsBusy;

    /// <summary>Mirrors the message into the page and the window's status strip.</summary>
    private void SetStatus(string message)
    {
        UpdateMessage = message;
        _state.ReportStatus(message);
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
            SetStatus($"Could not open '{path}': {ex.Message}");
        }
    }
}
