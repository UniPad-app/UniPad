using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using UniPad.Core.Mapping;

namespace UniPad.App.Views.Controls;

/// <summary>
/// Xbox-style controller traced in a 1024 x 1024 design space from the supplied reference.
/// All artwork is vector geometry; no image assets or additional packages are required.
/// The background is transparent, just like the reference PNG. Set the parent's background
/// to black for the same appearance as the supplied preview.
/// Leave BodyBrush, DetailBrush and OutlineBrush unset to use the reference palette.
/// Call SetState on the UI thread. Existing PadState inputs and brush properties are preserved.
/// </summary>
public sealed class ControllerPreview : Control
{
    public static readonly StyledProperty<IBrush?> BodyBrushProperty =
        AvaloniaProperty.Register<ControllerPreview, IBrush?>(nameof(BodyBrush));
    public static readonly StyledProperty<IBrush?> DetailBrushProperty =
        AvaloniaProperty.Register<ControllerPreview, IBrush?>(nameof(DetailBrush));
    public static readonly StyledProperty<IBrush?> HighlightBrushProperty =
        AvaloniaProperty.Register<ControllerPreview, IBrush?>(nameof(HighlightBrush));
    public static readonly StyledProperty<IBrush?> OutlineBrushProperty =
        AvaloniaProperty.Register<ControllerPreview, IBrush?>(nameof(OutlineBrush));

    // Layout values precede every geometry/brush initializer that uses them.
    private static readonly Point LeftStickCentre = new(284, 463);
    private static readonly Point RightStickCentre = new(637.5, 599.5);
    private static readonly Point GuideCentre = new(517.5, 370.5);
    private static readonly Point ViewCentre = new(454, 465);
    private static readonly Point MenuCentre = new(581.5, 465);
    private static readonly Point ACentre = new(761.5, 531.5);
    private static readonly Point BCentre = new(822, 468);
    private static readonly Point XCentre = new(699, 468);
    private static readonly Point YCentre = new(761.5, 407.5);
    private static readonly Rect LeftTriggerRect = new(309, 211, 58, 53);
    private static readonly Rect RightTriggerRect = new(663, 213, 59, 54);
    private static readonly Rect LeftBumperRect = new(194, 236, 58, 41);
    private static readonly Rect RightBumperRect = new(773, 236, 57, 41);
    private const double FaceRadius = 31.5;
    private const double WellRadius = 82;
    private const double CapRadius = 50.5;
    
    private static readonly Rect ControllerBounds = new(79, 211, 877, 654);

    private static readonly Color PressColour = Color.Parse("#FF8C1A");
    private static readonly IBrush PressFallback = new SolidColorBrush(PressColour);
    private static readonly IBrush InkBrush = new SolidColorBrush(Color.Parse("#252D38"));
    private static readonly IBrush SilverFill = Linear("#D5D6D8", "#CECFD1", "#D4D5D7");
    private static readonly IBrush WhiteFill = Linear("#FFFFFF", "#F4F4F5", "#FFFFFF");
    private static readonly IBrush StickFill = Linear("#FEFEFE", "#F5F5F6", "#FFFFFF");
    private static readonly IBrush GuideFill = Linear("#555655", "#4B4D4B", "#555755");
    private static readonly IBrush GuideHalo = Halo(Color.Parse("#46BDF5"));
    private static readonly Palette APalette = Palette.For("#329F38");
    private static readonly Palette BPalette = Palette.For("#D84B47");
    private static readonly Palette XPalette = Palette.For("#4C91CF");
    private static readonly Palette YPalette = Palette.For("#F6CF3E");
    private static readonly Pen WhiteRing = new(Brushes.White, 3);

