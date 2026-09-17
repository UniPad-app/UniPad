using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using Serilog;
using UniPad.App.Localization;
using UniPad.App.Services;
using UniPad.App.ViewModels;
using UniPad.App.Views;
using Avalonia.Threading;

namespace UniPad.App;

/// <summary>Avalonia application root. Owns <see cref="AppState"/> for the process lifetime.</summary>
public partial class App : Application
{
    private AppState? _state;
    private MainWindow? _window;
    private TrayIconHost? _tray;

    /// <summary>True when the process was launched with <c>--tray</c>.</summary>
    public static bool StartHidden { get; set; }

    /// <inheritdoc />
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    /// <inheritdoc />
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
        {
            base.OnFrameworkInitializationCompleted();
            return;
        }

        // Closing the last window must not end the process, because UniPad keeps mapping while
        // living in the notification area.
        desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

        try
        {
            _state = new AppState();
            Strings.Instance.Language = _state.Config.Language;
            ApplyTheme(_state.Config.Theme);

            _state.Startup();

            var viewModel = new MainWindowViewModel(_state);
            viewModel.ThemeChangeRequested += ApplyTheme;
            viewModel.ExitRequested += () => Shutdown(desktop);

            _window = new MainWindow { DataContext = viewModel };
            viewModel.HideRequested += () => _window?.HideToTray();
            viewModel.ShowRequested += () => _window?.RestoreFromTray();

            desktop.MainWindow = _window;

            // Installed after the window exists so the menu commands are already live.
            _tray = new TrayIconHost(viewModel);

            // Listening starts here rather than in Main, because a launch that arrives before the
            // window exists would have nothing to show.
            if (SingleInstance.Current is { } guard)
            {
                guard.ActivationRequested += OnActivationRequested;
                guard.StartListening();
            }

            var startHidden = StartHidden || _state.Config.StartMinimizedToTray;
            if (startHidden)
            {
                // Show then hide so Avalonia completes window creation; a never-shown window
                // cannot be restored reliably from the tray on Windows.
                _window.Show();
                _window.HideToTray();
            }

            desktop.Exit += (_, _) => Cleanup();
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Startup failed");
            throw;
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>Switches between the dark and light variants.</summary>
    private void ApplyTheme(string theme)
    {
        RequestedThemeVariant = string.Equals(theme, "Light", StringComparison.OrdinalIgnoreCase)
            ? ThemeVariant.Light
            : ThemeVariant.Dark;
    }

    /// <summary>
    /// Brings the window back after someone launched UniPad again. Raised on the listener thread,
    /// so the work is posted to the UI thread before touching the window.
    /// </summary>
    private void OnActivationRequested() =>
        Dispatcher.UIThread.Post(() => _window?.RestoreFromTray());

    private void Shutdown(IClassicDesktopStyleApplicationLifetime desktop)
    {
        Cleanup();
        desktop.Shutdown();
    }

    private void Cleanup()
    {
        var tray = _tray;
        _tray = null;
        tray?.Dispose();

        var state = _state;
        _state = null;
        state?.Dispose();
    }
}
