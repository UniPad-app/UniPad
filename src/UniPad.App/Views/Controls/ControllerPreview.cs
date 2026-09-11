using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using UniPad.Core.Mapping;

namespace UniPad.App.Views.Controls;

/// <summary>
/// Draws an Xbox-style gamepad and lights each element up as the corresponding physical input is
/// pressed, giving the user immediate confirmation that a binding is correct.
/// <para>
/// The artwork is generated in code from primitive geometry, so the build stays free of any
/// third-party controller imagery and the drawing scales cleanly to any size.
/// </para>
/// <para>
/// Face buttons carry their own Xbox colours and brighten with a halo when pressed. Every other
/// element is neutral at rest and lights up in the shared press colour from
/// <see cref="HighlightBrush"/>.
/// </para>
/// <para>
/// Field order in this class matters. Static initialisers run in textual order, so every layout
/// constant is declared before the geometry that is built from it.
/// </para>
/// </summary>
public sealed class ControllerPreview : Control
{
    // ---------------------------------------------------------------- styled properties

    /// <summary>Body fill colour.</summary>
    public static readonly StyledProperty<IBrush?> BodyBrushProperty =
        AvaloniaProperty.Register<ControllerPreview, IBrush?>(nameof(BodyBrush));

    /// <summary>Colour of unpressed detail elements.</summary>
    public static readonly StyledProperty<IBrush?> DetailBrushProperty =
        AvaloniaProperty.Register<ControllerPreview, IBrush?>(nameof(DetailBrush));

    /// <summary>Colour applied to a pressed element other than a face button.</summary>
    public static readonly StyledProperty<IBrush?> HighlightBrushProperty =
        AvaloniaProperty.Register<ControllerPreview, IBrush?>(nameof(HighlightBrush));

    /// <summary>Outline colour.</summary>
    public static readonly StyledProperty<IBrush?> OutlineBrushProperty =
        AvaloniaProperty.Register<ControllerPreview, IBrush?>(nameof(OutlineBrush));

    // ---------------------------------------------------------------- design space

    /// <summary>Reference width; every coordinate below is expressed in this space.</summary>
    private const double DesignWidth = 360;

    /// <summary>Reference height.</summary>
    private const double DesignHeight = 270;

    /// <summary>Horizontal mirror line of the pad.</summary>
    private const double CentreX = DesignWidth / 2;

    // ---------------------------------------------------------------- palette

    private static readonly Color AColour = Color.FromRgb(0x23, 0xA4, 0x55);
    private static readonly Color BColour = Color.FromRgb(0xE0, 0x3A, 0x3A);
    private static readonly Color XColour = Color.FromRgb(0x1E, 0x6F, 0xD9);
    private static readonly Color YColour = Color.FromRgb(0xF2, 0xB0, 0x1E);

    /// <summary>Press colour used when the host supplies no <see cref="HighlightBrush"/>.</summary>
    private static readonly Color PressColour = Color.FromRgb(0xFF, 0x8C, 0x1A);

    private static readonly IBrush PressFallback = new SolidColorBrush(PressColour);
    private static readonly IBrush PressGlow = new SolidColorBrush(PressColour, 0.32);

    private static readonly Palette APalette = Palette.For(AColour);
    private static readonly Palette BPalette = Palette.For(BColour);
    private static readonly Palette XPalette = Palette.For(XColour);
    private static readonly Palette YPalette = Palette.For(YColour);

    private static readonly IBrush InkBrush = new SolidColorBrush(Color.FromRgb(0x2B, 0x34, 0x40));
    private static readonly IBrush GuideFill = new SolidColorBrush(Color.FromRgb(0xFA, 0xFC, 0xFE));
    private static readonly IBrush GuideGlow = new SolidColorBrush(Color.FromRgb(0x4F, 0xA8, 0xF5), 0.30);

    private static readonly Pen InkPen = new(InkBrush, 1.6);
    private static readonly Pen InkThinPen = new(InkBrush, 1.2);
    private static readonly Pen PressRingPen = new(Brushes.White, 1.8);

    private static readonly Pen GlyphPen =
        new(InkBrush, 2.2) { LineCap = PenLineCap.Round };

    private static readonly Pen GlyphPressedPen =
        new(Brushes.White, 2.2) { LineCap = PenLineCap.Round };

    private static readonly Typeface LabelTypeface =
        new("Segoe UI", FontStyle.Normal, FontWeight.Bold);

