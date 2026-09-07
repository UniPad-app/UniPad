using SDL;
using Serilog;
using UniPad.Core.Input;

namespace UniPad.Core.Mapping;

/// <summary>Outcome of an automatic mapping attempt, shown to the user as a notification.</summary>
/// <param name="Success">True when at least the face buttons could be mapped.</param>
/// <param name="UsedSdlMapping">True when SDL's community database supplied an exact mapping.</param>
/// <param name="MappedTargets">How many logical outputs received a binding.</param>
/// <param name="Message">Human readable summary.</param>
public readonly record struct AutoMapResult(
    bool Success,
    bool UsedSdlMapping,
    int MappedTargets,
    string Message);

/// <summary>
/// Produces a sensible binding table for a device without user interaction.
/// <para>
/// Two very different paths exist. When SDL recognises the device the mapping is exact. When it
/// does not - the common case for retro adapters and no-name clones - a heuristic based on axis and
/// button counts is applied, which is the whole point of this project.
/// </para>
/// </summary>
public static unsafe class AutoMapper
{
    /// <summary>Builds and applies an automatic mapping for <paramref name="device"/>.</summary>
    public static AutoMapResult Apply(PlayerMapping mapping, InputDevice device)
    {
        mapping.ClearBindings();
        mapping.Device = device.Id;
        mapping.DeviceName = device.Name;

        if (device.ReadMode == DeviceReadMode.Gamepad && device.GamepadHandle != IntPtr.Zero)
        {
            var count = ApplySdlGamepadMapping(mapping, device);
            mapping.EmulateStickWithDpad = false;

            Log.Information(
                "Auto-mapped {Name} using SDL gamepad database ({Count} targets)",
                device.Name, count);

            return new AutoMapResult(
                Success: true,
                UsedSdlMapping: true,
                MappedTargets: count,
                Message: $"Auto-mapped '{device.Name}' from the SDL controller database.");
        }

        var heuristicCount = ApplyHeuristicMapping(mapping, device);

        Log.Information(
            "Auto-mapped {Name} heuristically ({Count} targets, {Axes} axes / {Buttons} buttons / {Hats} hats)",
            device.Name, heuristicCount, device.AxisCount, device.ButtonCount, device.HatCount);

        return new AutoMapResult(
            Success: heuristicCount > 0,
            UsedSdlMapping: false,
            MappedTargets: heuristicCount,
            Message: heuristicCount > 0
                ? $"Guessed a mapping for '{device.Name}'. Please review and correct it."
                : $"Could not guess a mapping for '{device.Name}'. Please bind manually.");
    }

