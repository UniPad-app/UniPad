namespace UniPad.Core.Input;

/// <summary>
/// Mutable, pre-allocated container for one device's raw state.
/// <para>
/// Instances are created once per device when it is opened and then reused forever. The polling
/// loop overwrites the arrays in place, so a full poll cycle performs zero heap allocations - a
/// hard requirement given the 1000 Hz target rate.
/// </para>
/// </summary>
public sealed class InputSnapshot
{
    /// <summary>Raw axis values in SDL range <c>[-32768, 32767]</c>.</summary>
    public short[] Axes { get; }

    /// <summary>Axis values sampled the first time the device was read, used to cancel resting drift.</summary>
    public short[] AxisRestingValues { get; }

    /// <summary>True for each currently held button.</summary>
    public bool[] Buttons { get; }

    /// <summary>SDL hat bitmasks (<c>SDL_HAT_UP</c> etc.) for each hat switch.</summary>
    public byte[] Hats { get; }

    /// <summary>Monotonic counter bumped on every successful poll; lets the UI detect staleness.</summary>
    public long Revision;

    /// <summary>True once <see cref="AxisRestingValues"/> has been captured.</summary>
    public bool HasRestingValues;

    /// <summary>Creates a snapshot sized for a specific device topology.</summary>
    public InputSnapshot(int axisCount, int buttonCount, int hatCount)
    {
        Axes = new short[Math.Max(axisCount, 0)];
        AxisRestingValues = new short[Math.Max(axisCount, 0)];
        Buttons = new bool[Math.Max(buttonCount, 0)];
        Hats = new byte[Math.Max(hatCount, 0)];
    }

    /// <summary>Clears every value back to neutral without reallocating.</summary>
    public void Reset()
    {
        Array.Clear(Axes);
        Array.Clear(Buttons);
        Array.Clear(Hats);
    }

    /// <summary>
    /// Records the current axis positions as the resting baseline. Old analogue sticks frequently
    /// rest far away from zero, and without this baseline the bind capture logic would instantly
    /// latch onto a drifting axis.
    /// </summary>
    public void CaptureRestingValues()
    {
        Array.Copy(Axes, AxisRestingValues, Axes.Length);
        HasRestingValues = true;
    }

    /// <summary>Copies the live state of this snapshot into <paramref name="destination"/>.</summary>
    public void CopyTo(InputSnapshot destination)
    {
        var axisCount = Math.Min(Axes.Length, destination.Axes.Length);
        Array.Copy(Axes, destination.Axes, axisCount);
        Array.Copy(AxisRestingValues, destination.AxisRestingValues, axisCount);

        var buttonCount = Math.Min(Buttons.Length, destination.Buttons.Length);
        Array.Copy(Buttons, destination.Buttons, buttonCount);

        var hatCount = Math.Min(Hats.Length, destination.Hats.Length);
        Array.Copy(Hats, destination.Hats, hatCount);

        destination.HasRestingValues = HasRestingValues;
        destination.Revision = Revision;
    }

    /// <summary>Safe axis read that returns 0 for out-of-range indices.</summary>
    public short GetAxis(int index) =>
        (uint)index < (uint)Axes.Length ? Axes[index] : (short)0;

    /// <summary>Safe button read that returns false for out-of-range indices.</summary>
    public bool GetButton(int index) =>
        (uint)index < (uint)Buttons.Length && Buttons[index];

    /// <summary>Safe hat read that returns SDL_HAT_CENTERED for out-of-range indices.</summary>
    public byte GetHat(int index) =>
        (uint)index < (uint)Hats.Length ? Hats[index] : (byte)0;

    /// <summary>Axis value with the resting baseline subtracted, clamped to the SDL range.</summary>
    public short GetAxisRelativeToRest(int index)
    {
        if ((uint)index >= (uint)Axes.Length)
        {
            return 0;
        }

        if (!HasRestingValues)
        {
            return Axes[index];
        }

        var delta = Axes[index] - AxisRestingValues[index];
        return (short)Math.Clamp(delta, short.MinValue, short.MaxValue);
    }
}
