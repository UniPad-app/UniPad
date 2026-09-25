using System.Text.Json;
using Serilog;
using UniPad.Core.Input;
using UniPad.Core.Mapping;
using UniPad.Core.Output;
using UniPad.Core.SystemServices;

namespace UniPad.Core.Profiles;

/// <summary>
/// Loads and saves profiles and global configuration, and converts between the on-disk DTOs and
/// the runtime model.
/// <para>
/// Writes go through a temp file plus atomic replace, so an interrupted save can never leave a
/// truncated profile behind.
/// </para>
/// </summary>
public sealed class ProfileStore
{
    /// <summary>Name of the profile created on first run.</summary>
    public const string DefaultProfileName = "Default";

    /// <summary>Reads the global configuration, returning defaults when it is missing or corrupt.</summary>
    public AppConfigDto LoadConfig()
    {
        var path = PortablePaths.ConfigFile;

        if (!File.Exists(path))
        {
            Log.Information("No config file found; creating defaults at {Path}", path);
            var fresh = new AppConfigDto();
            SaveConfig(fresh);
            return fresh;
        }

        try
        {
            var json = File.ReadAllText(path);
            var config = JsonSerializer.Deserialize(json, ProfileJsonContext.Default.AppConfigDto);
            if (config is null)
            {
                throw new JsonException("Configuration deserialised to null.");
            }

            ProfileSchema.Migrate(config);
            return config;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to read config; falling back to defaults");
            BackupCorruptFile(path);
            return new AppConfigDto();
        }
    }

    /// <summary>Writes the global configuration atomically.</summary>
    public void SaveConfig(AppConfigDto config)
    {
        try
        {
            config.SchemaVersion = ProfileSchema.CurrentVersion;
            var json = JsonSerializer.Serialize(config, ProfileJsonContext.Default.AppConfigDto);
            WriteAtomic(PortablePaths.ConfigFile, json);
            Log.Debug("Configuration saved");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to save configuration");
        }
    }