    /// <summary>
    /// Exact path: SDL already knows the physical layout, so each semantic gamepad element is
    /// translated into the underlying raw index and stored as a binding.
    /// </summary>
    private static int ApplySdlGamepadMapping(PlayerMapping mapping, InputDevice device)
    {
        var count = 0;

        // Buttons
        count += TryBindGamepadButton(mapping, device, SDL_GamepadButton.SDL_GAMEPAD_BUTTON_SOUTH, PadTarget.A);
        count += TryBindGamepadButton(mapping, device, SDL_GamepadButton.SDL_GAMEPAD_BUTTON_EAST, PadTarget.B);
        count += TryBindGamepadButton(mapping, device, SDL_GamepadButton.SDL_GAMEPAD_BUTTON_WEST, PadTarget.X);
        count += TryBindGamepadButton(mapping, device, SDL_GamepadButton.SDL_GAMEPAD_BUTTON_NORTH, PadTarget.Y);
        count += TryBindGamepadButton(mapping, device, SDL_GamepadButton.SDL_GAMEPAD_BUTTON_LEFT_SHOULDER, PadTarget.LeftBumper);
        count += TryBindGamepadButton(mapping, device, SDL_GamepadButton.SDL_GAMEPAD_BUTTON_RIGHT_SHOULDER, PadTarget.RightBumper);
        count += TryBindGamepadButton(mapping, device, SDL_GamepadButton.SDL_GAMEPAD_BUTTON_BACK, PadTarget.Back);
        count += TryBindGamepadButton(mapping, device, SDL_GamepadButton.SDL_GAMEPAD_BUTTON_START, PadTarget.Start);
        count += TryBindGamepadButton(mapping, device, SDL_GamepadButton.SDL_GAMEPAD_BUTTON_GUIDE, PadTarget.Guide);
        count += TryBindGamepadButton(mapping, device, SDL_GamepadButton.SDL_GAMEPAD_BUTTON_LEFT_STICK, PadTarget.LStickPress);
        count += TryBindGamepadButton(mapping, device, SDL_GamepadButton.SDL_GAMEPAD_BUTTON_RIGHT_STICK, PadTarget.RStickPress);
        count += TryBindGamepadButton(mapping, device, SDL_GamepadButton.SDL_GAMEPAD_BUTTON_DPAD_UP, PadTarget.DPadUp);
        count += TryBindGamepadButton(mapping, device, SDL_GamepadButton.SDL_GAMEPAD_BUTTON_DPAD_DOWN, PadTarget.DPadDown);
        count += TryBindGamepadButton(mapping, device, SDL_GamepadButton.SDL_GAMEPAD_BUTTON_DPAD_LEFT, PadTarget.DPadLeft);
        count += TryBindGamepadButton(mapping, device, SDL_GamepadButton.SDL_GAMEPAD_BUTTON_DPAD_RIGHT, PadTarget.DPadRight);

        // Sticks: SDL Y axes are positive-down, the emulated pad is positive-up, hence the
        // Negative direction for "up" and Positive for "down".
        count += TryBindGamepadAxis(mapping, device, SDL_GamepadAxis.SDL_GAMEPAD_AXIS_LEFTX, PadTarget.LStickRight, AxisDirection.Positive);
        count += TryBindGamepadAxis(mapping, device, SDL_GamepadAxis.SDL_GAMEPAD_AXIS_LEFTX, PadTarget.LStickLeft, AxisDirection.Negative);
        count += TryBindGamepadAxis(mapping, device, SDL_GamepadAxis.SDL_GAMEPAD_AXIS_LEFTY, PadTarget.LStickDown, AxisDirection.Positive);
        count += TryBindGamepadAxis(mapping, device, SDL_GamepadAxis.SDL_GAMEPAD_AXIS_LEFTY, PadTarget.LStickUp, AxisDirection.Negative);

        count += TryBindGamepadAxis(mapping, device, SDL_GamepadAxis.SDL_GAMEPAD_AXIS_RIGHTX, PadTarget.RStickRight, AxisDirection.Positive);
        count += TryBindGamepadAxis(mapping, device, SDL_GamepadAxis.SDL_GAMEPAD_AXIS_RIGHTX, PadTarget.RStickLeft, AxisDirection.Negative);
        count += TryBindGamepadAxis(mapping, device, SDL_GamepadAxis.SDL_GAMEPAD_AXIS_RIGHTY, PadTarget.RStickDown, AxisDirection.Positive);
        count += TryBindGamepadAxis(mapping, device, SDL_GamepadAxis.SDL_GAMEPAD_AXIS_RIGHTY, PadTarget.RStickUp, AxisDirection.Negative);

        // Triggers
        count += TryBindGamepadAxis(mapping, device, SDL_GamepadAxis.SDL_GAMEPAD_AXIS_LEFT_TRIGGER, PadTarget.LeftTrigger, AxisDirection.Positive);
        count += TryBindGamepadAxis(mapping, device, SDL_GamepadAxis.SDL_GAMEPAD_AXIS_RIGHT_TRIGGER, PadTarget.RightTrigger, AxisDirection.Positive);

        return count;
    }

