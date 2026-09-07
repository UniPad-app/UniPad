using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace UniPad.App.Views;

/// <summary>Code-behind for the Advanced tab.</summary>
public partial class AdvancedView : UserControl
{
    /// <summary>Creates the view.</summary>
    public AdvancedView() => AvaloniaXamlLoader.Load(this);
}
