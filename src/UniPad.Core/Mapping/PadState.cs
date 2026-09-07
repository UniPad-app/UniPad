namespace UniPad.Core.Mapping;

/// <summary>
/// One complete emulated pad report. Deliberately a mutable struct so the mapping engine can build
/// it on the stack every cycle with no allocation, then hand it to the output layer by reference.
/// <para>
/// <see cref="IEquatable{T}"/> is implemented explicitly: the UI binds this struct and the default
/// <see cref="ValueType.Equals(object)"/> would fall back to reflection on every comparison.
/// </para>
/// </summary>
public struct PadState : IEquatable<PadState>
{
    /// <summary>A button.</summary>
    public bool A;
    /// <summary>B button.</summary>
    public bool B;
    /// <summary>X button.</summary>
    public bool X;
    /// <summary>Y button.</summary>
    public bool Y;
    /// <summary>Left shoulder.</summary>
    public bool LeftBumper;
    /// <summary>Right shoulder.</summary>
    public bool RightBumper;
    /// <summary>Left stick click.</summary>
    public bool LeftThumb;
    /// <summary>Right stick click.</summary>
    public bool RightThumb;
    /// <summary>Back / Select.</summary>
    public bool Back;
    /// <summary>Start.</summary>
    public bool Start;
    /// <summary>Guide / Home.</summary>
    public bool Guide;
    /// <summary>D-Pad up.</summary>
    public bool DPadUp;
    /// <summary>D-Pad down.</summary>
    public bool DPadDown;
    /// <summary>D-Pad left.</summary>
    public bool DPadLeft;
    /// <summary>D-Pad right.</summary>
    public bool DPadRight;

    /// <summary>Left stick X, SDL range.</summary>
    public short LeftThumbX;
    /// <summary>Left stick Y, SDL range, positive is up.</summary>
    public short LeftThumbY;
    /// <summary>Right stick X, SDL range.</summary>
    public short RightThumbX;
    /// <summary>Right stick Y, SDL range, positive is up.</summary>
    public short RightThumbY;

    /// <summary>Left trigger, 0..255.</summary>
    public byte LeftTrigger;
    /// <summary>Right trigger, 0..255.</summary>
    public byte RightTrigger;

    /// <summary>Resets every field back to neutral.</summary>
    public void Clear()
    {
        this = default;
    }

    /// <summary>True when no button is held and every analogue value rests at zero.</summary>
    public bool IsNeutral =>
        !A && !B && !X && !Y && !LeftBumper && !RightBumper && !LeftThumb && !RightThumb
        && !Back && !Start && !Guide && !DPadUp && !DPadDown && !DPadLeft && !DPadRight
        && LeftThumbX == 0 && LeftThumbY == 0 && RightThumbX == 0 && RightThumbY == 0
        && LeftTrigger == 0 && RightTrigger == 0;

    /// <inheritdoc />
    public bool Equals(PadState other) =>
        A == other.A && B == other.B && X == other.X && Y == other.Y
        && LeftBumper == other.LeftBumper && RightBumper == other.RightBumper
        && LeftThumb == other.LeftThumb && RightThumb == other.RightThumb
        && Back == other.Back && Start == other.Start && Guide == other.Guide
        && DPadUp == other.DPadUp && DPadDown == other.DPadDown
        && DPadLeft == other.DPadLeft && DPadRight == other.DPadRight
        && LeftThumbX == other.LeftThumbX && LeftThumbY == other.LeftThumbY
        && RightThumbX == other.RightThumbX && RightThumbY == other.RightThumbY
        && LeftTrigger == other.LeftTrigger && RightTrigger == other.RightTrigger;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is PadState other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(
        HashCode.Combine(A, B, X, Y, LeftBumper, RightBumper, LeftThumb, RightThumb),
        HashCode.Combine(Back, Start, Guide, DPadUp, DPadDown, DPadLeft, DPadRight),
        HashCode.Combine(LeftThumbX, LeftThumbY, RightThumbX, RightThumbY),
        HashCode.Combine(LeftTrigger, RightTrigger));

    /// <summary>Value equality operator.</summary>
    public static bool operator ==(PadState left, PadState right) => left.Equals(right);

    /// <summary>Value inequality operator.</summary>
    public static bool operator !=(PadState left, PadState right) => !left.Equals(right);
}