    /// <summary>
    /// Resolves the raw button index behind an SDL semantic button by inspecting the device's
    /// mapping string, then stores a raw binding. Working in raw indices keeps the runtime path
    /// uniform for mapped and unmapped devices alike.
    /// </summary>
    private static int TryBindGamepadButton(
        PlayerMapping mapping, InputDevice device, SDL_GamepadButton button, PadTarget target)
    {
        var binding = ResolveGamepadBinding(device, GamepadElementKind.Button, (int)button);
        if (binding is null)
        {
            return 0;
        }

        mapping.SetBinding(target, binding);
        return 1;
    }

    private static int TryBindGamepadAxis(
        PlayerMapping mapping, InputDevice device, SDL_GamepadAxis axis, PadTarget target, AxisDirection direction)
    {
        var binding = ResolveGamepadBinding(device, GamepadElementKind.Axis, (int)axis);
        if (binding is null)
        {
            return 0;
        }

        binding.Direction = direction;
        mapping.SetBinding(target, binding);
        return 1;
    }

    private enum GamepadElementKind
    {
        Button,
        Axis,
    }

    /// <summary>
    /// Parses SDL's mapping string (<c>a:b0,leftx:a0,dpup:h0.1,...</c>) to find which raw element
    /// backs a semantic one.
    /// </summary>
    private static InputBinding? ResolveGamepadBinding(InputDevice device, GamepadElementKind kind, int element)
    {
        var mappingText = SDL3.SDL_GetGamepadMappingForID((SDL_JoystickID)device.InstanceId);
        if (string.IsNullOrEmpty(mappingText))
        {
            // Without a mapping string, fall back to assuming SDL's canonical ordering.
            return kind == GamepadElementKind.Button
                ? InputBinding.ForButton(device.Id, element)
                : InputBinding.ForAxis(device.Id, element, AxisDirection.Full);
        }

        var wantedName = kind == GamepadElementKind.Button
            ? GamepadButtonName((SDL_GamepadButton)element)
            : GamepadAxisName((SDL_GamepadAxis)element);

        if (wantedName is null)
        {
            return null;
        }

        // Fields are semicolon-free, comma separated: "name:target".
        foreach (var field in mappingText.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var colon = field.IndexOf(':');
            if (colon <= 0)
            {
                continue;
            }

            var key = field[..colon].Trim();
            if (!string.Equals(key, wantedName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var value = field[(colon + 1)..].Trim();
            return ParseSdlTarget(device, value);
        }

        return null;
    }

    /// <summary>
    /// Parses one right-hand side of an SDL mapping field: <c>b3</c>, <c>a1</c>, <c>a1~</c>,
    /// <c>+a2</c>, <c>-a2</c> or <c>h0.4</c>.
    /// </summary>
    private static InputBinding? ParseSdlTarget(InputDevice device, string value)
    {
        if (value.Length < 2)
        {
            return null;
        }

        var invert = value.EndsWith('~');
        if (invert)
        {
            value = value[..^1];
        }

        var direction = AxisDirection.Full;
        if (value[0] == '+')
        {
            direction = AxisDirection.Positive;
            value = value[1..];
        }
        else if (value[0] == '-')
        {
            direction = AxisDirection.Negative;
            value = value[1..];
        }

        if (value.Length < 2)
        {
            return null;
        }

        var kind = value[0];
        var rest = value[1..];

        switch (kind)
        {
            case 'b' when int.TryParse(rest, out var buttonIndex):
                return InputBinding.ForButton(device.Id, buttonIndex);

            case 'a' when int.TryParse(rest, out var axisIndex):
            {
                var binding = InputBinding.ForAxis(device.Id, axisIndex, direction, invert);
                return binding;
            }

            case 'h':
            {
                var dot = rest.IndexOf('.');
                if (dot <= 0
                    || !int.TryParse(rest[..dot], out var hatIndex)
                    || !int.TryParse(rest[(dot + 1)..], out var maskValue))
                {
                    return null;
                }

                return InputBinding.ForHat(device.Id, hatIndex, (byte)maskValue);
            }

            default:
                return null;
        }
    }

    private static string? GamepadButtonName(SDL_GamepadButton button) => button switch
    {
        SDL_GamepadButton.SDL_GAMEPAD_BUTTON_SOUTH => "a",
        SDL_GamepadButton.SDL_GAMEPAD_BUTTON_EAST => "b",
        SDL_GamepadButton.SDL_GAMEPAD_BUTTON_WEST => "x",
        SDL_GamepadButton.SDL_GAMEPAD_BUTTON_NORTH => "y",
        SDL_GamepadButton.SDL_GAMEPAD_BUTTON_BACK => "back",
        SDL_GamepadButton.SDL_GAMEPAD_BUTTON_GUIDE => "guide",
        SDL_GamepadButton.SDL_GAMEPAD_BUTTON_START => "start",
        SDL_GamepadButton.SDL_GAMEPAD_BUTTON_LEFT_STICK => "leftstick",
        SDL_GamepadButton.SDL_GAMEPAD_BUTTON_RIGHT_STICK => "rightstick",
        SDL_GamepadButton.SDL_GAMEPAD_BUTTON_LEFT_SHOULDER => "leftshoulder",
        SDL_GamepadButton.SDL_GAMEPAD_BUTTON_RIGHT_SHOULDER => "rightshoulder",
        SDL_GamepadButton.SDL_GAMEPAD_BUTTON_DPAD_UP => "dpup",
        SDL_GamepadButton.SDL_GAMEPAD_BUTTON_DPAD_DOWN => "dpdown",
        SDL_GamepadButton.SDL_GAMEPAD_BUTTON_DPAD_LEFT => "dpleft",
        SDL_GamepadButton.SDL_GAMEPAD_BUTTON_DPAD_RIGHT => "dpright",
        _ => null,
    };

    private static string? GamepadAxisName(SDL_GamepadAxis axis) => axis switch
    {
        SDL_GamepadAxis.SDL_GAMEPAD_AXIS_LEFTX => "leftx",
        SDL_GamepadAxis.SDL_GAMEPAD_AXIS_LEFTY => "lefty",
        SDL_GamepadAxis.SDL_GAMEPAD_AXIS_RIGHTX => "rightx",
        SDL_GamepadAxis.SDL_GAMEPAD_AXIS_RIGHTY => "righty",
        SDL_GamepadAxis.SDL_GAMEPAD_AXIS_LEFT_TRIGGER => "lefttrigger",
        SDL_GamepadAxis.SDL_GAMEPAD_AXIS_RIGHT_TRIGGER => "righttrigger",
        _ => null,
    };

    /// <summary>
    /// Heuristic path for devices SDL does not recognise.
    /// <para>
    /// Layout guesses follow the de-facto conventions used by USB adapter firmware: axes 0/1 are
    /// the left stick, 2/3 (or 3/4) the right one, hat 0 is the D-Pad, and buttons are numbered
    /// starting from the bottom face button.
    /// </para>
    /// </summary>
    private static int ApplyHeuristicMapping(PlayerMapping mapping, InputDevice device)
    {
        var count = 0;
        var id = device.Id;

        // ---- Sticks from axis count ----
        if (device.AxisCount >= 2)
        {
            mapping.SetBinding(PadTarget.LStickRight, InputBinding.ForAxis(id, 0, AxisDirection.Positive));
            mapping.SetBinding(PadTarget.LStickLeft, InputBinding.ForAxis(id, 0, AxisDirection.Negative));
            mapping.SetBinding(PadTarget.LStickDown, InputBinding.ForAxis(id, 1, AxisDirection.Positive));
            mapping.SetBinding(PadTarget.LStickUp, InputBinding.ForAxis(id, 1, AxisDirection.Negative));
            count += 4;
        }

        if (device.AxisCount >= 4)
        {
            // Five-axis adapters commonly place the right stick on 3/4 with 2 used as a throttle.
            var rightX = device.AxisCount >= 5 ? 3 : 2;
            var rightY = device.AxisCount >= 5 ? 4 : 3;

            mapping.SetBinding(PadTarget.RStickRight, InputBinding.ForAxis(id, rightX, AxisDirection.Positive));
            mapping.SetBinding(PadTarget.RStickLeft, InputBinding.ForAxis(id, rightX, AxisDirection.Negative));
            mapping.SetBinding(PadTarget.RStickDown, InputBinding.ForAxis(id, rightY, AxisDirection.Positive));
            mapping.SetBinding(PadTarget.RStickUp, InputBinding.ForAxis(id, rightY, AxisDirection.Negative));
            count += 4;
        }

        // ---- D-Pad from hat, or from the high button range ----
        if (device.HatCount >= 1)
        {
            mapping.SetBinding(PadTarget.DPadUp, InputBinding.ForHat(id, 0, InputBinding.HatUp));
            mapping.SetBinding(PadTarget.DPadDown, InputBinding.ForHat(id, 0, InputBinding.HatDown));
            mapping.SetBinding(PadTarget.DPadLeft, InputBinding.ForHat(id, 0, InputBinding.HatLeft));
            mapping.SetBinding(PadTarget.DPadRight, InputBinding.ForHat(id, 0, InputBinding.HatRight));
            count += 4;
        }
        else if (device.ButtonCount >= 16)
        {
            // Widely used convention: buttons 12..15 are up/down/left/right.
            mapping.SetBinding(PadTarget.DPadUp, InputBinding.ForButton(id, 12));
            mapping.SetBinding(PadTarget.DPadDown, InputBinding.ForButton(id, 13));
            mapping.SetBinding(PadTarget.DPadLeft, InputBinding.ForButton(id, 14));
            mapping.SetBinding(PadTarget.DPadRight, InputBinding.ForButton(id, 15));
            count += 4;
        }

        // ---- Buttons in ascending order ----
        var buttonOrder = new[]
        {
            PadTarget.A, PadTarget.B, PadTarget.X, PadTarget.Y,
            PadTarget.LeftBumper, PadTarget.RightBumper,
            PadTarget.Back, PadTarget.Start,
            PadTarget.LStickPress, PadTarget.RStickPress,
            PadTarget.Guide,
        };

        for (var i = 0; i < buttonOrder.Length && i < device.ButtonCount; i++)
        {
            mapping.SetBinding(buttonOrder[i], InputBinding.ForButton(id, i));
            count++;
        }

        // ---- Triggers ----
        if (device.AxisCount >= 6)
        {
            mapping.SetBinding(PadTarget.LeftTrigger, InputBinding.ForAxis(id, 2, AxisDirection.Positive));
            mapping.SetBinding(PadTarget.RightTrigger, InputBinding.ForAxis(id, 5, AxisDirection.Positive));
            count += 2;
        }
        else if (device.ButtonCount >= 8)
        {
            // Digital shoulder buttons doubling as triggers - typical for PS1/PS2 adapters
            // where L2/R2 are plain switches.
            mapping.SetBinding(PadTarget.LeftTrigger, InputBinding.ForButton(id, 6));
            mapping.SetBinding(PadTarget.RightTrigger, InputBinding.ForButton(id, 7));

            // Those two indices were already claimed by Back/Start above, so move those along.
            if (device.ButtonCount >= 10)
            {
                mapping.SetBinding(PadTarget.Back, InputBinding.ForButton(id, 8));
                mapping.SetBinding(PadTarget.Start, InputBinding.ForButton(id, 9));
            }

            count += 2;
        }

        // ---- Digital-only controller: let the D-Pad drive the left stick as well ----
        // Most modern games read only the analogue stick, so without this a NES-style pad would
        // move nothing at all.
        mapping.EmulateStickWithDpad = device.AxisCount < 2;

        return count;
    }
}
