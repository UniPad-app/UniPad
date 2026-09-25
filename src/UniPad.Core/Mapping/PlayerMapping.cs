using UniPad.Core.Input;

namespace UniPad.Core.Mapping;

/// <summary>Which flavour of virtual pad a player is exposed as.</summary>
public enum VirtualPadType
{
    /// <summary>Xbox 360 pad seen through XInput. Only four XInput slots exist in Windows.</summary>
    Xbox360 = 0,

    /// <summary>DualShock 4 pad seen through DirectInput/HID; used for players five to eight.</summary>
    DualShock4 = 1,
}

/// <summary>Per-stick tuning values.</summary>
public sealed class StickSettings
{
    /// <summary>Factory dead zone, as a fraction of full travel.</summary>
    public const float DefaultDeadzone = 0.15f;

    /// <summary>Factory output scaling.</summary>
    public const float DefaultRange = 0.95f;

    /// <summary>Factory modifier multiplier.</summary>
    public const float DefaultModifierScale = 0.5f;

    /// <summary>Radial dead zone as a fraction of full travel.</summary>
    public float Deadzone { get; set; } = DefaultDeadzone;

    /// <summary>Output scaling as a fraction; values below 1 reduce maximum deflection.</summary>
    public float Range { get; set; } = DefaultRange;

    /// <summary>Multiplier applied while the stick's modifier button is held.</summary>
    public float ModifierScale { get; set; } = DefaultModifierScale;

    /// <summary>
    /// Returns every value to its factory setting, in place.
    /// <para>
    /// In place rather than by replacing the instance, because the mapping engine reads these
    /// settings through the reference it was given when the player was set up.
    /// </para>
    /// </summary>
    public void Reset()
    {
        Deadzone = DefaultDeadzone;
        Range = DefaultRange;
        ModifierScale = DefaultModifierScale;
    }

    /// <summary>Deep copy.</summary>
    public StickSettings Clone() => new()
    {
        Deadzone = Deadzone,
        Range = Range,
        ModifierScale = ModifierScale,
    };
}

/// <summary>Vibration routing configuration for a player.</summary>
public sealed class VibrationSettings
{
    /// <summary>Whether rumble is forwarded by default.</summary>
    public const bool DefaultEnabled = true;

    /// <summary>Factory forwarding strength, as a percentage.</summary>
    public const int DefaultStrength = 100;

    /// <summary>Whether rumble from the game is forwarded to the physical device.</summary>
    public bool Enabled { get; set; } = DefaultEnabled;

    /// <summary>Strength percentage applied to the forwarded amplitude, 0..100.</summary>
    public int Strength { get; set; } = DefaultStrength;

    /// <summary>Returns every value to its factory setting, in place.</summary>
    public void Reset()
    {
        Enabled = DefaultEnabled;
        Strength = DefaultStrength;
    }

    /// <summary>Deep copy.</summary>
    public VibrationSettings Clone() => new() { Enabled = Enabled, Strength = Strength };
}

/// <summary>
/// Everything needed to translate one physical device into one virtual pad: which device, which
/// output type, the binding table and the analogue tuning.
/// </summary>
public sealed class PlayerMapping
{
    /// <summary>Zero-based player slot, 0..7.</summary>
    public int Index { get; set; }

    /// <summary>Whether this player produces a virtual pad at all.</summary>
    public bool Enabled { get; set; }

    /// <summary>Which virtual pad flavour to expose.</summary>
    public VirtualPadType OutputType { get; set; } = VirtualPadType.Xbox360;

    /// <summary>Stable id of the assigned physical device.</summary>
    public DeviceId? Device { get; set; }

    /// <summary>Last known friendly name of the device, kept so the UI can show it while unplugged.</summary>
    public string? DeviceName { get; set; }

    /// <summary>Name of the per-player profile this slot was last loaded from or saved to, or null.</summary>
    public string? ProfileName { get; set; }

    /// <summary>Whether D-Pad bindings also drive the left stick by default.</summary>
    public const bool DefaultEmulateStickWithDpad = true;

    /// <summary>
    /// When true, D-Pad bindings additionally drive the left stick. Essential for digital-only
    /// controllers because most modern games read only the analogue stick.
    /// </summary>
    public bool EmulateStickWithDpad { get; set; } = DefaultEmulateStickWithDpad;

    /// <summary>Left stick tuning.</summary>
    public StickSettings LeftStick { get; set; } = new();

    /// <summary>Right stick tuning.</summary>
    public StickSettings RightStick { get; set; } = new();

    /// <summary>Rumble configuration.</summary>
    public VibrationSettings Vibration { get; set; } = new();

    /// <summary>Binding table keyed by logical output.</summary>
    public Dictionary<PadTarget, InputBinding> Bindings { get; init; } = new();

    /// <summary>Returns the binding for a target, or an unbound placeholder.</summary>
    public InputBinding GetBinding(PadTarget target) =>
        Bindings.TryGetValue(target, out var binding) ? binding : InputBinding.Empty;

    /// <summary>Assigns a binding, removing the entry entirely when it is unbound.</summary>
    public void SetBinding(PadTarget target, InputBinding? binding)
    {
        if (binding is null || !binding.IsBound)
        {
            Bindings.Remove(target);
            return;
        }

        Bindings[target] = binding;
    }

    /// <summary>Removes every binding but keeps device assignment and tuning.</summary>
    public void ClearBindings() => Bindings.Clear();

    /// <summary>
    /// Returns the analogue tuning of both sticks to its factory setting, leaving the bindings,
    /// the device assignment, vibration and stick emulation alone.
    /// <para>
    /// This is the narrow reset offered per player. It used to also cover vibration and stick
    /// emulation, which meant a user restoring a dead zone they had pushed too far silently lost
    /// their rumble settings as well.
    /// </para>
    /// </summary>
    public void RestoreStickDefaults()
    {
        LeftStick.Reset();
        RightStick.Reset();
    }

    /// <summary>
    /// Returns every tunable value to its factory setting, leaving only the bindings and the
    /// device assignment. Paired with <see cref="ClearBindings"/> it takes a player back to the
    /// state it had on a first run.
    /// </summary>
    public void RestoreDefaults()
    {
        RestoreStickDefaults();
        Vibration.Reset();
        EmulateStickWithDpad = DefaultEmulateStickWithDpad;
    }

    /// <summary>True when at least one target is bound.</summary>
    public bool HasAnyBinding => Bindings.Count > 0;

    /// <summary>Deep copy, used for cancellable edit sessions.</summary>
    public PlayerMapping Clone()
    {
        var clone = new PlayerMapping
        {
            Index = Index,
            Enabled = Enabled,
            OutputType = OutputType,
            Device = Device,
            DeviceName = DeviceName,
            ProfileName = ProfileName,
            EmulateStickWithDpad = EmulateStickWithDpad,
            LeftStick = LeftStick.Clone(),
            RightStick = RightStick.Clone(),
            Vibration = Vibration.Clone(),
        };

        foreach (var (target, binding) in Bindings)
        {
            clone.Bindings[target] = binding.Clone();
        }

        return clone;
    }
}
