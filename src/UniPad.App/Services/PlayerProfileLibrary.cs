using UniPad.Core.Mapping;
using UniPad.Core.Profiles;

namespace UniPad.App.Services;

/// <summary>
/// The shared list of single-player profiles. Every player tab reads from this one instance, so a
/// profile created or deleted in one tab appears in, or disappears from, every other tab at once.
/// Used from the UI thread only.
/// </summary>
public sealed class PlayerProfileLibrary
{
    private readonly List<string> _names = [];

    /// <summary>Loads the list from disk.</summary>
    public PlayerProfileLibrary() => _names.AddRange(PlayerProfileStore.List());

    /// <summary>Profile names, sorted.</summary>
    public IReadOnlyList<string> Names => _names;

    /// <summary>Raised after the list was re-read, whether or not it actually changed.</summary>
    public event Action? Changed;

    /// <summary>True when a profile with this name exists, ignoring case as Windows does.</summary>
    public bool Contains(string name)
    {
        var trimmed = name.Trim();
        return _names.Any(n => string.Equals(n, trimmed, StringComparison.OrdinalIgnoreCase))
               || PlayerProfileStore.Exists(trimmed);
    }

    /// <summary>Saves a player's settings under a name, then refreshes every tab.</summary>
    public bool Save(string name, PlayerMapping mapping)
    {
        if (!PlayerProfileStore.Save(name, mapping))
        {
            return false;
        }

        Reload();
        return true;
    }

    /// <summary>Reads a profile, or null when it is missing or unreadable.</summary>
    public PlayerProfileDto? Load(string name) => PlayerProfileStore.Load(name);

    /// <summary>Deletes a profile, then refreshes every tab.</summary>
    public bool Delete(string name)
    {
        if (!PlayerProfileStore.Delete(name))
        {
            return false;
        }

        Reload();
        return true;
    }

    /// <summary>Re-reads the folder and notifies every tab.</summary>
    public void Reload()
    {
        _names.Clear();
        _names.AddRange(PlayerProfileStore.List());
        Changed?.Invoke();
    }
}