    // The silhouette is deliberately not forcibly mirrored: the reference has subtle asymmetry.
    private static readonly Geometry BodyGeometry = Geometry.Parse(
        "M 388,306 L 646,306 C 662,303 674,287 695,287 " +
        "C 719,286 794,312 817,330 C 829,339 832,350 835,361 " +
        "C 850,374 860,394 869,418 C 897,490 947,679 956,769 " +
        "C 961,804 943,840 916,855 C 900,865 846,865 820,855 " +
        "C 789,843 749,800 714,766 C 670,724 650,718 578,718 " +
        "L 461,718 C 393,718 368,724 337,748 " +
        "C 302,775 268,817 223,847 C 198,866 159,865 130,859 " +
        "C 100,854 76,817 79,777 C 83,689 133,502 163,423 " +
        "C 172,396 180,377 199,361 C 202,344 207,334 222,326 " +
        "C 247,310 311,287 336,287 C 354,284 373,299 388,306 Z");

    private static readonly Geometry TopShellGeometry = Geometry.Parse(
        "M 199,361 C 201,344 207,334 222,326 " +
        "C 247,310 311,287 336,287 C 354,284 373,299 388,306 " +
        "L 646,306 C 662,303 674,287 695,287 C 719,286 794,312 817,330 " +
        "C 829,339 832,350 835,361 C 813,347 748,323 714,315 " +
        "C 690,308 680,317 661,322 L 375,322 C 359,321 353,309 330,313 " +
        "C 295,317 231,344 199,361 Z");

    private static readonly Geometry CentrePanelGeometry = Geometry.Parse(
        "M 199,361 C 231,344 295,317 330,313 " +
        "C 353,309 359,321 375,322 L 661,322 C 680,317 690,308 714,315 " +
        "C 748,323 813,347 835,361 C 845,370 851,380 856,390 " +
        "C 828,362 763,341 720,329 C 701,324 689,324 679,332 " +
        "C 656,351 618,397 604,407 C 597,413 586,416 517,416 " +
        "C 480,416 443,414 434,410 C 420,405 373,348 351,332 " +
        "C 338,319 312,326 293,331 C 240,346 199,362 178,393 " +
        "C 183,379 188,372 199,361 Z");

    private static readonly Geometry LowerRimGeometry = Geometry.Parse(
        "M 151,861 C 195,856 248,806 289,766 C 323,733 344,716 371,708 " +
        "C 400,699 431,699 469,699 L 570,699 C 620,699 649,703 672,715 " +
        "C 713,736 745,777 780,807 C 816,838 851,860 883,861 " +
        "C 861,865 836,862 820,855 C 789,843 749,800 714,766 " +
        "C 670,724 650,718 578,718 L 461,718 C 393,718 368,724 337,748 " +
        "C 302,775 268,817 223,847 C 204,861 177,864 151,861 Z");

    private static readonly Geometry LeftTriggerGeometry = Geometry.Parse(
        "M 315,212 L 357,212 C 364,212 367,218 367,225 " +
        "L 366,239 C 364,254 352,263 338,264 L 309,264 " +
        "C 319,252 326,232 315,214 Z");
    private static readonly Geometry RightTriggerGeometry = Geometry.Parse(
        "M 674,214 L 716,214 C 706,232 709,252 722,266 L 695,266 " +
        "C 677,266 664,254 664,240 L 664,226 C 664,219 668,214 674,214 Z");
    private static readonly Geometry LeftBumperGeometry = Geometry.Parse(
        "M 199,248 C 208,242 227,236 239,236 L 251,247 " +
        "L 247,273 Q 247,277 243,277 L 199,277 Q 193,277 194,271 Z");
    private static readonly Geometry RightBumperGeometry = Geometry.Parse(
        "M 785,236 C 799,236 817,241 825,248 L 830,271 " +
        "Q 831,277 825,277 L 780,277 Q 776,277 776,272 L 773,248 Z");

