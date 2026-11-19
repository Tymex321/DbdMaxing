namespace DbdMaxing.Core.Models;

public enum PerkSource
{
    Manual,
    Scanned,
    Dlc,
}

public sealed record OwnedPerk(string PerkId, PerkSource Source, DateTimeOffset FirstSeen);

/// <summary>The perks an account is known to own, keyed by perk id. Manual removals win over scans.</summary>
public sealed class OwnedPerkSet
{
    public Dictionary<string, OwnedPerk> Perks { get; set; } = new();

    /// <summary>Perk ids the user explicitly un-ticked; scans/DLC will not re-add them.</summary>
    public HashSet<string> ManuallyExcluded { get; set; } = new();

    public bool Contains(string perkId) => Perks.ContainsKey(perkId);

    /// <summary>Returns true if the perk was newly added.</summary>
    public bool Add(string perkId, PerkSource source)
    {
        if (source == PerkSource.Manual)
            ManuallyExcluded.Remove(perkId);
        else if (ManuallyExcluded.Contains(perkId))
            return false;

        return Perks.TryAdd(perkId, new OwnedPerk(perkId, source, DateTimeOffset.UtcNow));
    }

    public void RemoveManually(string perkId)
    {
        Perks.Remove(perkId);
        ManuallyExcluded.Add(perkId);
    }
}
