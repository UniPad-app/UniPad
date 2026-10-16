using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using UniPad.App.ViewModels;

namespace UniPad.App.Views;

/// <summary>Code-behind for the Phones page.</summary>
public partial class PhonesView : UserControl
{
    /// <summary>Creates the view.</summary>
    public PhonesView() => AvaloniaXamlLoader.Load(this);

    /// <summary>
    /// The clipboard belongs to the top-level window, which a view model cannot reach, so the copy
    /// happens here and the view model only reports it.
    /// </summary>
    private async void OnCopyLinkClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not PhonesViewModel vm || vm.JoinUrl.Length == 0)
        {
            return;
        }

        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null)
        {
            return;
        }

        await clipboard.SetTextAsync(vm.JoinUrl);
        vm.ReportCopied();
    }
}