    private static readonly Geometry DPadGeometry = Geometry.Parse(
        "M 378,535 Q 401,527 425,535 L 425,573 Q 425,580 432,580 " +
        "L 468,580 Q 477,603 469,626 L 432,626 Q 425,626 425,633 " +
        "L 425,669 Q 401,678 378,669 L 378,633 Q 378,626 371,626 " +
        "L 335,626 Q 326,603 335,580 L 371,580 Q 378,580 378,573 Z");
    private static readonly Geometry UpArrow = Geometry.Parse(
        "M 400,538 Q 402,536 404,539 L 416,556 Q 418,560 413,560 " +
        "L 390,560 Q 385,560 387,556 Z");
    private static readonly Geometry DownArrow = Geometry.Parse(
        "M 387,648 Q 385,645 390,645 L 413,645 Q 418,645 416,649 " +
        "L 404,665 Q 402,668 400,665 Z");
    private static readonly Geometry LeftArrow = Geometry.Parse(
        "M 339,600 L 357,588 Q 360,586 360,591 L 360,614 " +
        "Q 360,619 356,616 L 339,605 Q 336,603 339,600 Z");
    private static readonly Geometry RightArrow = Geometry.Parse(
        "M 443,591 Q 443,586 447,589 L 464,600 Q 467,603 464,605 " +
        "L 447,617 Q 443,619 443,614 Z");

    // Curved ribbons form the white Xbox mark, not a plain typographic X.
    private static readonly Geometry GuideMark = Geometry.Parse(
        "M 490,344 C 498,340 508,343 517,349 C 526,343 537,340 545,344 " +
        "C 536,345 530,351 525,355 C 539,369 547,383 545,395 " +
        "C 539,381 528,369 517,362 C 506,370 496,382 490,396 " +
        "C 487,383 496,366 509,355 C 502,349 496,345 490,344 Z");

