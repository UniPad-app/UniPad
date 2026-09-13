using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace UniPad.App.Views.Controls;

/// <summary>
/// Draws one analogue stick: the outer travel circle, the grey dead zone circle and a dot at the
/// live position.
/// <para>
/// Rendered with a custom <see cref="Render"/> override rather than composed shapes, because the
/// control redraws sixty times a second and building a visual tree per frame would be wasteful.
/// </para>
/// </summary>
public sealed class AnalogStickPreview : Control
{
    /// <summary>Horizontal position, -1 to 1.</summary>
    public static readonly StyledProperty<double> ValueXProperty =
        AvaloniaProperty.Register<AnalogStickPreview, double>(nameof(ValueX));

    /// <summary>Vertical position, -1 to 1, positive is up.</summary>
    public static readonly StyledProperty<double> ValueYProperty =
        AvaloniaProperty.Register<AnalogStickPreview, double>(nameof(ValueY));

    /// <summary>Dead zone radius as a fraction of full travel.</summary>
    public static readonly StyledProperty<double> DeadzoneProperty =
        AvaloniaProperty.Register<AnalogStickPreview, double>(nameof(Deadzone), 0.15);

    /// <summary>Output range as a fraction of full travel.</summary>
    public static readonly StyledProperty<double> RangeProperty =
        AvaloniaProperty.Register<AnalogStickPreview, double>(nameof(Range), 0.95);

    /// <summary>Colour of the live position dot.</summary>
    public static readonly StyledProperty<IBrush?> DotBrushProperty =
        AvaloniaProperty.Register<AnalogStickPreview, IBrush?>(nameof(DotBrush));

    /// <summary>Colour of the travel circle outline.</summary>
    public static readonly StyledProperty<IBrush?> OutlineBrushProperty =
        AvaloniaProperty.Register<AnalogStickPreview, IBrush?>(nameof(OutlineBrush));

    /// <summary>Fill of the well behind the stick.</summary>
    public static readonly StyledProperty<IBrush?> WellBrushProperty =
        AvaloniaProperty.Register<AnalogStickPreview, IBrush?>(nameof(WellBrush));

    static AnalogStickPreview()
    {
        // Any of these changing must repaint the control.
        AffectsRender<AnalogStickPreview>(
            ValueXProperty, ValueYProperty, DeadzoneProperty, RangeProperty,
            DotBrushProperty, OutlineBrushProperty, WellBrushProperty);
    }

    /// <summary>Horizontal position, -1 to 1.</summary>
    public double ValueX
    {
        get => GetValue(ValueXProperty);
        set => SetValue(ValueXProperty, value);
    }

    /// <summary>Vertical position, -1 to 1, positive is up.</summary>
    public double ValueY
    {
        get => GetValue(ValueYProperty);
        set => SetValue(ValueYProperty, value);
    }

    /// <summary>Dead zone radius as a fraction of full travel.</summary>
    public double Deadzone
    {
        get => GetValue(DeadzoneProperty);
        set => SetValue(DeadzoneProperty, value);
    }

    /// <summary>Output range as a fraction of full travel.</summary>
    public double Range
    {
        get => GetValue(RangeProperty);
        set => SetValue(RangeProperty, value);
    }

    /// <summary>Colour of the live position dot.</summary>
    public IBrush? DotBrush
    {
        get => GetValue(DotBrushProperty);
        set => SetValue(DotBrushProperty, value);
    }

    /// <summary>Colour of the travel circle outline.</summary>
    public IBrush? OutlineBrush
    {
        get => GetValue(OutlineBrushProperty);
        set => SetValue(OutlineBrushProperty, value);
    }

    /// <summary>Fill of the well behind the stick.</summary>
    public IBrush? WellBrush
    {
        get => GetValue(WellBrushProperty);
        set => SetValue(WellBrushProperty, value);
    }

    /// <inheritdoc />
    public override void Render(DrawingContext context)
    {
        var size = Math.Min(Bounds.Width, Bounds.Height);
        if (size <= 4)
        {
            return;
        }

        var centre = new Point(Bounds.Width / 2, Bounds.Height / 2);
        var radius = (size / 2) - 2;

        var outline = OutlineBrush ?? Brushes.Gray;
        var well = WellBrush ?? Brushes.Transparent;
        var dot = DotBrush ?? new SolidColorBrush(Color.FromRgb(0xFF, 0x8C, 0x1A));

        // Well and travel circle.
        context.DrawEllipse(well, new Pen(outline, 1), centre, radius, radius);

        // Range circle: dashed, shows where the output saturates.
        var rangeRadius = radius * Math.Clamp(Range, 0.1, 1.0);
        if (rangeRadius < radius - 1)
        {
            var rangePen = new Pen(outline, 1)
            {
                DashStyle = new DashStyle([2, 2], 0),
            };
            context.DrawEllipse(null, rangePen, centre, rangeRadius, rangeRadius);
        }

        // Dead zone circle: solid grey fill, since input inside it produces nothing.
        var deadzoneRadius = radius * Math.Clamp(Deadzone, 0, 0.95);
        if (deadzoneRadius > 1)
        {
            var deadzoneBrush = new SolidColorBrush(Color.FromArgb(70, 128, 128, 128));
            context.DrawEllipse(deadzoneBrush, null, centre, deadzoneRadius, deadzoneRadius);
        }

        // Cross hairs.
        var hairPen = new Pen(outline, 0.5);
        context.DrawLine(hairPen, new Point(centre.X - radius, centre.Y), new Point(centre.X + radius, centre.Y));
        context.DrawLine(hairPen, new Point(centre.X, centre.Y - radius), new Point(centre.X, centre.Y + radius));

        // Live dot. Screen Y grows downward, so the value is negated.
        var x = centre.X + (Math.Clamp(ValueX, -1, 1) * radius);
        var y = centre.Y - (Math.Clamp(ValueY, -1, 1) * radius);
        context.DrawEllipse(dot, null, new Point(x, y), 4, 4);
    }
}
