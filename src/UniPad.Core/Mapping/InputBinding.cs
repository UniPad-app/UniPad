using System.Globalization;
using System.Text;
using UniPad.Core.Input;

namespace UniPad.Core.Mapping;

/// <summary>Kind of physical source a binding reads from.</summary>
public enum BindingSourceType
{
    /// <summary>Unbound.</summary>
    None = 0,
    /// <summary>A digital joystick button.</summary>
    Button,
    /// <summary>An analogue joystick axis, in one direction or full range.</summary>
    Axis,
    /// <summary>One direction of a hat switch.</summary>
    Hat,
    /// <summary>A keyboard key.</summary>
    Keyboard,
    /// <summary>A mouse button or wheel.</summary>
    Mouse,
}

/// <summary>Which half of an axis a binding responds to.</summary>
public enum AxisDirection
{
    /// <summary>Only values greater than the resting point.</summary>
    Positive = 0,
    /// <summary>Only values lower than the resting point.</summary>
    Negative,
    /// <summary>The whole travel of the axis, used for stick-to-stick and trigger mappings.</summary>
    Full,
}

/// <summary>
/// One physical source mapped onto one <see cref="PadTarget"/>.
/// <para>
/// Bindings serialise to a compact, human readable parameter string so that a profile file can be
/// inspected and hand-edited, e.g.
/// <c>device:030000005e0400008e02000010010000:0,axis:1,dir:-,threshold:0.5</c>.
/// </para>
/// </summary>
public sealed class InputBinding
{
    /// <summary>Source device. Null means unbound.</summary>
    public DeviceId? Device { get; set; }

    /// <summary>Source category.</summary>
    public BindingSourceType Type { get; set; } = BindingSourceType.None;

    /// <summary>Button, axis or hat index on the source device.</summary>
    public int Index { get; set; }

    /// <summary>Which half of the axis is active (axis sources only).</summary>
    public AxisDirection Direction { get; set; } = AxisDirection.Positive;

    /// <summary>SDL hat bitmask such as <c>SDL_HAT_UP</c> (hat sources only).</summary>
    public byte HatMask { get; set; }

    /// <summary>Flips the sign of an analogue reading.</summary>
    public bool Invert { get; set; }

    /// <summary>When true the source acts as a latching switch rather than a momentary contact.</summary>
    public bool Toggle { get; set; }

    /// <summary>Fraction of full travel (0..1) at which an axis counts as pressed.</summary>
    public float Threshold { get; set; } = 0.5f;

    /// <summary>Keyboard key name, used when <see cref="Type"/> is <see cref="BindingSourceType.Keyboard"/>.</summary>
    public string? KeyName { get; set; }

    /// <summary>True when this binding actually points at something.</summary>
    public bool IsBound => Type != BindingSourceType.None;

    /// <summary>Creates an unbound binding.</summary>
    public static InputBinding Empty => new();

    /// <summary>Creates a binding for a digital button.</summary>
    public static InputBinding ForButton(DeviceId device, int index) => new()
    {
        Device = device,
        Type = BindingSourceType.Button,
        Index = index,
    };

    /// <summary>Creates a binding for one direction (or the full range) of an axis.</summary>
    public static InputBinding ForAxis(DeviceId device, int index, AxisDirection direction, bool invert = false) => new()
    {
        Device = device,
        Type = BindingSourceType.Axis,
        Index = index,
        Direction = direction,
        Invert = invert,
    };

    /// <summary>Creates a binding for one direction of a hat switch.</summary>
    public static InputBinding ForHat(DeviceId device, int index, byte mask) => new()
    {
        Device = device,
        Type = BindingSourceType.Hat,
        Index = index,
        HatMask = mask,
    };

    /// <summary>Creates a keyboard binding.</summary>
    public static InputBinding ForKey(string keyName) => new()
    {
        Device = DeviceId.Keyboard,
        Type = BindingSourceType.Keyboard,
        KeyName = keyName,
    };

    /// <summary>Deep copy, used when profiles are cloned or edits are cancelled.</summary>
    public InputBinding Clone() => new()
    {
        Device = Device,
        Type = Type,
        Index = Index,
        Direction = Direction,
        HatMask = HatMask,
        Invert = Invert,
        Toggle = Toggle,
        Threshold = Threshold,
        KeyName = KeyName,
    };

    /// <summary>Short text shown on a bind button in the UI, mirroring yuzu's wording.</summary>
    public string ToDisplayString() => Type switch
    {
        BindingSourceType.Button => $"Button {Index}",
        BindingSourceType.Axis => Direction switch
        {
            AxisDirection.Positive => $"Axis {Index}+",
            AxisDirection.Negative => $"Axis {Index}-",
            _ => $"Axis {Index}{(Invert ? " (inv)" : string.Empty)}",
        },
        BindingSourceType.Hat => $"Hat {Index} {HatMaskToName(HatMask)}",
        BindingSourceType.Keyboard => $"Key {KeyName}",
        BindingSourceType.Mouse => $"Mouse {Index}",
        _ => "[not set]",
    };

