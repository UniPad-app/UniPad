using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using UniPad.Core.Mapping;

namespace UniPad.App.Views.Controls;

/// <summary>
/// Draws a stylised gamepad and highlights each element as the corresponding physical input is
/// pressed, giving the user immediate confirmation that a binding is correct.
/// <para>
/// The artwork is generated in code from primitive geometry. That keeps the build free of any
/// third-party controller imagery (and of any question about yuzu's GPL-licensed assets), and the
/// drawing scales cleanly to any size.
/// </para>
/// <para>
/// The live state is a plain field rather than a styled property. <c>PadState</c> is a struct, and
/// routing it through the property system at 60 Hz would box it on every comparison; passing it by
/// reference through <see cref="SetState"/> keeps the preview allocation-free.
/// </para>
/// </summary>
public sealed class ControllerPreview : Control
{
    /// <summary>Body fill colour.</summary>
    public static readonly StyledProperty<IBrush?> BodyBrushProperty =
        AvaloniaProperty.Register<ControllerPreview, IBrush?>(nameof(BodyBrush));

    /// <summary>Colour of unpressed detail elements.</summary>
    public static readonly StyledProperty<IBrush?> DetailBrushProperty =
        AvaloniaProperty.Register<ControllerPreview, IBrush?>(nameof(DetailBrush));

    /// <summary>Colour applied to a pressed element.</summary>
    public static readonly StyledProperty<IBrush?> HighlightBrushProperty =
        AvaloniaProperty.Register<ControllerPreview, IBrush?>(nameof(HighlightBrush));

    /// <summary>Outline colour.</summary>
    public static readonly StyledProperty<IBrush?> OutlineBrushProperty =
        AvaloniaProperty.Register<ControllerPreview, IBrush?>(nameof(OutlineBrush));

    /// <summary>Reference design width; all coordinates below are expressed in this space.</summary>
    private const double DesignWidth = 320;

    /// <summary>Reference design height.</summary>
    private const double DesignHeight = 210;

    private PadState _state;

    static ControllerPreview()
    {
        AffectsRender<ControllerPreview>(
            BodyBrushProperty, DetailBrushProperty, HighlightBrushProperty, OutlineBrushProperty);
    }

    /// <summary>Live emulated pad state currently being drawn.</summary>
    public ref readonly PadState State => ref _state;

    /// <summary>Body fill colour.</summary>
    public IBrush? BodyBrush
    {
        get => GetValue(BodyBrushProperty);
        set => SetValue(BodyBrushProperty, value);
    }

    /// <summary>Colour of unpressed detail elements.</summary>
    public IBrush? DetailBrush
    {
        get => GetValue(DetailBrushProperty);
        set => SetValue(DetailBrushProperty, value);
    }

    /// <summary>Colour applied to a pressed element.</summary>
    public IBrush? HighlightBrush
    {
        get => GetValue(HighlightBrushProperty);
        set => SetValue(HighlightBrushProperty, value);
    }

    /// <summary>Outline colour.</summary>
    public IBrush? OutlineBrush
    {
        get => GetValue(OutlineBrushProperty);
        set => SetValue(OutlineBrushProperty, value);
    }

    /// <summary>
    /// Replaces the state being drawn and schedules a repaint, but only when something that is
    /// actually visible has changed. At 60 Hz an idle controller would otherwise repaint the whole
    /// diagram sixty times a second for no reason.
    /// </summary>
    /// <param name="state">The state currently being sent to the game.</param>
    public void SetState(in PadState state)
    {
        if (!HasVisibleChange(in _state, in state))
        {
            return;
        }

        _state = state;
        InvalidateVisual();
    }

    /// <summary>
    /// Compares exactly the members the renderer reads. Deliberately explicit rather than a struct
    /// equality check: <c>PadState</c> carries fields this diagram never draws, and the default
    /// comparison for a struct without <c>IEquatable</c> boxes both operands.
    /// </summary>
    private static bool HasVisibleChange(in PadState a, in PadState b) =>
        a.A != b.A
        || a.B != b.B
        || a.X != b.X
        || a.Y != b.Y
        || a.LeftBumper != b.LeftBumper
        || a.RightBumper != b.RightBumper
        || a.LeftTrigger != b.LeftTrigger
        || a.RightTrigger != b.RightTrigger
        || a.DPadUp != b.DPadUp
        || a.DPadDown != b.DPadDown
        || a.DPadLeft != b.DPadLeft
        || a.DPadRight != b.DPadRight
        || a.LeftThumb != b.LeftThumb
        || a.RightThumb != b.RightThumb
        || a.LeftThumbX != b.LeftThumbX
        || a.LeftThumbY != b.LeftThumbY
        || a.RightThumbX != b.RightThumbX
        || a.RightThumbY != b.RightThumbY
        || a.Back != b.Back
        || a.Start != b.Start
        || a.Guide != b.Guide;

