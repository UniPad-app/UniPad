using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using UniPad.App.ViewModels;
using UniPad.App.Views.Controls;
using UniPad.Core.Mapping;

namespace UniPad.App.Views;

/// <summary>Code-behind for a single player configuration tab.</summary>
public partial class PlayerConfigView : UserControl
{
    private ControllerPreview? _preview;
    private PlayerConfigViewModel? _subscribed;

    /// <summary>Creates the view.</summary>
    public PlayerConfigView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
        _preview = this.FindControl<ControllerPreview>("PART_ControllerPreview");
    }

    /// <summary>
    /// Re-targets the preview subscription. The view is created by a data template and recycled
    /// across tabs, so the view model underneath it can change at any time; leaving the old
    /// subscription in place would draw one player's input onto another player's diagram.
    /// </summary>
    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_subscribed is not null)
        {
            _subscribed.PreviewStateUpdated -= OnPreviewStateUpdated;
            _subscribed = null;
        }

        if (DataContext is PlayerConfigViewModel viewModel)
        {
            viewModel.PreviewStateUpdated += OnPreviewStateUpdated;
            _subscribed = viewModel;
        }
    }

    /// <summary>
    /// Pushes the latest emulated pad state into the diagram. Driven by the window's 60 Hz timer
    /// via the view model rather than bound, because <c>PadState</c> is a struct and binding it
    /// would box every frame.
    /// </summary>
    private void OnPreviewStateUpdated(in PadState state)
    {
        _preview?.SetState(in state);
    }
}