    // Font-independent letter outlines from the reference.
    private static readonly Geometry LabelLT = Geometry.Parse(
        "M 339.5,247 L 328.5,247 L 328,246.5 L 328,225.5 L 328.5,225 L 331.5,225 L 332,225.5 L 332,242.5 L 332.5,243 L 340.5,243 L 341,243.5 L 341,245.5 Z M 351.5,247 L 347.5,247 L 347,246.5 L 347,229.5 L 346.5,229 L 341.5,229 L 341,228.5 L 341,226.5 L 341.5,226 L 357.5,226 L 358,226.5 L 358,228.5 L 357.5,229 L 352.5,229 L 352,229.5 L 352,246.5 Z");
    private static readonly Geometry LabelRT = Geometry.Parse(
        "M 687.5,249 L 683.5,249 L 682,247.5 L 681,244.5 L 677.5,241 L 676,241.5 L 676,248.5 L 675.5,249 L 671.5,249 L 671,248.5 L 671,229.5 L 671.5,229 L 683.5,229 L 686,231.5 L 686,236.5 L 682,240.5 L 686,244.5 L 686,245.5 L 688,248.5 Z M 698.5,249 L 694.5,249 L 694,248.5 L 694,232.5 L 693.5,232 L 689.5,232 L 689,231.5 L 689,229.5 L 689.5,229 L 704.5,229 L 705,229.5 L 705,231.5 L 704.5,232 L 699.5,232 L 699,232.5 L 699,248.5 Z M 680,237.5 L 682,234.5 L 681,233.5 L 681,232.5 L 680.5,232 L 676.5,232 L 676,232.5 L 676,237.5 L 676.5,238 L 679.5,238 Z");
    private static readonly Geometry LabelLB = Geometry.Parse(
        "M 214.5,270 L 213.5,269 L 210.5,269 L 210,268.5 L 210,249.5 L 210.5,249 L 213.5,249 L 214,249.5 L 214,265.5 L 214.5,266 L 221.5,266 L 222,266.5 L 222,268.5 L 221.5,269 L 215.5,269 Z M 237.5,269 L 225.5,269 L 225,268.5 L 225,249.5 L 225.5,249 L 236.5,249 L 240,251.5 L 240,254.5 L 239,255.5 L 239,256.5 L 237,257.5 L 237,258.5 L 238.5,259 L 240,260.5 L 240,261.5 L 241,262.5 L 241,264.5 L 240,265.5 L 240,266.5 Z M 234,256.5 L 235,255.5 L 235,253.5 L 233.5,252 L 230.5,252 L 230,252.5 L 230,256.5 L 230.5,257 L 233.5,257 Z M 235,265.5 L 236,264.5 L 236,261.5 L 233.5,260 L 230.5,260 L 230,260.5 L 230,265.5 L 230.5,266 L 234.5,266 Z");
    private static readonly Geometry LabelRB = Geometry.Parse(
        "M 799.5,269 L 795.5,269 L 794,267.5 L 793,264.5 L 789.5,261 L 788,261.5 L 788,268.5 L 787.5,269 L 783.5,269 L 783,268.5 L 783,249.5 L 783.5,249 L 794.5,249 L 798,251.5 L 798,256.5 L 794,260.5 L 798,264.5 L 798,265.5 L 800,268.5 Z M 814.5,269 L 803.5,269 L 803,268.5 L 803,249.5 L 803.5,249 L 814.5,249 L 818,252.5 L 817,255.5 L 814,258.5 L 814.5,259 L 816.5,259 L 818,260.5 L 818,265.5 L 817,266.5 L 817,267.5 Z M 792,257.5 L 794,254.5 L 791.5,252 L 788.5,252 L 788,252.5 L 788,257.5 L 788.5,258 L 791.5,258 Z M 812,256.5 L 813,255.5 L 813,253.5 L 811.5,252 L 807.5,252 L 807,252.5 L 807,256.5 L 807.5,257 L 811.5,257 Z M 813,265.5 L 814,264.5 L 814,262.5 L 811.5,260 L 808.5,260 L 807,261.5 L 807,265.5 L 807.5,266 L 812.5,266 Z");
    private static readonly Geometry LabelA = Geometry.Parse(
        "M 777.5,547 L 770.5,547 L 769,544.5 L 769,542.5 L 768,541.5 L 768,540.5 L 766.5,539 L 755.5,539 L 755,539.5 L 755,541.5 L 754,542.5 L 754,544.5 L 752.5,547 L 744.5,547 L 744,546.5 L 745,545.5 L 745,543.5 L 747,540.5 L 747,538.5 L 749,535.5 L 749,533.5 L 750,532.5 L 750,530.5 L 752,527.5 L 752,525.5 L 753,524.5 L 753,522.5 L 754,521.5 L 754,519.5 L 756,516.5 L 756,514.5 L 757.5,513 L 765.5,513 L 767,514.5 L 767,516.5 L 768,517.5 L 768,519.5 L 770,522.5 L 770,524.5 L 771,525.5 L 771,527.5 L 772,528.5 L 772,530.5 L 774,533.5 L 774,535.5 L 775,536.5 L 775,538.5 L 777,541.5 L 777,543.5 L 778,544.5 L 778,546.5 Z M 761,533.5 L 761.5,533 L 764.5,533 L 765,532.5 L 765,530.5 L 764,529.5 L 764,527.5 L 763,526.5 L 763,524.5 L 762,523.5 L 762,521.5 L 761.5,521 L 760,523.5 L 760,525.5 L 759,526.5 L 759,528.5 L 757,531.5 L 757,532.5 L 758.5,534 L 760.5,534 Z");
    private static readonly Geometry LabelB = Geometry.Parse(
        "M 829.5,484 L 811.5,484 L 811,483.5 L 811,451.5 L 811.5,451 L 826.5,451 L 827.5,452 L 829.5,452 L 830.5,453 L 831.5,453 L 834,455.5 L 834,457.5 L 835,458.5 L 835,459.5 L 834,460.5 L 834,462.5 L 831.5,465 L 830.5,465 L 829,466.5 L 829.5,467 L 831.5,467 L 835,470.5 L 835,471.5 L 836,472.5 L 836,477.5 L 835,478.5 L 835,479.5 L 831.5,483 L 830.5,483 Z M 823,464.5 L 824.5,464 L 827,461.5 L 827,459.5 L 824.5,457 L 818.5,457 L 818,457.5 L 818,464.5 L 818.5,465 L 822.5,465 Z M 826,478.5 L 828,476.5 L 828,472.5 L 825.5,470 L 818.5,470 L 818,470.5 L 818,478.5 L 818.5,479 L 825.5,479 Z");
    private static readonly Geometry LabelX = Geometry.Parse(
        "M 712.5,484 L 705.5,484 L 704,482.5 L 704,481.5 L 703,480.5 L 703,479.5 L 702,478.5 L 702,477.5 L 700,474.5 L 700,473.5 L 698.5,472 L 697,474.5 L 697,475.5 L 696,476.5 L 696,477.5 L 694,479.5 L 693,482.5 L 691.5,484 L 684.5,484 L 684,483.5 L 684,482.5 L 688,477.5 L 689,474.5 L 691,472.5 L 691,471.5 L 693,469.5 L 693,468.5 L 694,467.5 L 693,466.5 L 693,465.5 L 691,463.5 L 690,460.5 L 689,459.5 L 689,458.5 L 687,456.5 L 687,455.5 L 685,452.5 L 685.5,451 L 692.5,451 L 695,454.5 L 695,455.5 L 698,460.5 L 698,462.5 L 698.5,463 L 700,461.5 L 700,460.5 L 702,458.5 L 702,457.5 L 703,456.5 L 703,455.5 L 704,454.5 L 704,453.5 L 705.5,451 L 712.5,451 L 713,451.5 L 713,452.5 L 711,454.5 L 710,457.5 L 708,459.5 L 707,462.5 L 705,464.5 L 705,465.5 L 704,466.5 L 704,468.5 L 706,470.5 L 707,473.5 L 709,475.5 L 710,478.5 L 712,480.5 L 712,481.5 L 713,482.5 Z");
    private static readonly Geometry LabelY = Geometry.Parse(
        "M 764.5,425 L 758.5,425 L 758,424.5 L 758,412.5 L 757,411.5 L 757,410.5 L 756,409.5 L 755,406.5 L 753,403.5 L 753,402.5 L 751,400.5 L 751,398.5 L 749,396.5 L 749,395.5 L 748,394.5 L 748,392.5 L 748.5,392 L 755.5,392 L 756,392.5 L 756,393.5 L 758,396.5 L 758,398.5 L 761,403.5 L 761,405.5 L 761.5,406 L 763,404.5 L 763,403.5 L 765,400.5 L 765,398.5 L 766,397.5 L 766,396.5 L 767,395.5 L 767,394.5 L 768.5,392 L 775.5,392 L 776,392.5 L 775,393.5 L 775,394.5 L 774,395.5 L 774,396.5 L 771,401.5 L 770,404.5 L 768,406.5 L 768,407.5 L 765,412.5 L 765,424.5 Z");

