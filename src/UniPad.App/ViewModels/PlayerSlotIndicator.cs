using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace UniPad.App.ViewModels;

/// <summary>
/// One square in the player strip under the player panel: filled while that player's virtual pad
/// is live, and its number highlighted while that player's tab is the one being shown.
/// </summary>
/// <remarks>
/// One shared set of eight exists for the whole window. Only one tab is visible at a time, so
/// "current" can follow the selected tab index instead of being tracked per tab.
/// </remarks>
public sealed partial class PlayerSlotIndicator(int number) : ObservableObject
{
    /// <summary>One-based player number.</summary>
    public int Number { get; } = number;

    /// <summary>The number as text, in Latin digits in both languages like the rest of the panel.</summary>
    public string Caption { get; } = number.ToString(CultureInfo.InvariantCulture);

    /// <summary>True while this player is enabled and its virtual pad is connected.</summary>
    [ObservableProperty]
    private bool _isConnected;

    /// <summary>True while this player's tab is the selected one.</summary>
    [ObservableProperty]
    private bool _isCurrent;
}
