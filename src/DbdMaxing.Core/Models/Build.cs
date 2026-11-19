namespace DbdMaxing.Core.Models;

/// <summary>A recommended 4-perk loadout. <see cref="Killer"/> is the killer character id the build was tuned for, if any.</summary>
public sealed record Build(
    Role Role,
    IReadOnlyList<Perk> Perks,
    double Score,
    IReadOnlyList<string> Reasons,
    string Name,
    string? Killer = null);