    /// <summary>Serialises to the canonical parameter string.</summary>
    public string ToParamString()
    {
        if (!IsBound)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();

        switch (Type)
        {
            case BindingSourceType.Keyboard:
                builder.Append("device:keyboard");
                builder.Append(",key:").Append(KeyName ?? string.Empty);
                break;

            case BindingSourceType.Mouse:
                builder.Append("device:mouse");
                builder.Append(",button:").Append(Index.ToString(CultureInfo.InvariantCulture));
                break;

            default:
                builder.Append("device:").Append(Device?.ToString() ?? string.Empty);
                switch (Type)
                {
                    case BindingSourceType.Button:
                        builder.Append(",button:").Append(Index.ToString(CultureInfo.InvariantCulture));
                        break;

                    case BindingSourceType.Axis:
                        builder.Append(",axis:").Append(Index.ToString(CultureInfo.InvariantCulture));
                        builder.Append(",dir:").Append(Direction switch
                        {
                            AxisDirection.Positive => "+",
                            AxisDirection.Negative => "-",
                            _ => "full",
                        });
                        builder.Append(",threshold:")
                            .Append(Threshold.ToString("0.###", CultureInfo.InvariantCulture));
                        break;

                    case BindingSourceType.Hat:
                        builder.Append(",hat:").Append(Index.ToString(CultureInfo.InvariantCulture));
                        builder.Append(",mask:").Append(HatMaskToName(HatMask));
                        break;
                }

                break;
        }

        if (Invert)
        {
            builder.Append(",invert:1");
        }

        if (Toggle)
        {
            builder.Append(",toggle:1");
        }

        return builder.ToString();
    }

    /// <summary>
    /// Parses the canonical parameter string. Returns an unbound binding for unrecognised input so
    /// that a damaged profile never prevents the application from starting.
    /// </summary>
    public static InputBinding FromParamString(string? text)
    {
        var binding = new InputBinding();
        if (string.IsNullOrWhiteSpace(text))
        {
            return binding;
        }

        foreach (var part in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var colon = part.IndexOf(':');
            if (colon <= 0)
            {
                continue;
            }

            var key = part[..colon];
            var value = part[(colon + 1)..];

            switch (key)
            {
                case "device":
                    if (value == "keyboard")
                    {
                        binding.Device = DeviceId.Keyboard;
                        binding.Type = BindingSourceType.Keyboard;
                    }
                    else if (value == "mouse")
                    {
                        binding.Device = DeviceId.Mouse;
                        binding.Type = BindingSourceType.Mouse;
                    }
                    else
                    {
                        binding.Device = DeviceId.TryParse(value);
                    }

                    break;

                case "button":
                    if (int.TryParse(value, CultureInfo.InvariantCulture, out var buttonIndex))
                    {
                        binding.Index = buttonIndex;
                        if (binding.Type is not BindingSourceType.Mouse)
                        {
                            binding.Type = BindingSourceType.Button;
                        }
                    }

                    break;

                case "axis":
                    if (int.TryParse(value, CultureInfo.InvariantCulture, out var axisIndex))
                    {
                        binding.Index = axisIndex;
                        binding.Type = BindingSourceType.Axis;
                    }

                    break;

                case "hat":
                    if (int.TryParse(value, CultureInfo.InvariantCulture, out var hatIndex))
                    {
                        binding.Index = hatIndex;
                        binding.Type = BindingSourceType.Hat;
                    }

                    break;

                case "dir":
                    binding.Direction = value switch
                    {
                        "+" => AxisDirection.Positive,
                        "-" => AxisDirection.Negative,
                        _ => AxisDirection.Full,
                    };
                    break;

                case "mask":
                    binding.HatMask = HatNameToMask(value);
                    break;

                case "threshold":
                    if (float.TryParse(value, CultureInfo.InvariantCulture, out var threshold))
                    {
                        binding.Threshold = Math.Clamp(threshold, 0.05f, 0.95f);
                    }

                    break;

                case "invert":
                    binding.Invert = value is "1" or "true";
                    break;

                case "toggle":
                    binding.Toggle = value is "1" or "true";
                    break;

                case "key":
                    binding.KeyName = value;
                    binding.Type = BindingSourceType.Keyboard;
                    binding.Device = DeviceId.Keyboard;
                    break;
            }
        }

        return binding;
    }

    /// <summary>SDL hat bitmask constants, duplicated here so Mapping does not depend on SDL.</summary>
    public const byte HatCentered = 0x00;
    /// <summary>Hat up bit.</summary>
    public const byte HatUp = 0x01;
    /// <summary>Hat right bit.</summary>
    public const byte HatRight = 0x02;
    /// <summary>Hat down bit.</summary>
    public const byte HatDown = 0x04;
    /// <summary>Hat left bit.</summary>
    public const byte HatLeft = 0x08;

    private static string HatMaskToName(byte mask) => mask switch
    {
        HatUp => "up",
        HatDown => "down",
        HatLeft => "left",
        HatRight => "right",
        _ => "center",
    };

    private static byte HatNameToMask(string name) => name switch
    {
        "up" => HatUp,
        "down" => HatDown,
        "left" => HatLeft,
        "right" => HatRight,
        _ => HatCentered,
    };
}
