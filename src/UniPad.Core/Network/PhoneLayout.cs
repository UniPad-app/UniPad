using UniPad.Core.Input;
using UniPad.Core.Mapping;

namespace UniPad.Core.Network;

/// <summary>
/// Fixed button/axis layout of the phone page. These numbers MUST match the BTN table in
/// controller.html.
/// </summary>
public static class PhoneLayout
{
    public const int ButtonCount = 15;
    public const int AxisCount = 6;

    public const int A = 0, B = 1, X = 2, Y = 3, LB = 4, RB = 5, Back = 6, Start = 7, Guide = 8;
    public const int L3 = 9, R3 = 10, Up = 11, Down = 12, Left = 13, Right = 14;

    public const int LX = 0, LY = 1, RX = 2, RY = 3, LT = 4, RT = 5;

    /// <summary>Writes the standard phone bindings into a player slot.</summary>
    public static void ApplyTo(PlayerMapping mapping, DeviceId id)
    {
        mapping.ClearBindings();

        void Button(PadTarget target, int index) =>
            mapping.SetBinding(target, InputBinding.ForButton(id, index));

        void Axis(PadTarget target, int index, AxisDirection direction) =>
            mapping.SetBinding(target, InputBinding.ForAxis(id, index, direction));

        Button(PadTarget.A, A);
        Button(PadTarget.B, B);
        Button(PadTarget.X, X);
        Button(PadTarget.Y, Y);
        Button(PadTarget.LeftBumper, LB);
        Button(PadTarget.RightBumper, RB);
        Button(PadTarget.Back, Back);
        Button(PadTarget.Start, Start);
        Button(PadTarget.Guide, Guide);
        Button(PadTarget.LStickPress, L3);
        Button(PadTarget.RStickPress, R3);
        Button(PadTarget.DPadUp, Up);
        Button(PadTarget.DPadDown, Down);
        Button(PadTarget.DPadLeft, Left);
        Button(PadTarget.DPadRight, Right);

        Axis(PadTarget.LeftTrigger, LT, AxisDirection.Positive);
        Axis(PadTarget.RightTrigger, RT, AxisDirection.Positive);

        // Screen Y grows downward, the same convention SDL sticks use.
        Axis(PadTarget.LStickLeft, LX, AxisDirection.Negative);
        Axis(PadTarget.LStickRight, LX, AxisDirection.Positive);
        Axis(PadTarget.LStickUp, LY, AxisDirection.Negative);
        Axis(PadTarget.LStickDown, LY, AxisDirection.Positive);
        Axis(PadTarget.RStickLeft, RX, AxisDirection.Negative);
        Axis(PadTarget.RStickRight, RX, AxisDirection.Positive);
        Axis(PadTarget.RStickUp, RY, AxisDirection.Negative);
        Axis(PadTarget.RStickDown, RY, AxisDirection.Positive);

        // A touch stick has no drift and a real stick exists, so no D-Pad emulation.
        mapping.EmulateStickWithDpad = false;
        mapping.LeftStick.Deadzone = 0.05f;
        mapping.LeftStick.Range = 1f;
        mapping.RightStick.Deadzone = 0.05f;
        mapping.RightStick.Range = 1f;
    }
}
