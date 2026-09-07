using UniPad.Core.Input;

namespace UniPad.Core.Mapping;

/// <summary>
/// Translates raw device snapshots into a finished <see cref="PadState"/> for one player.
/// <para>
/// This class implements the four conversion scenarios that make legacy hardware usable:
/// button to button, button to axis, axis to button and axis to axis (plus the trigger and hat
/// variants). It runs inside the 1000 Hz polling loop, so it performs no allocation and no LINQ.
/// </para>
/// </summary>
public sealed class MappingEngine
{
    private const short AxisMax = 32767;
    private const short AxisMin = -32768;

    /// <summary>Latch state for bindings marked <see cref="InputBinding.Toggle"/>.</summary>
    private readonly Dictionary<PadTarget, bool> _toggleStates = new();

    /// <summary>Previous raw pressed state, needed to detect the rising edge of a toggle.</summary>
    private readonly Dictionary<PadTarget, bool> _togglePrevious = new();

    /// <summary>Resolves a <see cref="DeviceId"/> into the live device record.</summary>
    private readonly Func<DeviceId?, InputDevice?> _deviceResolver;

    /// <summary>Creates an engine that looks devices up through <paramref name="deviceResolver"/>.</summary>
    public MappingEngine(Func<DeviceId?, InputDevice?> deviceResolver)
    {
        _deviceResolver = deviceResolver;
    }

    /// <summary>
    /// Evaluates every binding of <paramref name="mapping"/> and writes the result into
    /// <paramref name="state"/>.
    /// </summary>
    public void Evaluate(PlayerMapping mapping, ref PadState state)
    {
        state.Clear();

        // ---- Modifiers are read first because stick scaling depends on them ----
        var leftModifierHeld = ReadDigital(mapping, PadTarget.LStickModifier);
        var rightModifierHeld = ReadDigital(mapping, PadTarget.RStickModifier);

        // ---- Plain buttons ----
        state.A = ReadDigital(mapping, PadTarget.A);
        state.B = ReadDigital(mapping, PadTarget.B);
        state.X = ReadDigital(mapping, PadTarget.X);
        state.Y = ReadDigital(mapping, PadTarget.Y);
        state.LeftBumper = ReadDigital(mapping, PadTarget.LeftBumper);
        state.RightBumper = ReadDigital(mapping, PadTarget.RightBumper);
        state.LeftThumb = ReadDigital(mapping, PadTarget.LStickPress);
        state.RightThumb = ReadDigital(mapping, PadTarget.RStickPress);
        state.Back = ReadDigital(mapping, PadTarget.Back);
        state.Start = ReadDigital(mapping, PadTarget.Start);
        state.Guide = ReadDigital(mapping, PadTarget.Guide);

        state.DPadUp = ReadDigital(mapping, PadTarget.DPadUp);
        state.DPadDown = ReadDigital(mapping, PadTarget.DPadDown);
        state.DPadLeft = ReadDigital(mapping, PadTarget.DPadLeft);
        state.DPadRight = ReadDigital(mapping, PadTarget.DPadRight);

        // ---- Triggers ----
        state.LeftTrigger = ReadTrigger(mapping, PadTarget.LeftTrigger);
        state.RightTrigger = ReadTrigger(mapping, PadTarget.RightTrigger);

        // ---- Sticks ----
        EvaluateStick(
            mapping,
            PadTarget.LStickUp, PadTarget.LStickDown, PadTarget.LStickLeft, PadTarget.LStickRight,
            mapping.LeftStick, leftModifierHeld,
            out var leftX, out var leftY);

        EvaluateStick(
            mapping,
            PadTarget.RStickUp, PadTarget.RStickDown, PadTarget.RStickLeft, PadTarget.RStickRight,
            mapping.RightStick, rightModifierHeld,
            out var rightX, out var rightY);

        // ---- D-Pad also driving the left stick (retro controller support) ----
        if (mapping.EmulateStickWithDpad && leftX == 0 && leftY == 0)
        {
            var scale = leftModifierHeld ? mapping.LeftStick.ModifierScale : 1f;
            var magnitude = (short)(AxisMax * Math.Clamp(mapping.LeftStick.Range * scale, 0f, 1f));

            var dpadX = 0;
            var dpadY = 0;
            if (state.DPadLeft)
            {
                dpadX -= magnitude;
            }

            if (state.DPadRight)
            {
                dpadX += magnitude;
            }

            if (state.DPadDown)
            {
                dpadY -= magnitude;
            }

            if (state.DPadUp)
            {
                dpadY += magnitude;
            }

            // Normalise diagonals so that up+right does not exceed full deflection.
            if (dpadX != 0 && dpadY != 0)
            {
                const float DiagonalFactor = 0.7071f;
                dpadX = (int)(dpadX * DiagonalFactor);
                dpadY = (int)(dpadY * DiagonalFactor);
            }

            leftX = ClampToAxis(dpadX);
            leftY = ClampToAxis(dpadY);
        }

        state.LeftThumbX = leftX;
        state.LeftThumbY = leftY;
        state.RightThumbX = rightX;
        state.RightThumbY = rightY;
    }