    /// <summary>Lists the names of every stored profile, always including the default.</summary>
    public List<string> ListProfiles()
    {
        var names = new List<string>();

        try
        {
            foreach (var file in Directory.EnumerateFiles(PortablePaths.ProfilesDirectory, "*.json"))
            {
                // Single-player profiles share this folder but are a different document type.
                if (file.EndsWith(PlayerProfileStore.FileSuffix, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var name = Path.GetFileNameWithoutExtension(file);
                if (!string.IsNullOrWhiteSpace(name))
                {
                    names.Add(name);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to enumerate profiles");
        }

        if (!names.Contains(DefaultProfileName, StringComparer.OrdinalIgnoreCase))
        {
            names.Insert(0, DefaultProfileName);
        }

        names.Sort(StringComparer.OrdinalIgnoreCase);
        return names;
    }

    /// <summary>
    /// Loads a profile by name and converts it into runtime mappings. A missing profile yields a
    /// blank set of eight players rather than an error.
    /// </summary>
    public List<PlayerMapping> LoadProfile(string name)
    {
        var path = GetProfilePath(name);

        if (!File.Exists(path))
        {
            Log.Information("Profile '{Name}' not found; starting from blank slots", name);
            return CreateBlankPlayers();
        }

        try
        {
            var json = File.ReadAllText(path);
            var dto = JsonSerializer.Deserialize(json, ProfileJsonContext.Default.ProfileDto);
            if (dto is null)
            {
                throw new JsonException("Profile deserialised to null.");
            }

            ProfileSchema.Migrate(dto);
            return FromDto(dto);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to read profile '{Name}'", name);
            BackupCorruptFile(path);
            return CreateBlankPlayers();
        }
    }

    /// <summary>Saves runtime mappings under the given profile name.</summary>
    public void SaveProfile(string name, IReadOnlyList<PlayerMapping> players)
    {
        try
        {
            var dto = ToDto(name, players);
            var json = JsonSerializer.Serialize(dto, ProfileJsonContext.Default.ProfileDto);
            WriteAtomic(GetProfilePath(name), json);
            Log.Information("Profile '{Name}' saved ({Count} players)", name, players.Count);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to save profile '{Name}'", name);
        }
    }

    /// <summary>Deletes a profile. The default profile cannot be removed.</summary>
    public bool DeleteProfile(string name)
    {
        if (string.Equals(name, DefaultProfileName, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            var path = GetProfilePath(name);
            if (File.Exists(path))
            {
                File.Delete(path);
                Log.Information("Profile '{Name}' deleted", name);
                return true;
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to delete profile '{Name}'", name);
        }

        return false;
    }

    /// <summary>Renames a profile by copying and deleting.</summary>
    public bool RenameProfile(string oldName, string newName)
    {
        if (string.IsNullOrWhiteSpace(newName)
            || string.Equals(oldName, DefaultProfileName, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            var source = GetProfilePath(oldName);
            var destination = GetProfilePath(newName);

            if (!File.Exists(source) || File.Exists(destination))
            {
                return false;
            }

            File.Move(source, destination);
            Log.Information("Profile '{Old}' renamed to '{New}'", oldName, newName);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to rename profile '{Old}'", oldName);
            return false;
        }
    }

    /// <summary>Creates eight disabled player slots with default tuning.</summary>
    public static List<PlayerMapping> CreateBlankPlayers()
    {
        var players = new List<PlayerMapping>(OutputManager.MaxPlayers);
        for (var i = 0; i < OutputManager.MaxPlayers; i++)
        {
            players.Add(new PlayerMapping
            {
                Index = i,
                Enabled = false,
                // Only four XInput slots exist, so players five to eight default to DS4.
                OutputType = i < OutputManager.XInputSlotCount
                    ? VirtualPadType.Xbox360
                    : VirtualPadType.DualShock4,
            });
        }

        return players;
    }

    private static List<PlayerMapping> FromDto(ProfileDto dto)
    {
        var players = CreateBlankPlayers();

        foreach (var playerDto in dto.Players)
        {
            if ((uint)playerDto.Index >= OutputManager.MaxPlayers)
            {
                continue;
            }

            var mapping = players[playerDto.Index];
            mapping.Enabled = playerDto.Enabled;
            mapping.OutputType = Enum.TryParse<VirtualPadType>(playerDto.OutputType, ignoreCase: true, out var type)
                ? type
                : VirtualPadType.Xbox360;
            mapping.Device = DeviceId.TryParse(playerDto.Device);
            mapping.DeviceName = playerDto.DeviceName;
            mapping.ProfileName = playerDto.ProfileName;
            mapping.EmulateStickWithDpad = playerDto.EmulateStickWithDpad;

            mapping.LeftStick = new StickSettings
            {
                Deadzone = Math.Clamp(playerDto.LeftStick.Deadzone, 0f, 0.95f),
                Range = Math.Clamp(playerDto.LeftStick.Range, 0.1f, 1.5f),
                ModifierScale = Math.Clamp(playerDto.LeftStick.ModifierScale, 0f, 1f),
            };

            mapping.RightStick = new StickSettings
            {
                Deadzone = Math.Clamp(playerDto.RightStick.Deadzone, 0f, 0.95f),
                Range = Math.Clamp(playerDto.RightStick.Range, 0.1f, 1.5f),
                ModifierScale = Math.Clamp(playerDto.RightStick.ModifierScale, 0f, 1f),
            };

            mapping.Vibration = new VibrationSettings
            {
                Enabled = playerDto.Vibration.Enabled,
                Strength = Math.Clamp(playerDto.Vibration.Strength, 0, 100),
            };

            mapping.ClearBindings();
            foreach (var (targetName, paramString) in playerDto.Bindings)
            {
                if (!Enum.TryParse<PadTarget>(targetName, ignoreCase: true, out var target))
                {
                    Log.Debug("Unknown binding target '{Target}' ignored", targetName);
                    continue;
                }

                var binding = InputBinding.FromParamString(paramString);
                if (binding.IsBound)
                {
                    mapping.SetBinding(target, binding);
                }
            }
        }

        return players;
    }

    private static ProfileDto ToDto(string name, IReadOnlyList<PlayerMapping> players)
    {
        var dto = new ProfileDto
        {
            SchemaVersion = ProfileSchema.CurrentVersion,
            Name = name,
        };

        foreach (var mapping in players)
        {
            var playerDto = new PlayerDto
            {
                Index = mapping.Index,
                Enabled = mapping.Enabled,
                OutputType = mapping.OutputType.ToString(),
                Device = mapping.Device?.ToString(),
                DeviceName = mapping.DeviceName,
                ProfileName = mapping.ProfileName,
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
                    playerDto.Bindings[target.ToString()] = binding.ToParamString();
                }
            }

            dto.Players.Add(playerDto);
        }

        return dto;
    }

    private static string GetProfilePath(string name)
    {
        var safe = SanitiseFileName(name);
        return Path.Combine(PortablePaths.ProfilesDirectory, $"{safe}.json");
    }

    /// <summary>Strips characters Windows forbids in file names.</summary>
    public static string SanitiseFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string([.. name.Where(c => !invalid.Contains(c))]).Trim();
        return string.IsNullOrWhiteSpace(cleaned) ? DefaultProfileName : cleaned;
    }

    /// <summary>Writes via a temp file and replace so readers never observe a partial document.</summary>
    internal static void WriteAtomic(string path, string contents)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temp = path + ".tmp";
        File.WriteAllText(temp, contents);

        if (File.Exists(path))
        {
            File.Replace(temp, path, destinationBackupFileName: null);
        }
        else
        {
            File.Move(temp, path);
        }
    }

    private static void BackupCorruptFile(string path)
    {
        try
        {
            var backup = $"{path}.corrupt-{DateTime.Now:yyyyMMdd-HHmmss}";
            File.Copy(path, backup, overwrite: true);
            Log.Warning("Corrupt file backed up to {Backup}", backup);
        }
        catch
        {
            // Nothing useful can be done if even the backup fails.
        }
    }
}
