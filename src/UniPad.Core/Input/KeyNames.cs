namespace UniPad.Core.Input;

/// <summary>
/// Virtual-key constants and display names for the synthetic keyboard/mouse device.
/// <para>
/// Bindings store the raw VK code in <see cref="Mapping.InputBinding.Index"/>, so this table is
/// only ever used for captions. Unknown codes fall back to a numeric form rather than throwing,
/// because a profile written on another keyboard layout may contain codes this build has no name
/// for.
/// </para>
/// </summary>
public static class KeyNames
{
    /// <summary>Left mouse button.</summary>
    public const int MouseLeft = 0x01;

    /// <summary>Right mouse button.</summary>
    public const int MouseRight = 0x02;

    /// <summary>Middle mouse button.</summary>
    public const int MouseMiddle = 0x04;

    /// <summary>Fourth mouse button (back).</summary>
    public const int MouseX1 = 0x05;

    /// <summary>Fifth mouse button (forward).</summary>
    public const int MouseX2 = 0x06;

    /// <summary>Pseudo-button index set briefly when the wheel turns up.</summary>
    public const int WheelUp = 256;

    /// <summary>Pseudo-button index set briefly when the wheel turns down.</summary>
    public const int WheelDown = 257;

    /// <summary>Total size of the synthetic button array.</summary>
    public const int ButtonCount = 258;

    /// <summary>Axis index carrying horizontal mouse movement.</summary>
    public const int MouseAxisX = 0;

    /// <summary>Axis index carrying vertical mouse movement.</summary>
    public const int MouseAxisY = 1;

    /// <summary>True when the index refers to a mouse button or the wheel.</summary>
    public static bool IsMouse(int index) =>
        index is MouseLeft or MouseRight or MouseMiddle or MouseX1 or MouseX2 or WheelUp or WheelDown;

    /// <summary>Human readable caption for a bind button.</summary>
    public static string ToDisplay(int index)
    {
        if (Named.TryGetValue(index, out var name))
        {
            return name;
        }

        // Letters, digits, function and numpad keys are contiguous in the VK table, so they need
        // no dictionary entries of their own.
        if (index is >= 0x41 and <= 0x5A || index is >= 0x30 and <= 0x39)
        {
            return ((char)index).ToString();
        }

        if (index is >= 0x70 and <= 0x87)
        {
            return $"F{index - 0x6F}";
        }

        if (index is >= 0x60 and <= 0x69)
        {
            return $"Num {index - 0x60}";
        }

        return $"Key {index}";
    }

    private static readonly Dictionary<int, string> Named = new()
    {
        [MouseLeft] = "Mouse L",
        [MouseRight] = "Mouse R",
        [MouseMiddle] = "Mouse M",
        [MouseX1] = "Mouse 4",
        [MouseX2] = "Mouse 5",
        [WheelUp] = "Wheel Up",
        [WheelDown] = "Wheel Down",

        [0x08] = "Backspace",
        [0x09] = "Tab",
        [0x0D] = "Enter",
        [0x13] = "Pause",
        [0x14] = "Caps Lock",
        [0x1B] = "Esc",
        [0x20] = "Space",
        [0x21] = "Page Up",
        [0x22] = "Page Down",
        [0x23] = "End",
        [0x24] = "Home",
        [0x25] = "Left",
        [0x26] = "Up",
        [0x27] = "Right",
        [0x28] = "Down",
        [0x2C] = "Print Screen",
        [0x2D] = "Insert",
        [0x2E] = "Delete",

        [0x6A] = "Num *",
        [0x6B] = "Num +",
        [0x6D] = "Num -",
        [0x6E] = "Num .",
        [0x6F] = "Num /",

        [0x90] = "Num Lock",
        [0x91] = "Scroll Lock",

        [0x5B] = "L Win",
        [0x5C] = "R Win",
        [0x5D] = "Menu",
        [0xA0] = "L Shift",
        [0xA1] = "R Shift",
        [0xA2] = "L Ctrl",
        [0xA3] = "R Ctrl",
        [0xA4] = "L Alt",
        [0xA5] = "R Alt",

        [0xBA] = ";",
        [0xBB] = "=",
        [0xBC] = ",",
        [0xBD] = "-",
        [0xBE] = ".",
        [0xBF] = "/",
        [0xC0] = "`",
        [0xDB] = "[",
        [0xDC] = "\\",
        [0xDD] = "]",
        [0xDE] = "'",
    };
}