    /// <summary>
    /// Combines the four direction bindings of one stick into a pair of axis values, applying the
    /// radial dead zone, range and modifier scaling.
    /// </summary>
    private void EvaluateStick(
        PlayerMapping mapping,
        PadTarget up, PadTarget down, PadTarget left, PadTarget right,
        StickSettings settings, bool modifierHeld,
        out short outX, out short outY)
    {
        // Each direction is evaluated as a normalised 0..1 magnitude, which lets a digital button
        // (full deflection when pressed) and an analogue axis share the same code path.
        var upValue = ReadAnalogMagnitude(mapping, up);
        var downValue = ReadAnalogMagnitude(mapping, down);
        var leftValue = ReadAnalogMagnitude(mapping, left);
        var rightValue = ReadAnalogMagnitude(mapping, right);

        var x = rightValue - leftValue;
        var y = upValue - downValue;

        if (x == 0f && y == 0f)
        {
            outX = 0;
            outY = 0;
            return;
        }

        // Radial dead zone: the magnitude of the vector is tested, not each axis separately.
        // This is what makes an analogue stick feel correct near the centre.
        var magnitude = MathF.Sqrt((x * x) + (y * y));
        var deadzone = Math.Clamp(settings.Deadzone, 0f, 0.95f);

        if (magnitude <= deadzone)
        {
            outX = 0;
            outY = 0;
            return;
        }

        var range = Math.Clamp(settings.Range, 0.1f, 1.5f);
        var modifier = modifierHeld ? Math.Clamp(settings.ModifierScale, 0f, 1f) : 1f;

        var scaled = MathF.Min((magnitude - deadzone) / (1f - deadzone), 1f) * range * modifier;

        var normalisedX = x / magnitude * scaled;
        var normalisedY = y / magnitude * scaled;

        outX = ClampToAxis((int)(Math.Clamp(normalisedX, -1f, 1f) * AxisMax));
        outY = ClampToAxis((int)(Math.Clamp(normalisedY, -1f, 1f) * AxisMax));
    }

    /// <summary>
    /// Reads a binding as a normalised 0..1 magnitude.
    /// <para>
    /// Scenario "button to axis": a pressed button yields 1.0, giving full stick deflection.
    /// Scenario "axis to axis": the axis travel beyond its resting point is returned proportionally.
    /// </para>
    /// </summary>
    private float ReadAnalogMagnitude(PlayerMapping mapping, PadTarget target)
    {
        var binding = mapping.GetBinding(target);
        if (!binding.IsBound)
        {
            return 0f;
        }

        var device = _deviceResolver(binding.Device);
        if (device is not { IsConnected: true })
        {
            return 0f;
        }

        var snapshot = device.Snapshot;

        switch (binding.Type)
        {
            case BindingSourceType.Button:
                return ResolveToggle(target, binding, snapshot.GetButton(binding.Index)) ? 1f : 0f;

            case BindingSourceType.Hat:
                return (snapshot.GetHat(binding.Index) & binding.HatMask) != 0 ? 1f : 0f;

            case BindingSourceType.Axis:
            {
                var raw = snapshot.GetAxisRelativeToRest(binding.Index);
                if (binding.Invert)
                {
                    raw = (short)Math.Clamp(-raw, AxisMin, AxisMax);
                }

                return binding.Direction switch
                {
                    // Only the positive half contributes; the opposite half is another binding's job.
                    AxisDirection.Positive => raw > 0 ? raw / (float)AxisMax : 0f,
                    AxisDirection.Negative => raw < 0 ? -raw / (float)AxisMax : 0f,
                    // Full-range mapping: a single axis drives one direction pair, so only the
                    // matching half is emitted here and the opposite target reads the same axis.
                    _ => Math.Abs(raw) / (float)AxisMax,
                };
            }

            default:
                return 0f;
        }
    }