    private PadState _state;

    static ControllerPreview()
    {
        AffectsRender<ControllerPreview>(BodyBrushProperty, DetailBrushProperty,
            HighlightBrushProperty, OutlineBrushProperty);
    }

    public ref readonly PadState State => ref _state;
    public IBrush? BodyBrush
    {
        get => GetValue(BodyBrushProperty);
        set => SetValue(BodyBrushProperty, value);
    }
    public IBrush? DetailBrush
    {
        get => GetValue(DetailBrushProperty);
        set => SetValue(DetailBrushProperty, value);
    }
    public IBrush? HighlightBrush
    {
        get => GetValue(HighlightBrushProperty);
        set => SetValue(HighlightBrushProperty, value);
    }
    public IBrush? OutlineBrush
    {
        get => GetValue(OutlineBrushProperty);
        set => SetValue(OutlineBrushProperty, value);
    }

    public void SetState(in PadState state)
    {
        if (!HasVisibleChange(in _state, in state))
            return;
        _state = state;
        InvalidateVisual();
    }

    private static bool HasVisibleChange(in PadState a, in PadState b) =>
        a.A != b.A || a.B != b.B || a.X != b.X || a.Y != b.Y
        || a.LeftBumper != b.LeftBumper || a.RightBumper != b.RightBumper
        || a.LeftTrigger != b.LeftTrigger || a.RightTrigger != b.RightTrigger
        || a.DPadUp != b.DPadUp || a.DPadDown != b.DPadDown
        || a.DPadLeft != b.DPadLeft || a.DPadRight != b.DPadRight
        || a.LeftThumb != b.LeftThumb || a.RightThumb != b.RightThumb
        || a.LeftThumbX != b.LeftThumbX || a.LeftThumbY != b.LeftThumbY
        || a.RightThumbX != b.RightThumbX || a.RightThumbY != b.RightThumbY
        || a.Back != b.Back || a.Start != b.Start || a.Guide != b.Guide;

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (Bounds.Width <= 10 || Bounds.Height <= 10)
            return;

