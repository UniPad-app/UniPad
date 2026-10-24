using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace UniPad.App.ViewModels;

/// <summary>One fixed colour per player, shared by every place that identifies a player.</summary>
public static class PlayerColors
{
    // The identity palette, tuned against the dark theme's dark surfaces. It also stays the fill
    // and frame colour in both variants - the strip squares, the device rows and the group box
    // borders are blocks and strokes, which keep enough contrast on light grey as they are.
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

    // The same eight hues darkened for use as text on the light theme. On the light grey tab
    // strip (#E4E4E4) the bright originals wash out - the gold of Player 8 worst of all, at
    // barely 1.6:1 contrast - so captions take these darker variants, which sit near 4:1.
    // Fills keep the bright palette; only text goes through here.
    private static readonly IBrush[] TextPalette =
    [
        new ImmutableSolidColorBrush(Color.Parse("#e87d13")), // 1 orange
        new ImmutableSolidColorBrush(Color.Parse("#19bd89")), // 2 green
        new ImmutableSolidColorBrush(Color.Parse("#793de2")), // 3 purple
        new ImmutableSolidColorBrush(Color.Parse("#e5288d")), // 4 pink
        new ImmutableSolidColorBrush(Color.Parse("#00a7b9")), // 5 turquoise
        new ImmutableSolidColorBrush(Color.Parse("#df2b2b")), // 6 coral
        new ImmutableSolidColorBrush(Color.Parse("#1f66d0")), // 7 blue
        new ImmutableSolidColorBrush(Color.Parse("#d3a500")), // 8 gold
    ];

    /// <summary>Colour of the zero-based player <paramref name="index"/>.</summary>
    public static IBrush Get(int index) =>
        (uint)index < (uint)Palette.Length ? Palette[index] : Brushes.Gray;

    /// <summary>
    /// Text-safe variant of the player colour for the tab captions: the bright palette on dark,
    /// the darkened variant on light, so the caption keeps its contrast against the strip in both
    /// variants. Falls back to grey like <see cref="Get"/> for out-of-range indexes.
    /// </summary>
    public static IBrush GetText(int index, bool lightTheme) =>
        (uint)index < (uint)TextPalette.Length
            ? (lightTheme ? TextPalette[index] : Palette[index])
            : Brushes.Gray;
}
