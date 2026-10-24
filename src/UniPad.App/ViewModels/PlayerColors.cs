using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace UniPad.App.ViewModels;

/// <summary>One fixed colour per player, shared by every place that identifies a player.</summary>
public static class PlayerColors
{
    // The identity palette, tuned against the dark theme's dark surfaces.
    private static readonly IBrush[] DarkPalette =
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

    // The same eight hues darkened for the light theme. On its light surfaces (#E4E4E4 strip,
    // #FAFAFA panels) the bright originals wash out - the gold of Player 8 worst of all, at
    // barely 1.6:1 contrast - so every player colour goes through the darkened variants there:
    // tab captions, group box frames, the strip squares and the highlighted numbers alike.
    private static readonly IBrush[] LightPalette =
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

    // Which variant the brushes are picked for. Set once at startup and again whenever the user
    // flips the theme, before anything that draws in a player colour is refreshed; every call
    // site reads it through Get afterwards, so nothing else has to track the theme.
    private static bool _light;

    /// <summary>Selects the palette <see cref="Get"/> hands out: the bright one on dark.</summary>
    public static void SetLightTheme(bool light) => _light = light;

    /// <summary>Colour of the zero-based player <paramref name="index"/>, for the active variant.</summary>
    public static IBrush Get(int index) =>
        (uint)index < DarkPalette.Length
            ? (_light ? LightPalette[index] : DarkPalette[index])
            : Brushes.Gray;

    // How faint the inside of an assigned device's frame is tinted: about 12% of the player
    // colour, enough to pick the row out at a glance while staying far below the frame and
    // text contrast, in both variants.
    private const byte FillAlpha = 30;

    /// <summary>
    /// Translucent variant of a player colour, for painting the background of a device row that
    /// belongs to that player. Takes whatever brush the caller already holds, so the tint always
    /// derives from the same colour as the frame; anything non-solid returns null, which keeps
    /// the panel background of an unassigned row.
    /// </summary>
    public static IBrush? ToFill(IBrush? brush) =>
        brush is ISolidColorBrush solid
            ? new ImmutableSolidColorBrush(new Color(FillAlpha, solid.Color.R, solid.Color.G, solid.Color.B))
            : null;
}
