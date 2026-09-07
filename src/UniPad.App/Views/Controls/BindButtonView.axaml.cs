using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using UniPad.App.ViewModels;

namespace UniPad.App.Views.Controls;

/// <summary>
/// Code-behind for a bind button. Only the non-primary mouse buttons need handling here; the
/// left click is wired straight to the capture command in XAML.
/// </summary>
public partial class BindButtonView : UserControl
{
    /// <summary>Creates the view.</summary>
    public BindButtonView() => AvaloniaXamlLoader.Load(this);

    /// <summary>Middle click clears the binding as a quick shortcut, matching yuzu.</summary>
    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton != MouseButton.Middle)
        {
            return;
        }

        if (DataContext is BindButtonViewModel viewModel)
        {
            viewModel.ClearCommand.Execute(null);
            e.Handled = true;
        }
    }
}
