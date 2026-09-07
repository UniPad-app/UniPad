using System.Reflection;
using CommunityToolkit.Mvvm.Input;
using UniPad.App.Services;
using UniPad.Core.SystemServices;

namespace UniPad.App.ViewModels;

/// <summary>
/// Backs the About tab. Shows the build version, the data folder and the licence credits for every
/// third-party component, which the licences of those components require.
/// </summary>
public sealed partial class AboutViewModel : ViewModelBase
{
    private readonly AppState _state;

    /// <summary>Creates the About view model.</summary>
    public AboutViewModel(AppState state) => _state = state;

    /// <summary>Informational version of the running assembly.</summary>
    public string Version =>
        Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            .Split('+')[0]
        ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString()
        ?? "1.0.0";

    /// <summary>Absolute path of the data folder, so the user can find profiles and logs.</summary>
    public string DataRoot => PortablePaths.DataRoot;

    /// <summary>Opens the logs folder in the shell.</summary>
    [RelayCommand]
    private void OpenLogs() => OpenPath(PortablePaths.LogsDirectory);

    /// <summary>Opens the data folder in the shell.</summary>
    [RelayCommand]
    private void OpenDataFolder() => OpenPath(PortablePaths.DataRoot);

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
            _state.ReportStatus($"Could not open '{path}': {ex.Message}");
        }
    }
}