    /// <inheritdoc />
    public override void Render(DrawingContext context)
    {
        if (Bounds.Width <= 10 || Bounds.Height <= 10)
        {
            return;
        }

        // Uniform scale with letterboxing so the pad never distorts.
        var scale = Math.Min(Bounds.Width / DesignWidth, Bounds.Height / DesignHeight);
        var offsetX = (Bounds.Width - (DesignWidth * scale)) / 2;
        var offsetY = (Bounds.Height - (DesignHeight * scale)) / 2;

        using var _ = context.PushTransform(
            Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(offsetX, offsetY));

        var body = BodyBrush ?? Brushes.WhiteSmoke;
        var detail = DetailBrush ?? Brushes.DimGray;
        var highlight = HighlightBrush ?? Brushes.DodgerBlue;
        var outline = OutlineBrush ?? Brushes.Gray;
        var outlinePen = new Pen(outline, 1.2);

        DrawBody(context, body, outlinePen);
        DrawShoulders(context, in _state, detail, highlight, outlinePen);
        DrawFaceButtons(context, in _state, detail, highlight);
        DrawDPad(context, in _state, detail, highlight);
        DrawSticks(context, in _state, detail, highlight, outlinePen);
        DrawCentreButtons(context, in _state, detail, highlight);
    }

    private static void DrawBody(DrawingContext context, IBrush body, Pen outlinePen)
    {
        // Rounded central slab with two grip lobes, evoking a modern pad silhouette.
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(new Point(70, 55), isFilled: true);
            ctx.CubicBezierTo(new Point(110, 42), new Point(210, 42), new Point(250, 55));
            ctx.CubicBezierTo(new Point(288, 68), new Point(300, 108), new Point(288, 138));
            ctx.CubicBezierTo(new Point(276, 168), new Point(244, 176), new Point(226, 156));
            ctx.CubicBezierTo(new Point(212, 140), new Point(206, 128), new Point(186, 126));
            ctx.LineTo(new Point(134, 126));
            ctx.CubicBezierTo(new Point(114, 128), new Point(108, 140), new Point(94, 156));
            ctx.CubicBezierTo(new Point(76, 176), new Point(44, 168), new Point(32, 138));
            ctx.CubicBezierTo(new Point(20, 108), new Point(32, 68), new Point(70, 55));
            ctx.EndFigure(isClosed: true);
        }

