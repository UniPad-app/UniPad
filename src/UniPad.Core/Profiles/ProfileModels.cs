using System.Text.Json.Serialization;

namespace UniPad.Core.Profiles;

/// <summary>Serialised form of one player's stick tuning.</summary>
public sealed class StickDto
{
    /// <summary>Radial dead zone, 0..1.</summary>
    [JsonPropertyName("deadzone")]
    public float Deadzone { get; set; } = 0.15f;

    /// <summary>Output range multiplier, 0..1.5.</summary>
    [JsonPropertyName("range")]
    public float Range { get; set; } = 0.95f;

    /// <summary>Multiplier applied while the modifier button is held.</summary>
    [JsonPropertyName("modifierScale")]
    public float ModifierScale { get; set; } = 0.5f;
}

/// <summary>Serialised form of a player's rumble settings.</summary>
public sealed class VibrationDto
{
    /// <summary>Whether rumble is forwarded.</summary>
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    /// <summary>Amplitude percentage, 0..100.</summary>
    [JsonPropertyName("strength")]
    public int Strength { get; set; } = 100;
}

/// <summary>Serialised form of one player slot.</summary>
public sealed class PlayerDto
{
    /// <summary>Zero-based player index.</summary>
    [JsonPropertyName("index")]
    public int Index { get; set; }

    /// <summary>Whether the player produces a virtual pad.</summary>
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    /// <summary>Virtual pad flavour: <c>Xbox360</c> or <c>DualShock4</c>.</summary>
    [JsonPropertyName("outputType")]
    public string OutputType { get; set; } = "Xbox360";

    /// <summary>Stable device id in <c>guid:port</c> form.</summary>
    [JsonPropertyName("device")]
    public string? Device { get; set; }

    /// <summary>Friendly device name, retained for display while the device is unplugged.</summary>
    [JsonPropertyName("deviceName")]
    public string? DeviceName { get; set; }

    
    /// <summary>
    /// Name of the per-player profile this slot was last loaded from or saved to, or null. Old
    /// files simply lack it, and older builds skip it when reading, so it is safe in both directions.
    /// </summary>
    [JsonPropertyName("profileName")]
    public string? ProfileName { get; set; }

    /// <summary>Whether D-Pad input also drives the left stick.</summary>
    [JsonPropertyName("emulateStickWithDpad")]
    public bool EmulateStickWithDpad { get; set; } = true;

    /// <summary>Rumble configuration.</summary>
    [JsonPropertyName("vibration")]
    public VibrationDto Vibration { get; set; } = new();

    /// <summary>Left stick tuning.</summary>
    [JsonPropertyName("leftStick")]
    public StickDto LeftStick { get; set; } = new();

    /// <summary>Right stick tuning.</summary>
    [JsonPropertyName("rightStick")]
    public StickDto RightStick { get; set; } = new();

    /// <summary>Binding table keyed by <c>PadTarget</c> name, values are parameter strings.</summary>
    [JsonPropertyName("bindings")]
    public Dictionary<string, string> Bindings { get; set; } = new();
}

/// <summary>A complete named profile document.</summary>
public sealed class ProfileDto
{
    /// <summary>Schema version, used to drive migrations.</summary>
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; set; } = ProfileSchema.CurrentVersion;

    /// <summary>Profile display name.</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = "Default";

    /// <summary>Player slots contained in this profile.</summary>
    [JsonPropertyName("players")]
    public List<PlayerDto> Players { get; set; } = [];
}

/// <summary>A single player's settings saved under a user-chosen name.</summary>
public sealed class PlayerProfileDto
{
    /// <summary>Schema version, used to drive migrations.</summary>
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; set; } = ProfileSchema.CurrentVersion;

    /// <summary>Profile display name.</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>Device the profile was made with, in <c>guid:port</c> form.</summary>
    [JsonPropertyName("device")]
    public string? Device { get; set; }

    /// <summary>Friendly name of that device.</summary>
    [JsonPropertyName("deviceName")]
    public string? DeviceName { get; set; }

    /// <summary>Whether D-Pad input also drives the left stick.</summary>
    [JsonPropertyName("emulateStickWithDpad")]
    public bool EmulateStickWithDpad { get; set; } = true;

    /// <summary>Rumble configuration.</summary>
    [JsonPropertyName("vibration")]
    public VibrationDto Vibration { get; set; } = new();

    /// <summary>Left stick tuning.</summary>
    [JsonPropertyName("leftStick")]
    public StickDto LeftStick { get; set; } = new();

    /// <summary>Right stick tuning.</summary>
    [JsonPropertyName("rightStick")]
    public StickDto RightStick { get; set; } = new();

    /// <summary>Binding table keyed by <c>PadTarget</c> name, values are parameter strings.</summary>
    [JsonPropertyName("bindings")]
    public Dictionary<string, string> Bindings { get; set; } = new();
}