    // ---------------------------------------------------------------- layout

    private static readonly Point LeftStickCentre = new(104, 116);
    private static readonly Point RightStickCentre = new(214, 156);
    private static readonly Point DPadCentre = new(144, 158);
    private static readonly Point FaceCentre = new(262, 116);
    private static readonly Point GuideCentre = new(CentreX, 86);
    private static readonly Point ViewCentre = new(154, 120);
    private static readonly Point MenuCentre = new(206, 120);

    private const double LeftWellRadius = 26;
    private const double LeftCapRadius = 17;
    private const double RightWellRadius = 23;
    private const double RightCapRadius = 15;

    private const double FaceOffset = 26;
    private const double FaceRadius = 15;

    private const double DPadArm = 15;
    private const double DPadLength = 15;

    private static readonly Rect LeftTriggerRect = new(92, 12, 54, 18);
    private static readonly Rect RightTriggerRect = new(214, 12, 54, 18);
    private static readonly Rect LeftBumperRect = new(78, 34, 62, 18);
    private static readonly Rect RightBumperRect = new(220, 34, 62, 18);
    private static readonly Rect BridgeRect = new(156, 44, 48, 14);

    // ---------------------------------------------------------------- geometry

    /// <summary>Pad silhouette. Constant, so it is built once for the lifetime of the process.</summary>
    private static readonly StreamGeometry BodyGeometry = CreateBodyGeometry();

    /// <summary>D-Pad cross outline, built once for the same reason.</summary>
    private static readonly StreamGeometry DPadGeometry = CreateCrossGeometry();

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

    /// <summary>Colour applied to a pressed element other than a face button.</summary>
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
    /// Replaces the state being drawn and schedules a repaint, but only when something actually
    /// visible has changed. At 60 Hz an idle controller would otherwise repaint the whole diagram
    /// sixty times a second for no reason.
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
    /// Compares exactly the members the renderer reads. Deliberately explicit: the default
    /// comparison for a struct would box both operands on every frame.
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

        // Uniform scale with letterboxing, so the pad never distorts and never clips.
        var scale = Math.Min(Bounds.Width / DesignWidth, Bounds.Height / DesignHeight);
        var offsetX = (Bounds.Width - (DesignWidth * scale)) / 2;
        var offsetY = (Bounds.Height - (DesignHeight * scale)) / 2;

        using var _ = context.PushTransform(
            Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(offsetX, offsetY));

        var body = BodyBrush ?? Brushes.WhiteSmoke;
        var detail = DetailBrush ?? Brushes.DimGray;
        var press = HighlightBrush ?? PressFallback;

        // Shoulders and the centre bridge go first: the body is painted over them so they tuck in
        // behind the top edge the way they sit on the real hardware.
        DrawShoulder(context, LeftTriggerRect, "LT", _state.LeftTrigger, body, press);
        DrawShoulder(context, RightTriggerRect, "RT", _state.RightTrigger, body, press);
        DrawBumper(context, LeftBumperRect, "LB", _state.LeftBumper, body, press);
        DrawBumper(context, RightBumperRect, "RB", _state.RightBumper, body, press);

        context.DrawRectangle(body, InkThinPen, new RoundedRect(BridgeRect, 7));

        context.DrawGeometry(body, InkPen, BodyGeometry);

        DrawGuide(context, in _state, press);
        DrawCentreButtons(context, in _state, detail, press);
        DrawFaceButtons(context, in _state);
        DrawDPad(context, in _state, body, press);

        DrawStick(
            context, LeftStickCentre, _state.LeftThumbX, _state.LeftThumbY, _state.LeftThumb,
            body, press, LeftWellRadius, LeftCapRadius);

