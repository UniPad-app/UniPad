namespace UniPad.Core.Mapping;

/// <summary>
/// Every logical output that a binding can drive on the emulated pad.
/// <para>
/// Analogue stick directions are individually bindable so that a purely digital controller
/// (NES/SNES pad, PS1 adapter) can still produce full stick deflection - this is the single most
/// important requirement for retro hardware support.
/// </para>
/// </summary>
public enum PadTarget
{
    /// <summary>No target (used as a default / unset value).</summary>
    None = 0,

    // ---- Face buttons ----
    /// <summary>Bottom face button (Xbox A).</summary>
    A,
    /// <summary>Right face button (Xbox B).</summary>
    B,
    /// <summary>Left face button (Xbox X).</summary>
    X,
    /// <summary>Top face button (Xbox Y).</summary>
    Y,

    // ---- Shoulders ----
    /// <summary>Left shoulder / LB.</summary>
    LeftBumper,
    /// <summary>Right shoulder / RB.</summary>
    RightBumper,

    // ---- Triggers (analogue 0..255) ----
    /// <summary>Left analogue trigger.</summary>
    LeftTrigger,
    /// <summary>Right analogue trigger.</summary>
    RightTrigger,

    // ---- D-Pad ----
    /// <summary>D-Pad up.</summary>
    DPadUp,
    /// <summary>D-Pad down.</summary>
    DPadDown,
    /// <summary>D-Pad left.</summary>
    DPadLeft,
    /// <summary>D-Pad right.</summary>
    DPadRight,

    // ---- Left stick ----
    /// <summary>Left stick upward deflection.</summary>
    LStickUp,
    /// <summary>Left stick downward deflection.</summary>
    LStickDown,
    /// <summary>Left stick leftward deflection.</summary>
    LStickLeft,
    /// <summary>Left stick rightward deflection.</summary>
    LStickRight,
    /// <summary>Left stick click (L3).</summary>
    LStickPress,
    /// <summary>Hold-to-slow modifier for the left stick.</summary>
    LStickModifier,

    // ---- Right stick ----
    /// <summary>Right stick upward deflection.</summary>
    RStickUp,
    /// <summary>Right stick downward deflection.</summary>
    RStickDown,
    /// <summary>Right stick leftward deflection.</summary>
    RStickLeft,
    /// <summary>Right stick rightward deflection.</summary>
    RStickRight,
    /// <summary>Right stick click (R3).</summary>
    RStickPress,
    /// <summary>Hold-to-slow modifier for the right stick.</summary>
    RStickModifier,

    // ---- Misc ----
    /// <summary>Back / Select / Minus.</summary>
    Back,
    /// <summary>Start / Plus.</summary>
    Start,
    /// <summary>Guide / Home / PS button.</summary>
    Guide,
}

/// <summary>Helpers for classifying and labelling <see cref="PadTarget"/> values.</summary>
public static class PadTargetInfo
{
    /// <summary>All bindable targets in the order the UI presents them.</summary>
    public static readonly PadTarget[] All =
    [
        PadTarget.A, PadTarget.B, PadTarget.X, PadTarget.Y,
        PadTarget.LeftBumper, PadTarget.RightBumper,
        PadTarget.LeftTrigger, PadTarget.RightTrigger,
        PadTarget.DPadUp, PadTarget.DPadDown, PadTarget.DPadLeft, PadTarget.DPadRight,
        PadTarget.LStickUp, PadTarget.LStickDown, PadTarget.LStickLeft, PadTarget.LStickRight,
        PadTarget.LStickPress, PadTarget.LStickModifier,
        PadTarget.RStickUp, PadTarget.RStickDown, PadTarget.RStickLeft, PadTarget.RStickRight,
        PadTarget.RStickPress, PadTarget.RStickModifier,
        PadTarget.Back, PadTarget.Start, PadTarget.Guide,
    ];

    /// <summary>True when the target contributes to an analogue stick axis.</summary>
    public static bool IsStickDirection(this PadTarget target) => target is
        PadTarget.LStickUp or PadTarget.LStickDown or PadTarget.LStickLeft or PadTarget.LStickRight or
        PadTarget.RStickUp or PadTarget.RStickDown or PadTarget.RStickLeft or PadTarget.RStickRight;

    /// <summary>True when the target is an analogue trigger.</summary>
    public static bool IsTrigger(this PadTarget target) =>
        target is PadTarget.LeftTrigger or PadTarget.RightTrigger;

    /// <summary>True when the target is a plain on/off button.</summary>
    public static bool IsButton(this PadTarget target) =>
        !target.IsStickDirection() && !target.IsTrigger() && target != PadTarget.None;

    /// <summary>Short label used on bind buttons and in the controller preview.</summary>
    public static string ToLabel(this PadTarget target) => target switch
    {
        PadTarget.A => "A",
        PadTarget.B => "B",
        PadTarget.X => "X",
        PadTarget.Y => "Y",
        PadTarget.LeftBumper => "LB",
        PadTarget.RightBumper => "RB",
        PadTarget.LeftTrigger => "LT",
        PadTarget.RightTrigger => "RT",
        PadTarget.DPadUp => "Up",
        PadTarget.DPadDown => "Down",
        PadTarget.DPadLeft => "Left",
        PadTarget.DPadRight => "Right",
        PadTarget.LStickUp => "Up",
        PadTarget.LStickDown => "Down",
        PadTarget.LStickLeft => "Left",
        PadTarget.LStickRight => "Right",
        PadTarget.LStickPress => "Pressed",
        PadTarget.LStickModifier => "Modifier",
        PadTarget.RStickUp => "Up",
        PadTarget.RStickDown => "Down",
        PadTarget.RStickLeft => "Left",
        PadTarget.RStickRight => "Right",
        PadTarget.RStickPress => "Pressed",
        PadTarget.RStickModifier => "Modifier",
        PadTarget.Back => "Back",
        PadTarget.Start => "Start",
        PadTarget.Guide => "Guide",
        _ => "None",
    };
}