/// <summary>Global application configuration, independent of any profile.</summary>
public sealed class AppConfigDto
{
    /// <summary>Schema version.</summary>
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; set; } = ProfileSchema.CurrentVersion;

    /// <summary>Name of the profile loaded at startup.</summary>
    [JsonPropertyName("activeProfile")]
    public string ActiveProfile { get; set; } = "Default";

    /// <summary>Input polling rate in Hz.</summary>
    [JsonPropertyName("pollRateHz")]
    public int PollRateHz { get; set; } = 1000;

    /// <summary>Whether physical controllers are cloaked with HidHide.</summary>
    [JsonPropertyName("hidePhysicalControllers")]
    public bool HidePhysicalControllers { get; set; }

    /// <summary>Whether the window starts hidden in the notification area.</summary>
    [JsonPropertyName("startMinimizedToTray")]
    public bool StartMinimizedToTray { get; set; }

    /// <summary>Whether closing the window hides it instead of exiting.</summary>
    [JsonPropertyName("minimizeToTrayOnClose")]
    public bool MinimizeToTrayOnClose { get; set; } = true;

    /// <summary>Whether UniPad is registered to launch at sign-in.</summary>
    [JsonPropertyName("runAtStartup")]
    public bool RunAtStartup { get; set; }

    /// <summary>UI theme: <c>Dark</c> or <c>Light</c>.</summary>
    [JsonPropertyName("theme")]
    public string Theme { get; set; } = "Dark";

    /// <summary>UI language code: <c>en</c> or <c>fa</c>.</summary>
    [JsonPropertyName("language")]
    public string Language { get; set; } = "en";

    /// <summary>Whether debug-level logging is enabled.</summary>
    [JsonPropertyName("verboseLogging")]
    public bool VerboseLogging { get; set; }

    /// <summary>Whether the mapping output is currently active.</summary>
    [JsonPropertyName("outputEnabled")]
    public bool OutputEnabled { get; set; } = true;

    /// <summary>Global hotkey that toggles output, in Avalonia gesture syntax.</summary>
    [JsonPropertyName("toggleHotkey")]
    public string ToggleHotkey { get; set; } = "Ctrl+Alt+U";

    /// <summary>Whether the HidHide recommendation banner has been dismissed.</summary>
    [JsonPropertyName("hidHideBannerDismissed")]
    public bool HidHideBannerDismissed { get; set; }

    /// <summary>
    /// Whether the synthetic keyboard and mouse source is created at startup. Read once during
    /// construction, so changing it needs a restart.
    /// </summary>
    [JsonPropertyName("keyboardMouseEnabled")]
    public bool KeyboardMouseEnabled { get; set; } = true;

    /// <summary>Mouse-to-stick sensitivity multiplier.</summary>
    [JsonPropertyName("mouseSensitivity")]
    public float MouseSensitivity { get; set; } = 1.0f;

    /// <summary>Seconds for the mouse-driven stick to fall back to centre.</summary>
    [JsonPropertyName("mouseReturnSpeed")]
    public float MouseReturnSpeed { get; set; } = 0.08f;

    /// <summary>Inverts vertical mouse movement.</summary>
    [JsonPropertyName("mouseInvertY")]
    public bool MouseInvertY { get; set; }
}

/// <summary>Schema version constants and migration helpers.</summary>
public static class ProfileSchema
{
    /// <summary>Current on-disk schema version.</summary>
    public const int CurrentVersion = 1;

    /// <summary>
    /// Upgrades an older document in place. Currently a no-op because version 1 is the first
    /// release, but the hook exists so future changes never break existing installs.
    /// </summary>
    public static void Migrate(ProfileDto profile)
    {
        if (profile.SchemaVersion >= CurrentVersion)
        {
            profile.SchemaVersion = CurrentVersion;
            return;
        }

        // Future migrations chain here, e.g. if (profile.SchemaVersion < 2) { ... }
        profile.SchemaVersion = CurrentVersion;
    }

    /// <summary>Upgrades an older configuration document in place.</summary>
    public static void Migrate(AppConfigDto config)
    {
        config.SchemaVersion = CurrentVersion;
    }
}