    /// <summary>
    /// Reads a binding as an on/off value.
    /// <para>
    /// Scenario "axis to button": the axis must travel past <see cref="InputBinding.Threshold"/>
    /// in the bound direction. Old sticks rest away from zero, so the resting baseline captured at
    /// device open time is subtracted first.
    /// </para>
    /// </summary>
    private bool ReadDigital(PlayerMapping mapping, PadTarget target)
    {
        var binding = mapping.GetBinding(target);
        if (!binding.IsBound)
        {
            return false;
        }

        var device = _deviceResolver(binding.Device);
        if (device is not { IsConnected: true })
        {
            return false;
        }

        var snapshot = device.Snapshot;
        bool pressed;

        switch (binding.Type)
        {
            case BindingSourceType.Button:
                pressed = snapshot.GetButton(binding.Index);
                break;

            case BindingSourceType.Hat:
                pressed = (snapshot.GetHat(binding.Index) & binding.HatMask) != 0;
                break;

            case BindingSourceType.Axis:
            {
                var raw = snapshot.GetAxisRelativeToRest(binding.Index);
                if (binding.Invert)
                {
                    raw = (short)Math.Clamp(-raw, AxisMin, AxisMax);
                }

                var threshold = Math.Clamp(binding.Threshold, 0.05f, 0.95f) * AxisMax;
                pressed = binding.Direction switch
                {
                    AxisDirection.Positive => raw >= threshold,
                    AxisDirection.Negative => raw <= -threshold,
                    _ => Math.Abs((int)raw) >= threshold,
                };

                break;
            }

            default:
                pressed = false;
                break;
        }

        return ResolveToggle(target, binding, pressed);
    }

    /// <summary>
    /// Reads a binding as an analogue trigger value.
    /// <para>
    /// Scenario "button to trigger": pressed yields 255. Scenario "axis to trigger": both the
    /// bipolar (-32768..32767, e.g. Xbox 360 shared trigger axis) and unipolar (0..32767) layouts
    /// are detected automatically from the resting position.
    /// </para>
    /// </summary>
    private byte ReadTrigger(PlayerMapping mapping, PadTarget target)
    {
        var binding = mapping.GetBinding(target);
        if (!binding.IsBound)
        {
            return 0;
        }

        var device = _deviceResolver(binding.Device);
        if (device is not { IsConnected: true })
        {
            return 0;
        }

        var snapshot = device.Snapshot;

        switch (binding.Type)
        {
            case BindingSourceType.Button:
                return ResolveToggle(target, binding, snapshot.GetButton(binding.Index)) ? byte.MaxValue : (byte)0;

            case BindingSourceType.Hat:
                return (snapshot.GetHat(binding.Index) & binding.HatMask) != 0 ? byte.MaxValue : (byte)0;

            case BindingSourceType.Axis:
            {
                var raw = snapshot.GetAxis(binding.Index);
                if (binding.Invert)
                {
                    raw = (short)Math.Clamp(-raw, AxisMin, AxisMax);
                }

                // A trigger that rests near the negative limit is bipolar; remap -32768..32767
                // onto 0..255. Otherwise treat it as unipolar 0..32767.
                var resting = snapshot.HasRestingValues
                    ? snapshot.AxisRestingValues[Math.Min(binding.Index, Math.Max(snapshot.AxisRestingValues.Length - 1, 0))]
                    : (short)0;

                float normalised;
                if (resting < -16000)
                {
                    normalised = (raw - (float)AxisMin) / (AxisMax - (float)AxisMin);
                }
                else if (binding.Direction == AxisDirection.Negative)
                {
                    normalised = raw < 0 ? -raw / (float)AxisMax : 0f;
                }
                else
                {
                    normalised = raw > 0 ? raw / (float)AxisMax : 0f;
                }

                return (byte)Math.Clamp(normalised * byte.MaxValue, 0f, byte.MaxValue);
            }

            default:
                return 0;
        }
    }

    /// <summary>
    /// Applies latching behaviour for toggle bindings: the output flips on each rising edge of the
    /// physical source instead of following it.
    /// </summary>
    private bool ResolveToggle(PadTarget target, InputBinding binding, bool rawPressed)
    {
        if (!binding.Toggle)
        {
            return rawPressed;
        }

        _togglePrevious.TryGetValue(target, out var previous);
        _toggleStates.TryGetValue(target, out var latched);

        if (rawPressed && !previous)
        {
            latched = !latched;
            _toggleStates[target] = latched;
        }

        _togglePrevious[target] = rawPressed;
        return latched;
    }

    /// <summary>Clears every latched toggle, e.g. when a profile is loaded.</summary>
    public void ResetToggles()
    {
        _toggleStates.Clear();
        _togglePrevious.Clear();
    }

    private static short ClampToAxis(int value) =>
        (short)Math.Clamp(value, AxisMin, AxisMax);
}