        DrawStick(
            context, RightStickCentre, _state.RightThumbX, _state.RightThumbY, _state.RightThumb,
            body, press, RightWellRadius, RightCapRadius);
    }

    // ---------------------------------------------------------------- shoulders

    /// <summary>A trigger fills proportionally to how far it is pressed.</summary>
    private static void DrawShoulder(
        DrawingContext context, Rect rect, string label, byte value, IBrush body, IBrush press)
    {
        context.DrawRectangle(body, InkThinPen, new RoundedRect(rect, 6));

        var fraction = value / 255.0;
        if (fraction > 0.01)
        {
            var fill = new Rect(
                rect.X + 1.5, rect.Y + 1.5, (rect.Width - 3) * fraction, rect.Height - 3);

            context.DrawRectangle(press, null, new RoundedRect(fill, 5));
        }

        DrawCentredText(context, label, rect.Center, 10, fraction > 0.5 ? Brushes.White : InkBrush);
    }

    private static void DrawBumper(
        DrawingContext context, Rect rect, string label, bool pressed, IBrush body, IBrush press)
    {
        context.DrawRectangle(
            pressed ? press : body,
            pressed ? PressRingPen : InkThinPen,
            new RoundedRect(rect, rect.Height / 2));

        DrawCentredText(context, label, rect.Center, 10, pressed ? Brushes.White : InkBrush);
    }

    // ---------------------------------------------------------------- face buttons

    private static void DrawFaceButtons(DrawingContext context, in PadState state)
    {
        // Diamond arrangement: Y top, X left, B right, A bottom.
        DrawFaceButton(
            context, new Point(FaceCentre.X, FaceCentre.Y - FaceOffset), state.Y, YPalette, "Y");

        DrawFaceButton(
            context, new Point(FaceCentre.X - FaceOffset, FaceCentre.Y), state.X, XPalette, "X");

        DrawFaceButton(
            context, new Point(FaceCentre.X + FaceOffset, FaceCentre.Y), state.B, BPalette, "B");

        DrawFaceButton(
            context, new Point(FaceCentre.X, FaceCentre.Y + FaceOffset), state.A, APalette, "A");
    }

    /// <summary>
    /// A face button always wears its own colour, as on the real pad. Pressing it brightens the
    /// fill and adds a halo plus a white rim, which reads clearly without changing the hue.
    /// </summary>
    private static void DrawFaceButton(
        DrawingContext context, Point centre, bool pressed, Palette palette, string label)
    {
        if (pressed)
        {
            context.DrawEllipse(palette.Glow, null, centre, FaceRadius + 6, FaceRadius + 6);
        }

        context.DrawEllipse(
            pressed ? palette.Bright : palette.Vivid,
            pressed ? PressRingPen : InkThinPen,
            centre, FaceRadius, FaceRadius);

        DrawCentredText(context, label, centre, 15, Brushes.White);
    }

    // ---------------------------------------------------------------- centre cluster

    private static void DrawGuide(DrawingContext context, in PadState state, IBrush press)
    {
        const double Radius = 13;

        context.DrawEllipse(
            state.Guide ? PressGlow : GuideGlow, null, GuideCentre, Radius + 6, Radius + 6);

        context.DrawEllipse(
            state.Guide ? press : GuideFill,
            state.Guide ? PressRingPen : InkThinPen,
            GuideCentre, Radius, Radius);

        // Stylised Xbox sphere: two crossed strokes.
        var pen = state.Guide ? GlyphPressedPen : GlyphPen;
        const double Arm = 4.6;

        context.DrawLine(
            pen,
            new Point(GuideCentre.X - Arm, GuideCentre.Y - Arm),
            new Point(GuideCentre.X + Arm, GuideCentre.Y + Arm));

        context.DrawLine(
            pen,
            new Point(GuideCentre.X + Arm, GuideCentre.Y - Arm),
            new Point(GuideCentre.X - Arm, GuideCentre.Y + Arm));
    }

    private static void DrawCentreButtons(
        DrawingContext context, in PadState state, IBrush detail, IBrush press)
    {
        DrawSmallButton(context, ViewCentre, state.Back, detail, press);
        DrawSmallButton(context, MenuCentre, state.Start, detail, press);

        // View: two overlapping panes.
        var viewPen = new Pen(state.Back ? Brushes.White : InkBrush, 1.1);
        context.DrawRectangle(null, viewPen, new Rect(ViewCentre.X - 3.6, ViewCentre.Y - 2.8, 4.8, 4.8));
        context.DrawRectangle(null, viewPen, new Rect(ViewCentre.X - 1.2, ViewCentre.Y - 0.6, 4.8, 4.8));

        // Menu: three stacked lines.
        var menuPen = new Pen(state.Start ? Brushes.White : InkBrush, 1.2);
        for (var i = -1; i <= 1; i++)
        {
            context.DrawLine(
                menuPen,
                new Point(MenuCentre.X - 3.6, MenuCentre.Y + (i * 2.5)),
                new Point(MenuCentre.X + 3.6, MenuCentre.Y + (i * 2.5)));
        }
    }

    private static void DrawSmallButton(
        DrawingContext context, Point centre, bool pressed, IBrush detail, IBrush press)
    {
        const double Radius = 8;

        if (pressed)
        {
            context.DrawEllipse(PressGlow, null, centre, Radius + 5, Radius + 5);
        }

        context.DrawEllipse(
            pressed ? press : detail,
            pressed ? PressRingPen : InkThinPen,
            centre, Radius, Radius);
    }

    // ---------------------------------------------------------------- d-pad

    private static void DrawDPad(DrawingContext context, in PadState state, IBrush body, IBrush press)
    {
        const double Half = DPadArm / 2;

        context.DrawGeometry(body, InkPen, DPadGeometry);

        // Pressed arms are filled inside the cross, stopping short of the hub so the silhouette of
        // the cross stays readable.
        if (state.DPadUp)
        {
            context.DrawRectangle(press, null, new Rect(
                DPadCentre.X - Half + 1.6, DPadCentre.Y - Half - DPadLength + 1.6,
                DPadArm - 3.2, DPadLength));
        }

        if (state.DPadDown)
        {
            context.DrawRectangle(press, null, new Rect(
                DPadCentre.X - Half + 1.6, DPadCentre.Y + Half - 1.6,
                DPadArm - 3.2, DPadLength));
        }

        if (state.DPadLeft)
        {
            context.DrawRectangle(press, null, new Rect(
                DPadCentre.X - Half - DPadLength + 1.6, DPadCentre.Y - Half + 1.6,
                DPadLength, DPadArm - 3.2));
        }

        if (state.DPadRight)
        {
            context.DrawRectangle(press, null, new Rect(
                DPadCentre.X + Half - 1.6, DPadCentre.Y - Half + 1.6,
                DPadLength, DPadArm - 3.2));
        }

        // Arrow glyphs. White on a pressed arm, ink on a resting one.
        var tip = Half + DPadLength - 3.6;
        const double Wing = 3.6;
        const double Depth = 4.6;

        DrawTriangle(context, state.DPadUp ? Brushes.White : InkBrush,
            new Point(DPadCentre.X, DPadCentre.Y - tip),
            new Point(DPadCentre.X - Wing, DPadCentre.Y - tip + Depth),
            new Point(DPadCentre.X + Wing, DPadCentre.Y - tip + Depth));

        DrawTriangle(context, state.DPadDown ? Brushes.White : InkBrush,
            new Point(DPadCentre.X, DPadCentre.Y + tip),
            new Point(DPadCentre.X - Wing, DPadCentre.Y + tip - Depth),
            new Point(DPadCentre.X + Wing, DPadCentre.Y + tip - Depth));

        DrawTriangle(context, state.DPadLeft ? Brushes.White : InkBrush,
            new Point(DPadCentre.X - tip, DPadCentre.Y),
            new Point(DPadCentre.X - tip + Depth, DPadCentre.Y - Wing),
            new Point(DPadCentre.X - tip + Depth, DPadCentre.Y + Wing));

        DrawTriangle(context, state.DPadRight ? Brushes.White : InkBrush,
            new Point(DPadCentre.X + tip, DPadCentre.Y),
            new Point(DPadCentre.X + tip - Depth, DPadCentre.Y - Wing),
            new Point(DPadCentre.X + tip - Depth, DPadCentre.Y + Wing));
    }

    // ---------------------------------------------------------------- sticks

    private static void DrawStick(
        DrawingContext context, Point centre, short valueX, short valueY, bool pressed,
        IBrush body, IBrush press, double wellRadius, double capRadius)
    {
        var maxTravel = wellRadius - capRadius + 3;

        // Moulded collar.
        context.DrawEllipse(body, InkPen, centre, wellRadius, wellRadius);

        // Cap, offset by the live stick position. Screen Y is inverted relative to the pad.
        var offsetX = Math.Clamp(valueX / 32767.0, -1, 1) * maxTravel;
        var offsetY = -Math.Clamp(valueY / 32767.0, -1, 1) * maxTravel;
        var capCentre = new Point(centre.X + offsetX, centre.Y + offsetY);

        if (pressed)
        {
            context.DrawEllipse(PressGlow, null, capCentre, capRadius + 5, capRadius + 5);
        }

        context.DrawEllipse(
            pressed ? press : body,
            pressed ? PressRingPen : InkPen,
            capCentre, capRadius, capRadius);

        context.DrawEllipse(null, InkThinPen, capCentre, capRadius - 5, capRadius - 5);
    }

    // ---------------------------------------------------------------- primitives

    private static void DrawTriangle(DrawingContext context, IBrush brush, Point a, Point b, Point c)
    {
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(a, isFilled: true);
            ctx.LineTo(b);
            ctx.LineTo(c);
            ctx.EndFigure(isClosed: true);
        }

        context.DrawGeometry(brush, null, geometry);
    }

    private static void DrawCentredText(
        DrawingContext context, string label, Point centre, double size, IBrush brush)
    {
        var text = new FormattedText(
            label,
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            LabelTypeface,
            size,
            brush);

        context.DrawText(text, new Point(centre.X - (text.Width / 2), centre.Y - (text.Height / 2)));
    }

    /// <summary>
    /// Pad silhouette: two shoulder humps, a wide waist and two grips. The right half is authored
    /// and the left half is its mirror, so the shape cannot drift out of symmetry.
    /// </summary>
    private static StreamGeometry CreateBodyGeometry()
    {
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(new Point(CentreX, 56), isFilled: true);

            // ---- right half, top to bottom ----
            ctx.CubicBezierTo(new Point(220, 50), new Point(258, 52), new Point(286, 64));
            ctx.CubicBezierTo(new Point(318, 78), new Point(338, 106), new Point(342, 136));
            ctx.CubicBezierTo(new Point(350, 178), new Point(338, 228), new Point(306, 246));
            ctx.CubicBezierTo(new Point(282, 258), new Point(258, 242), new Point(246, 214));
            ctx.CubicBezierTo(new Point(238, 196), new Point(226, 188), new Point(208, 186));
            ctx.LineTo(new Point(CentreX, 186));

            // ---- left half, mirrored and traversed back upwards ----
            ctx.LineTo(new Point(152, 186));
            ctx.CubicBezierTo(new Point(134, 188), new Point(122, 196), new Point(114, 214));
            ctx.CubicBezierTo(new Point(102, 242), new Point(78, 258), new Point(54, 246));
            ctx.CubicBezierTo(new Point(22, 228), new Point(10, 178), new Point(18, 136));
            ctx.CubicBezierTo(new Point(22, 106), new Point(42, 78), new Point(74, 64));
            ctx.CubicBezierTo(new Point(102, 52), new Point(140, 50), new Point(CentreX, 56));

            ctx.EndFigure(isClosed: true);
        }

        return geometry;
    }

    private static StreamGeometry CreateCrossGeometry()
    {
        const double Half = DPadArm / 2;
        var cx = DPadCentre.X;
        var cy = DPadCentre.Y;

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(new Point(cx - Half, cy - Half), isFilled: true);
            ctx.LineTo(new Point(cx - Half, cy - Half - DPadLength));
            ctx.LineTo(new Point(cx + Half, cy - Half - DPadLength));
            ctx.LineTo(new Point(cx + Half, cy - Half));
            ctx.LineTo(new Point(cx + Half + DPadLength, cy - Half));
            ctx.LineTo(new Point(cx + Half + DPadLength, cy + Half));
            ctx.LineTo(new Point(cx + Half, cy + Half));
            ctx.LineTo(new Point(cx + Half, cy + Half + DPadLength));
            ctx.LineTo(new Point(cx - Half, cy + Half + DPadLength));
            ctx.LineTo(new Point(cx - Half, cy + Half));
            ctx.LineTo(new Point(cx - Half - DPadLength, cy + Half));
            ctx.LineTo(new Point(cx - Half - DPadLength, cy - Half));
            ctx.EndFigure(isClosed: true);
        }

        return geometry;
    }

    /// <summary>Pre-built brushes for one face button, so rendering allocates nothing per frame.</summary>
    private sealed record Palette(IBrush Vivid, IBrush Bright, IBrush Glow)
    {
        /// <summary>Builds the resting, pressed and halo brushes for a face button colour.</summary>
        public static Palette For(Color colour) => new(
            new SolidColorBrush(colour),
            new SolidColorBrush(Lighten(colour, 0.35)),
            new SolidColorBrush(colour, 0.35));

        private static Color Lighten(Color colour, double amount) => Color.FromRgb(
            (byte)(colour.R + ((255 - colour.R) * amount)),
            (byte)(colour.G + ((255 - colour.G) * amount)),
            (byte)(colour.B + ((255 - colour.B) * amount)));
    }
}
