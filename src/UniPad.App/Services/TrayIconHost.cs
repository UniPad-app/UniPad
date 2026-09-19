using System.ComponentModel;
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
    private NativeMenuItem? _toggleItem;
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

            // The toggle caption names the action rather than the setting, so it has to follow
            // the switch itself - including when it is changed from the Advanced page.
            _viewModel.Advanced.PropertyChanged += OnAdvancedPropertyChanged;
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
            // A small PNG rather than the multi-size app.ico: the notification area asks for a
            // 16 or 20 pixel bitmap, and downscaling the 256 pixel frame looks soft.
            using var stream = AssetLoader.Open(new Uri("avares://UniPad/Assets/tray.png"));
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

        // Held onto so a later toggle only rewrites this caption instead of rebuilding the menu.
        _toggleItem = new NativeMenuItem { Command = _viewModel.ToggleOutputCommand };
        UpdateToggleCaption();
        menu.Add(_toggleItem);

        menu.Add(new NativeMenuItemSeparator());

        menu.Add(new NativeMenuItem
        {
            Header = Strings.Get("tray.exit"),
            Command = _viewModel.ExitCommand,
        });

        _icon.Menu = menu;
    }

    /// <summary>
    /// Names what the entry will do rather than what it controls.
    /// <para>
    /// A single "Enable / Disable Output" caption meant the only way to find out which of the two
    /// a click would give you was to open the Advanced page and read the checkbox. The entry now
    /// reads "Disable Output" while output is on, and the reverse while it is off.
    /// </para>
    /// </summary>
    private void UpdateToggleCaption()
    {
        if (_toggleItem is not null)
        {
            _toggleItem.Header = Strings.Get(
                _viewModel.Advanced.OutputEnabled ? "tray.disableOutput" : "tray.enableOutput");
        }
    }

    private void OnAdvancedPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // A null or empty name means "everything changed", which is worth honouring here.
        if (string.IsNullOrEmpty(e.PropertyName)
            || e.PropertyName == nameof(AdvancedViewModel.OutputEnabled))
        {
            UpdateToggleCaption();
        }
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
        _viewModel.Advanced.PropertyChanged -= OnAdvancedPropertyChanged;
        _toggleItem = null;

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