        // Scale only the actual controller bounds so it fills the available space.
        var scale = Math.Min(
            Bounds.Width / ControllerBounds.Width,
            Bounds.Height / ControllerBounds.Height);

        // Center the controller, then shift so that ControllerBounds maps to the visible area.
        var offsetX = (Bounds.Width - ControllerBounds.Width * scale) / 2
                    - ControllerBounds.X * scale;
        var offsetY = (Bounds.Height - ControllerBounds.Height * scale) / 2
                    - ControllerBounds.Y * scale;

        using var transform = context.PushTransform(
            Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(offsetX, offsetY));

        var body = BodyBrush ?? SilverFill;
        var detail = DetailBrush ?? WhiteFill;
        var ink = OutlineBrush ?? InkBrush;
        var press = HighlightBrush ?? PressFallback;
        var outline = new Pen(ink, 3);
        var thin = new Pen(ink, 2);

        DrawTrigger(context, LeftTriggerGeometry, LeftTriggerRect, "LT", _state.LeftTrigger, detail, ink, press, thin);
        DrawTrigger(context, RightTriggerGeometry, RightTriggerRect, "RT", _state.RightTrigger, detail, ink, press, thin);
        DrawBumper(context, LeftBumperGeometry, LeftBumperRect, "LB", _state.LeftBumper, detail, ink, press, thin);
        DrawBumper(context, RightBumperGeometry, RightBumperRect, "RB", _state.RightBumper, detail, ink, press, thin);

        context.DrawGeometry(body, thin, BodyGeometry);
        context.DrawGeometry(detail, outline, TopShellGeometry);
        context.DrawGeometry(detail, outline, CentrePanelGeometry);
        context.DrawLine(outline, new Point(388,306), new Point(388,322));
        context.DrawLine(outline, new Point(647,306), new Point(647,322));
        context.DrawGeometry(detail, outline, LowerRimGeometry);

