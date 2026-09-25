using System.Text.Json;
using Serilog;
using UniPad.Core.Input;
using UniPad.Core.Mapping;
using UniPad.Core.SystemServices;

namespace UniPad.Core.Profiles;

/// <summary>Why a proposed profile name was rejected. Reported as a value so the UI can localise it.</summary>
public enum ProfileNameError
{
    /// <summary>The name is acceptable.</summary>
    None = 0,
    /// <summary>Empty or whitespace only.</summary>
    Empty,
    /// <summary>Contains a character that is forbidden in file names or reserved by UniPad.</summary>
    InvalidCharacters,
    /// <summary>A device name Windows reserves, such as CON or COM1.</summary>
    ReservedName,
    /// <summary>Longer than <see cref="PlayerProfileStore.MaxNameLength"/>.</summary>
    TooLong,
}

/// <summary>
/// Stores single-player profiles: one player's bindings and tuning under a name the user chose, so a
/// layout made for one game can be loaded into any player slot.
/// <para>
/// Files live in the profiles folder as <c>&lt;name&gt;.player.json</c>. The double extension keeps
/// them apart from the eight-player documents such as <c>Default.json</c> that share the folder and
/// hold the live application state.
/// </para>
/// </summary>
public static class PlayerProfileStore
{
    /// <summary>File name suffix that marks a single-player profile.</summary>
    public const string FileSuffix = ".player.json";

    /// <summary>Longest accepted profile name.</summary>
    public const int MaxNameLength = 64;

    /// <summary>
    /// Characters refused in a profile name, shown to the user verbatim. The dot is included so a
    /// name can never produce a second extension and be mistaken for another document type.
    /// </summary>
    public const string ForbiddenCharacters = "<>:;\"/\\|,.!?*";

    private static readonly char[] InvalidFileNameChars = Path.GetInvalidFileNameChars();

    private static readonly string[] ReservedNames =
    [
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    ];

