using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Threading;
using UniPad.App.Localization;
using UniPad.App.ViewModels;

namespace UniPad.App.Views;

/// <summary>
/// Main window. Owns the 60 Hz dispatcher timer that drives every live preview - deliberately
/// decoupled from the 1000 Hz input loop so rendering never becomes the bottleneck.
/// </summary>
public partial class MainWindow : Window
{
    private readonly DispatcherTimer _uiTimer;

    /// <summary>Creates the window and starts the UI refresh timer.</summary>
    public MainWindow()
    {
        AvaloniaXamlLoader.Load(this);

        _uiTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(16),
        };
        _uiTimer.Tick += OnUiTimerTick;

        Opened += (_, _) => _uiTimer.Start();
        Closing += OnClosing;
        KeyDown += OnKeyDown;

        ApplyFlowDirection();
        Strings.Instance.LanguageChanged += ApplyFlowDirection;
    }

    /// <summary>
    /// Mirrors the whole layout for right-to-left languages. Setting it on the window is enough,
    /// because <c>FlowDirection</c> inherits down the visual tree.
    /// </summary>
    private void ApplyFlowDirection() =>
        FlowDirection = Strings.Instance.IsRightToLeft
            ? FlowDirection.RightToLeft
            : FlowDirection.LeftToRight;

    private void OnUiTimerTick(object? sender, EventArgs e)
    {
        if (DataContext is MainWindowViewModel viewModel)
        {
            viewModel.OnUiTick();
        }
    }

    /// <summary>
    /// Closing hides the window instead of exiting when the user opted for tray behaviour, so
    /// mapping keeps running while the game is in the foreground.
    /// </summary>
    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        if (viewModel.Advanced.MinimizeToTrayOnClose)
        {
            e.Cancel = true;
            HideToTray();
        }
    }

    /// <summary>Ctrl+Alt+U toggles the master output switch, matching the documented hotkey.</summary>
    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.U
            && e.KeyModifiers.HasFlag(KeyModifiers.Control)
            && e.KeyModifiers.HasFlag(KeyModifiers.Alt)
            && DataContext is MainWindowViewModel viewModel)
        {
            viewModel.ToggleOutputCommand.Execute(null);
            e.Handled = true;
        }
    }

    /// <summary>Hides the window and pauses the UI timer so a hidden window costs nothing.</summary>
    public void HideToTray()
    {
        _uiTimer.Stop();
        Hide();
    }

    /// <summary>Restores the window from the notification area.</summary>
    public void RestoreFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
        _uiTimer.Start();
    }
}