        DrawGuide(context, press);
        DrawCentreButtons(context, detail, ink, press, outline);
        DrawFaceButton(context, ACentre, _state.A, APalette, "A", ink, outline);
        DrawFaceButton(context, BCentre, _state.B, BPalette, "B", ink, outline);
        DrawFaceButton(context, XCentre, _state.X, XPalette, "X", ink, outline);
        DrawFaceButton(context, YCentre, _state.Y, YPalette, "Y", ink, outline);
        DrawDPad(context, detail, ink, press);
        DrawStick(context, LeftStickCentre, _state.LeftThumbX, _state.LeftThumbY,
            _state.LeftThumb, detail, ink, press, thin);
        DrawStick(context, RightStickCentre, _state.RightThumbX, _state.RightThumbY,
            _state.RightThumb, detail, ink, press, thin);
    }

    private static void DrawTrigger(DrawingContext context, Geometry geometry, Rect bounds,
        string label, byte value, IBrush detail, IBrush ink, IBrush press, Pen outline)
    {
        context.DrawGeometry(detail, null, geometry);
        var fraction = value / 255.0;
        if (value > 0)
        {
            // Clip the proportional fill to the actual concave trigger, not a rounded rectangle.
            using (context.PushGeometryClip(geometry))
                context.DrawRectangle(press, null,
                    new Rect(bounds.X, bounds.Y, bounds.Width * fraction, bounds.Height));
        }
        context.DrawGeometry(null, outline, geometry);
        DrawLabel(context, label, fraction > 0.55 ? Brushes.White : ink);
    }

    private static void DrawBumper(DrawingContext context, Geometry geometry, Rect bounds,
        string label, bool pressed, IBrush detail, IBrush ink, IBrush press, Pen outline)
    {
        context.DrawGeometry(pressed ? press : detail, pressed ? WhiteRing : outline, geometry);
        DrawLabel(context, label, pressed ? Brushes.White : ink);
    }

    private static void DrawFaceButton(DrawingContext context, Point centre, bool pressed,
        Palette palette, string label, IBrush ink, Pen outline)
    {
        if (pressed)
            context.DrawEllipse(palette.Glow, null, centre, FaceRadius + 16, FaceRadius + 16);
        context.DrawEllipse(pressed ? palette.Bright : palette.Vivid,
            pressed ? WhiteRing : outline, centre, FaceRadius, FaceRadius);
        // The reference has large dark letters, not white lettering.
        DrawLabel(context, label, pressed ? Brushes.White : ink);
    }

    private void DrawGuide(DrawingContext context, IBrush press)
    {
        context.DrawEllipse(GuideHalo, null, GuideCentre, 53, 53);
        if (_state.Guide)
            DrawGlow(context, GuideCentre, 58, press);
        context.DrawEllipse(_state.Guide ? press : GuideFill,
            new Pen(Brushes.White, 5), GuideCentre, 36, 36);
        context.DrawGeometry(Brushes.White, null, GuideMark);
    }

    private void DrawCentreButtons(DrawingContext context, IBrush detail,
        IBrush ink, IBrush press, Pen outline)
    {
        DrawSmallButton(context, ViewCentre, _state.Back, detail, press, outline);
        DrawSmallButton(context, MenuCentre, _state.Start, detail, press, outline);
        var viewPen = new Pen(_state.Back ? Brushes.White : ink, 2.5);
        context.DrawRectangle(null, viewPen, new Rect(443, 456, 13, 12));
        // Opaque front window hides the rear window line at their intersection.
        context.DrawRectangle(_state.Back ? press : detail, viewPen, new Rect(451, 463, 13, 12));
        var menuPen = new Pen(_state.Start ? Brushes.White : ink, 2.6)
        {
            LineCap = PenLineCap.Round
        };
        for (var i = -1; i <= 1; i++)
            context.DrawLine(menuPen, new Point(572, 465 + i * 6.5), new Point(591, 465 + i * 6.5));
    }

    private static void DrawSmallButton(DrawingContext context, Point centre, bool pressed,
        IBrush detail, IBrush press, Pen outline)
    {
        if (pressed)
            DrawGlow(context, centre, 34, press);
        context.DrawEllipse(pressed ? press : detail, pressed ? WhiteRing : outline, centre, 22, 22);
    }

    private void DrawDPad(DrawingContext context, IBrush detail, IBrush ink, IBrush press)
    {
        context.DrawGeometry(detail, null, DPadGeometry);
        using (context.PushGeometryClip(DPadGeometry))
        {
            if (_state.DPadUp)
                context.DrawRectangle(press, null, new Rect(377, 529, 49, 51));
            if (_state.DPadDown)
                context.DrawRectangle(press, null, new Rect(377, 626, 49, 50));
            if (_state.DPadLeft)
                context.DrawRectangle(press, null, new Rect(329, 579, 49, 48));
            if (_state.DPadRight)
                context.DrawRectangle(press, null, new Rect(425, 579, 49, 48));
        }
        context.DrawGeometry(null, new Pen(ink, 4), DPadGeometry);
        context.DrawGeometry(_state.DPadUp ? Brushes.White : ink, null, UpArrow);
        context.DrawGeometry(_state.DPadDown ? Brushes.White : ink, null, DownArrow);
        context.DrawGeometry(_state.DPadLeft ? Brushes.White : ink, null, LeftArrow);
        context.DrawGeometry(_state.DPadRight ? Brushes.White : ink, null, RightArrow);
    }

    private void DrawStick(DrawingContext context, Point centre, short valueX, short valueY,
        bool pressed, IBrush detail, IBrush ink, IBrush press, Pen thin)
    {
        context.DrawEllipse(detail, thin, centre, WellRadius, WellRadius);
        var axisX = Math.Clamp(valueX / 32767.0, -1, 1);
        var axisY = -Math.Clamp(valueY / 32767.0, -1, 1);
        // Keep a diagonal cap completely inside its well even for square-range input devices.
        var magnitude = Math.Sqrt(axisX * axisX + axisY * axisY);
        if (magnitude > 1)
        {
            axisX /= magnitude;
            axisY /= magnitude;
        }
        const double Travel = 24;
        var cap = new Point(centre.X + axisX * Travel, centre.Y + axisY * Travel);
        if (pressed)
            DrawGlow(context, cap, 65, press);
        context.DrawEllipse(pressed ? press : DetailBrush ?? StickFill,
            new Pen(pressed ? press : ink, 6.5), cap, CapRadius, CapRadius);
        if (pressed)
            context.DrawEllipse(null, WhiteRing, cap, CapRadius - 4, CapRadius - 4);
    }

    private static void DrawGlow(DrawingContext context, Point centre, double radius, IBrush brush)
    {
        // Uses the actual host-supplied brush, so custom highlight themes do not leave orange halos.
        for (var i = 4; i >= 1; i--)
        {
            using (context.PushOpacity(0.055))
                context.DrawEllipse(brush, null, centre, radius - (4 - i) * 3, radius - (4 - i) * 3);
        }
    }

    private static void DrawLabel(DrawingContext context, string label, IBrush brush)
    {
        var geometry = label switch
        {
            "LT" => LabelLT,
            "RT" => LabelRT,
            "LB" => LabelLB,
            "RB" => LabelRB,
            "A" => LabelA,
            "B" => LabelB,
            "X" => LabelX,
            "Y" => LabelY,
            _ => throw new ArgumentOutOfRangeException(nameof(label))
        };
        context.DrawGeometry(brush, null, geometry);
    }

    private static LinearGradientBrush Linear(string first, string middle, string last) => new()
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
        GradientStops = new GradientStops
        {
            new GradientStop(Color.Parse(first), 0),
            new GradientStop(Color.Parse(middle), 0.55),
            new GradientStop(Color.Parse(last), 1)
        }
    };

    private static RadialGradientBrush Halo(Color colour) => new()
    {
        Center = new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
        GradientOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
        GradientStops = new GradientStops
        {
            new GradientStop(Color.FromArgb(165, colour.R, colour.G, colour.B), 0),
            new GradientStop(Color.FromArgb(120, colour.R, colour.G, colour.B), 0.65),
            new GradientStop(Color.FromArgb(0, colour.R, colour.G, colour.B), 1)
        }
    };

    private sealed record Palette(IBrush Vivid, IBrush Bright, IBrush Glow)
    {
        public static Palette For(string value)
        {
            var c = Color.Parse(value);
            var bright = Color.FromRgb(
                (byte)(c.R + (255 - c.R) * 0.35),
                (byte)(c.G + (255 - c.G) * 0.35),
                (byte)(c.B + (255 - c.B) * 0.35));
            return new Palette(Linear(bright.ToString(), value, value),
                new SolidColorBrush(bright), Halo(c));
        }
    }
}
