using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace UniPad.App.Views.Controls;

/// <summary>A thin vertical bar showing how far an analogue trigger is pressed.</summary>
public sealed class TriggerBar : Control
{
    /// <summary>Fill level, 0 to 1.</summary>
    public static readonly StyledProperty<double> ValueProperty =
        AvaloniaProperty.Register<TriggerBar, double>(nameof(Value));

    /// <summary>Brush used for the filled portion.</summary>
    public static readonly StyledProperty<IBrush?> FillBrushProperty =
        AvaloniaProperty.Register<TriggerBar, IBrush?>(nameof(FillBrush));

    /// <summary>Brush used for the empty portion.</summary>
    public static readonly StyledProperty<IBrush?> TrackBrushProperty =
        AvaloniaProperty.Register<TriggerBar, IBrush?>(nameof(TrackBrush));

    /// <summary>Brush used for the outline.</summary>
    public static readonly StyledProperty<IBrush?> OutlineBrushProperty =
        AvaloniaProperty.Register<TriggerBar, IBrush?>(nameof(OutlineBrush));

    static TriggerBar()
    {
        AffectsRender<TriggerBar>(ValueProperty, FillBrushProperty, TrackBrushProperty, OutlineBrushProperty);
    }

    /// <summary>Fill level, 0 to 1.</summary>
    public double Value
    {
        get => GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    /// <summary>Brush used for the filled portion.</summary>
    public IBrush? FillBrush
    {
        get => GetValue(FillBrushProperty);
        set => SetValue(FillBrushProperty, value);
    }

    /// <summary>Brush used for the empty portion.</summary>
    public IBrush? TrackBrush
    {
        get => GetValue(TrackBrushProperty);
        set => SetValue(TrackBrushProperty, value);
    }

    /// <summary>Brush used for the outline.</summary>
    public IBrush? OutlineBrush
    {
        get => GetValue(OutlineBrushProperty);
        set => SetValue(OutlineBrushProperty, value);
    }

    /// <inheritdoc />
    public override void Render(DrawingContext context)
    {
        var width = Bounds.Width;
        var height = Bounds.Height;
        if (width <= 2 || height <= 2)
        {
            return;
        }

        var rect = new Rect(0.5, 0.5, width - 1, height - 1);
        var outline = OutlineBrush ?? Brushes.Gray;

        context.DrawRectangle(TrackBrush ?? Brushes.Transparent, new Pen(outline, 1), rect, 2, 2);

        var fraction = Math.Clamp(Value, 0, 1);
        if (fraction <= 0)
        {
            return;
        }

        // Fills upward from the bottom, matching the physical motion of a trigger.
        var fillHeight = (height - 2) * fraction;
        var fillRect = new Rect(1, height - 1 - fillHeight, width - 2, fillHeight);
        context.DrawRectangle(FillBrush ?? new SolidColorBrush(Color.FromRgb(0xFF, 0x8C, 0x1A)), null, fillRect, 1, 1);
    }
}
