using Avalonia.Controls;
using Avalonia.Platform;
using Serilog;
using UniPad.App.Localization;
using UniPad.App.ViewModels;

namespace UniPad.App.Services;

/// <summary>
/// Owns the notification-area icon and its menu.
/// <para>
/// The tray icon is built in code rather than declared in XAML for two reasons: its captions have
/// to be rebuilt whenever the user switches language, and a failure to load the icon must degrade
/// to "no tray icon" instead of taking the whole application down at startup.
/// </para>
/// </summary>
public sealed class TrayIconHost : IDisposable
{
    private readonly MainWindowViewModel _viewModel;
    private TrayIcon? _icon;
    private TrayIcons? _icons;
    private bool _disposed;

    /// <summary>Creates and installs the tray icon for the given window view model.</summary>
    public TrayIconHost(MainWindowViewModel viewModel)
    {
        _viewModel = viewModel;

        try
        {
            _icon = new TrayIcon
            {
                Icon = LoadIcon(),
                IsVisible = true,
            };

            // A left click is the conventional way to bring a tray application back.
            _icon.Clicked += OnClicked;

            _icons = [_icon];
            TrayIcon.SetIcons(Avalonia.Application.Current!, _icons);

            RebuildMenu();
            Strings.Instance.LanguageChanged += RebuildMenu;
        }
        catch (Exception ex)
        {
            // Some minimal Windows installations and most remote sessions have no notification
            // area at all. Mapping still works, so this is a warning rather than a failure.
            Log.Warning(ex, "Tray icon unavailable; continuing without it");
            _icon = null;
            _icons = null;
        }
    }

    private static WindowIcon? LoadIcon()
    {
        try
        {
            using var stream = AssetLoader.Open(new Uri("avares://UniPad/Assets/tray.ico"));
            return new WindowIcon(stream);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not load the tray icon asset");
            return null;
        }
    }

    private void OnClicked(object? sender, EventArgs e) => _viewModel.ShowWindowCommand.Execute(null);

    /// <summary>Rebuilds the menu so captions follow the active language.</summary>
    private void RebuildMenu()
    {
        if (_icon is null)
        {
            return;
        }

        _icon.ToolTipText = Strings.Get("tray.tooltip");

        var menu = new NativeMenu();

        menu.Add(new NativeMenuItem
        {
            Header = Strings.Get("tray.open"),
            Command = _viewModel.ShowWindowCommand,
        });

        menu.Add(new NativeMenuItemSeparator());

        menu.Add(new NativeMenuItem
        {
            Header = Strings.Get("tray.toggle"),
            Command = _viewModel.ToggleOutputCommand,
        });

        menu.Add(new NativeMenuItemSeparator());

        menu.Add(new NativeMenuItem
        {
            Header = Strings.Get("tray.exit"),
            Command = _viewModel.ExitCommand,
        });

        _icon.Menu = menu;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Strings.Instance.LanguageChanged -= RebuildMenu;

        if (_icon is not null)
        {
            _icon.Clicked -= OnClicked;
            _icon.IsVisible = false;
            _icon.Dispose();
            _icon = null;
        }

        _icons?.Clear();
        _icons = null;
    }
}
