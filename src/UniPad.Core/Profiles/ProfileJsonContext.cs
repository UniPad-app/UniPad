using System.Text.Json;
using System.Text.Json.Serialization;

namespace UniPad.Core.Profiles;

/// <summary>
/// Source-generated JSON contracts. Using the generator instead of reflection keeps serialisation
/// working after trimming and avoids the reflection warnings a single-file publish would emit.
/// </summary>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNameCaseInsensitive = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never)]
[JsonSerializable(typeof(ProfileDto))]
[JsonSerializable(typeof(PlayerProfileDto))]
[JsonSerializable(typeof(AppConfigDto))]
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(Dictionary<string, string>))]
public partial class ProfileJsonContext : JsonSerializerContext
{
}

/// <summary>Shared serialiser options for hand-written call sites.</summary>
public static class ProfileJson
{
    /// <summary>Options used for every profile and config read or write.</summary>
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        TypeInfoResolver = ProfileJsonContext.Default,
    };
}
