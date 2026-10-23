using System.Globalization;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace UniPad.App.ViewModels;

/// <summary>
/// One square in the player strip under the player panel: filled with the player's colour while
/// its virtual pad is live, number highlighted while its tab is shown. Clicking it opens that tab.
/// </summary>
public sealed partial class PlayerSlotIndicator(int number, Action<int> select) : ObservableObject
{
    /// <summary>One-based player number.</summary>
    public int Number { get; } = number;

    /// <summary>The number as text, in Latin digits in both languages like the rest of the panel.</summary>
    public string Caption { get; } = number.ToString(CultureInfo.InvariantCulture);

    /// <summary>This player's colour.</summary>
    public IBrush Brush { get; } = PlayerColors.Get(number - 1);

    /// <summary>Square fill: the player's colour while connected, empty otherwise.</summary>
    public IBrush BoxFill => IsConnected ? Brush : Brushes.Transparent;

    /// <summary>True while this player is enabled and its virtual pad is connected.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BoxFill))]
    private bool _isConnected;

    /// <summary>True while this player's tab is the selected one.</summary>
    [ObservableProperty]
    private bool _isCurrent;

    /// <summary>Opens this player's tab.</summary>
    [RelayCommand]
    private void Select() => select(Number - 1);
}