        context.DrawGeometry(body, outlinePen, geometry);
    }

    private static void DrawShoulders(
        DrawingContext context, in PadState state, IBrush detail, IBrush highlight, Pen outlinePen)
    {
        // Bumpers.
        context.DrawRectangle(
            state.LeftBumper ? highlight : detail, outlinePen,
            new RoundedRect(new Rect(52, 34, 46, 12), 5));

        context.DrawRectangle(
            state.RightBumper ? highlight : detail, outlinePen,
            new RoundedRect(new Rect(222, 34, 46, 12), 5));

        // Triggers, drawn with a fill proportional to how far they are pressed.
        DrawTriggerBlock(context, new Rect(58, 20, 36, 12), state.LeftTrigger, detail, highlight, outlinePen);
        DrawTriggerBlock(context, new Rect(226, 20, 36, 12), state.RightTrigger, detail, highlight, outlinePen);
    }

    private static void DrawTriggerBlock(
        DrawingContext context, Rect rect, byte value, IBrush detail, IBrush highlight, Pen outlinePen)
    {
        context.DrawRectangle(detail, outlinePen, new RoundedRect(rect, 4));

        var fraction = value / 255.0;
        if (fraction <= 0.01)
        {
            return;
        }

        var fillRect = new Rect(rect.X + 1, rect.Y + 1, (rect.Width - 2) * fraction, rect.Height - 2);
        context.DrawRectangle(highlight, null, new RoundedRect(fillRect, 3));
    }

    private static void DrawFaceButtons(DrawingContext context, in PadState state, IBrush detail, IBrush highlight)
    {
        const double Radius = 9;

        // Diamond arrangement: Y top, A bottom, X left, B right.
        DrawCircleWithLabel(context, new Point(240, 68), Radius, state.Y, detail, highlight, "Y");
        DrawCircleWithLabel(context, new Point(222, 86), Radius, state.X, detail, highlight, "X");
        DrawCircleWithLabel(context, new Point(258, 86), Radius, state.B, detail, highlight, "B");
        DrawCircleWithLabel(context, new Point(240, 104), Radius, state.A, detail, highlight, "A");
    }

    private static void DrawCircleWithLabel(
        DrawingContext context, Point centre, double radius, bool pressed,
        IBrush detail, IBrush highlight, string label)
    {
        context.DrawEllipse(pressed ? highlight : detail, null, centre, radius, radius);

        var text = new FormattedText(
            label,
            System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            Typeface.Default,
            10,
            pressed ? Brushes.White : Brushes.WhiteSmoke);

        context.DrawText(text, new Point(centre.X - (text.Width / 2), centre.Y - (text.Height / 2)));
    }

    private static void DrawDPad(DrawingContext context, in PadState state, IBrush detail, IBrush highlight)
    {
        const double ArmLength = 11;
        const double ArmWidth = 11;
        var centre = new Point(80, 104);

        // Up
        context.DrawRectangle(
            state.DPadUp ? highlight : detail, null,
            new RoundedRect(new Rect(centre.X - (ArmWidth / 2), centre.Y - ArmLength - (ArmWidth / 2), ArmWidth, ArmLength), 2));

        // Down
        context.DrawRectangle(
            state.DPadDown ? highlight : detail, null,
            new RoundedRect(new Rect(centre.X - (ArmWidth / 2), centre.Y + (ArmWidth / 2), ArmWidth, ArmLength), 2));

        // Left
        context.DrawRectangle(
            state.DPadLeft ? highlight : detail, null,
            new RoundedRect(new Rect(centre.X - ArmLength - (ArmWidth / 2), centre.Y - (ArmWidth / 2), ArmLength, ArmWidth), 2));

        // Right
        context.DrawRectangle(
            state.DPadRight ? highlight : detail, null,
            new RoundedRect(new Rect(centre.X + (ArmWidth / 2), centre.Y - (ArmWidth / 2), ArmLength, ArmWidth), 2));

        // Hub
        context.DrawRectangle(
            detail, null,
            new Rect(centre.X - (ArmWidth / 2), centre.Y - (ArmWidth / 2), ArmWidth, ArmWidth));
    }

    private static void DrawSticks(
        DrawingContext context, in PadState state, IBrush detail, IBrush highlight, Pen outlinePen)
    {
        DrawStick(
            context, new Point(126, 76), state.LeftThumbX, state.LeftThumbY, state.LeftThumb,
            detail, highlight, outlinePen);

        DrawStick(
            context, new Point(194, 108), state.RightThumbX, state.RightThumbY, state.RightThumb,
            detail, highlight, outlinePen);
    }

    private static void DrawStick(
        DrawingContext context, Point centre, short valueX, short valueY, bool pressed,
        IBrush detail, IBrush highlight, Pen outlinePen)
    {
        const double WellRadius = 16;
        const double CapRadius = 10;
        const double MaxTravel = WellRadius - CapRadius + 3;

        // Recessed well.
        context.DrawEllipse(
            new SolidColorBrush(Color.FromArgb(40, 0, 0, 0)), outlinePen, centre, WellRadius, WellRadius);

        // Cap, offset by the live stick position (screen Y is inverted).
        var offsetX = Math.Clamp(valueX / 32767.0, -1, 1) * MaxTravel;
        var offsetY = -Math.Clamp(valueY / 32767.0, -1, 1) * MaxTravel;
        var capCentre = new Point(centre.X + offsetX, centre.Y + offsetY);

        context.DrawEllipse(pressed ? highlight : detail, outlinePen, capCentre, CapRadius, CapRadius);
    }

    private static void DrawCentreButtons(DrawingContext context, in PadState state, IBrush detail, IBrush highlight)
    {
        // Back
        context.DrawEllipse(state.Back ? highlight : detail, null, new Point(142, 62), 5, 5);

        // Start
        context.DrawEllipse(state.Start ? highlight : detail, null, new Point(178, 62), 5, 5);

        // Guide, larger and centred.
        context.DrawEllipse(state.Guide ? highlight : detail, null, new Point(160, 74), 8, 8);
    }
}