    /// <summary>Checks whether <paramref name="name"/> can be used as a profile name.</summary>
    public static ProfileNameError ValidateName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return ProfileNameError.Empty;
        }

        var trimmed = name.Trim();
        if (trimmed.Length > MaxNameLength)
        {
            return ProfileNameError.TooLong;
        }

        foreach (var c in trimmed)
        {
            if (char.IsControl(c)
                || ForbiddenCharacters.Contains(c)
                || Array.IndexOf(InvalidFileNameChars, c) >= 0)
            {
                return ProfileNameError.InvalidCharacters;
            }
        }

        return ReservedNames.Contains(trimmed, StringComparer.OrdinalIgnoreCase)
            ? ProfileNameError.ReservedName
            : ProfileNameError.None;
    }

    /// <summary>Lists every stored player profile, sorted by name.</summary>
    public static List<string> List()
    {
        var names = new List<string>();

        try
        {
            foreach (var file in Directory.EnumerateFiles(PortablePaths.ProfilesDirectory, "*" + FileSuffix))
            {
                var fileName = Path.GetFileName(file);
                if (!fileName.EndsWith(FileSuffix, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var name = fileName[..^FileSuffix.Length];
                if (ValidateName(name) == ProfileNameError.None
                    && !names.Contains(name, StringComparer.OrdinalIgnoreCase))
                {
                    names.Add(name);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to enumerate player profiles");
        }

        names.Sort(StringComparer.OrdinalIgnoreCase);
        return names;
    }

    /// <summary>True when a profile with this name exists on disk.</summary>
    public static bool Exists(string name) =>
        ValidateName(name) == ProfileNameError.None && File.Exists(GetPath(name.Trim()));

    /// <summary>Writes one player's settings under <paramref name="name"/>, replacing any existing file.</summary>
    public static bool Save(string name, PlayerMapping mapping)
    {
        if (ValidateName(name) != ProfileNameError.None)
        {
            return false;
        }

        var trimmed = name.Trim();

        try
        {
            var dto = FromMapping(trimmed, mapping);
            var json = JsonSerializer.Serialize(dto, ProfileJsonContext.Default.PlayerProfileDto);
            ProfileStore.WriteAtomic(GetPath(trimmed), json);
            Log.Information("Player profile '{Name}' saved ({Count} bindings)", trimmed, dto.Bindings.Count);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to save player profile '{Name}'", trimmed);
            return false;
        }
    }

    /// <summary>Reads a player profile, or returns null when it is missing or unreadable.</summary>
    public static PlayerProfileDto? Load(string name)
    {
        if (ValidateName(name) != ProfileNameError.None)
        {
            return null;
        }

        var path = GetPath(name.Trim());
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize(json, ProfileJsonContext.Default.PlayerProfileDto);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to read player profile '{Name}'", name);
            return null;
        }
    }

    /// <summary>Deletes a player profile.</summary>
    public static bool Delete(string name)
    {
        if (ValidateName(name) != ProfileNameError.None)
        {
            return false;
        }

        try
        {
            var path = GetPath(name.Trim());
            if (!File.Exists(path))
            {
                return false;
            }

            File.Delete(path);
            Log.Information("Player profile '{Name}' deleted", name);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to delete player profile '{Name}'", name);
            return false;
        }
    }

    /// <summary>
    /// Copies a profile into <paramref name="target"/> in place. The caller must hold the slot out
    /// of the poll loop, because the binding dictionary is rewritten.
    /// <para>
    /// Bindings recorded against the profile's own device are re-pointed at the target player's
    /// device, so a layout made on one controller works on another player's controller. A keyboard
    /// layout is never moved onto a gamepad or the other way round: key codes mean nothing as
    /// joystick indices, and leaving them on their original device keeps them working.
    /// </para>
    /// </summary>
    public static void ApplyTo(PlayerProfileDto profile, PlayerMapping target)
    {
        var source = DeviceId.TryParse(profile.Device);

        // A player with no device takes the one the profile was made with.
        if (target.Device is null && source is not null)
        {
            target.Device = source;
            target.DeviceName = profile.DeviceName;
        }

        var destination = target.Device;

        target.ClearBindings();
        foreach (var (targetName, paramString) in profile.Bindings)
        {
            if (!Enum.TryParse<PadTarget>(targetName, ignoreCase: true, out var padTarget))
            {
                continue;
            }

            var binding = InputBinding.FromParamString(paramString);
            if (!binding.IsBound)
            {
                continue;
            }

            if (source is not null
                && destination is not null
                && binding.Device == source
                && source.IsSynthetic == destination.IsSynthetic)
            {
                binding.Device = destination;
            }

            target.SetBinding(padTarget, binding);
        }

        // In place: the mapping engine holds these setting objects by reference.
        target.LeftStick.Deadzone = Math.Clamp(profile.LeftStick.Deadzone, 0f, 0.95f);
        target.LeftStick.Range = Math.Clamp(profile.LeftStick.Range, 0.1f, 1.5f);
        target.LeftStick.ModifierScale = Math.Clamp(profile.LeftStick.ModifierScale, 0f, 1f);
        target.RightStick.Deadzone = Math.Clamp(profile.RightStick.Deadzone, 0f, 0.95f);
        target.RightStick.Range = Math.Clamp(profile.RightStick.Range, 0.1f, 1.5f);
        target.RightStick.ModifierScale = Math.Clamp(profile.RightStick.ModifierScale, 0f, 1f);
        target.Vibration.Enabled = profile.Vibration.Enabled;
        target.Vibration.Strength = Math.Clamp(profile.Vibration.Strength, 0, 100);
        target.EmulateStickWithDpad = profile.EmulateStickWithDpad;
    }

    private static PlayerProfileDto FromMapping(string name, PlayerMapping mapping)
    {
        var dto = new PlayerProfileDto
        {
            SchemaVersion = ProfileSchema.CurrentVersion,
            Name = name,
            Device = mapping.Device?.ToString(),
            DeviceName = mapping.DeviceName,
            EmulateStickWithDpad = mapping.EmulateStickWithDpad,
            Vibration = new VibrationDto
            {
                Enabled = mapping.Vibration.Enabled,
                Strength = mapping.Vibration.Strength,
            },
            LeftStick = new StickDto
            {
                Deadzone = mapping.LeftStick.Deadzone,
                Range = mapping.LeftStick.Range,
                ModifierScale = mapping.LeftStick.ModifierScale,
            },
            RightStick = new StickDto
            {
                Deadzone = mapping.RightStick.Deadzone,
                Range = mapping.RightStick.Range,
                ModifierScale = mapping.RightStick.ModifierScale,
            },
        };

        foreach (var (target, binding) in mapping.Bindings)
        {
            if (binding.IsBound)
            {
                dto.Bindings[target.ToString()] = binding.ToParamString();
            }
        }

        return dto;
    }

    private static string GetPath(string name) =>
        Path.Combine(PortablePaths.ProfilesDirectory, name + FileSuffix);
}
