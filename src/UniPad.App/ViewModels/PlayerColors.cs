using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace UniPad.App.ViewModels;

/// <summary>One fixed colour per player, shared by every place that identifies a player.</summary>
public static class PlayerColors
{
    private static readonly IBrush[] Palette =
    [
        new ImmutableSolidColorBrush(Color.Parse("#FF9F43")), // 1 orange
        new ImmutableSolidColorBrush(Color.Parse("#51CF9A")), // 2 green
        new ImmutableSolidColorBrush(Color.Parse("#A77BFF")), // 3 purple
        new ImmutableSolidColorBrush(Color.Parse("#F065B5")), // 4 pink
        new ImmutableSolidColorBrush(Color.Parse("#38C9D6")), // 5 turquoise
        new ImmutableSolidColorBrush(Color.Parse("#FF6B6B")), // 6 coral
        new ImmutableSolidColorBrush(Color.Parse("#5B9BFF")), // 7 blue
        new ImmutableSolidColorBrush(Color.Parse("#FFD43B")), // 8 gold
    ];

    /// <summary>Colour of the zero-based player <paramref name="index"/>.</summary>
    public static IBrush Get(int index) =>
        (uint)index < (uint)Palette.Length ? Palette[index] : Brushes.Gray;
}
