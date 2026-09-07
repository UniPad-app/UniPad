using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace UniPad.App.Views;

/// <summary>Code-behind for the About tab.</summary>
public partial class AboutView : UserControl
{
    /// <summary>Creates the view.</summary>
    public AboutView() => AvaloniaXamlLoader.Load(this);
}
