namespace DbdMaxing.Core.Builds;

/// <summary>Tunables for <see cref="BuildEngine"/>. The defaults are what the app ships with.</summary>
public sealed class BuildEngineOptions
{
    public static BuildEngineOptions Default { get; } = new();

    /// <summary>Perks per build (Dead by Daylight loadouts have 4 slots).</summary>
    public int BuildSize { get; init; } = 4;

    /// <summary>How many perks to keep after ranking them individually, before the combinatorial search.</summary>
    public int TopK { get; init; } = 24;

    /// <summary>Hard cap on the candidate set once synergy partners of the top-K perks are added.</summary>
    public int MaxCandidates { get; init; } = 32;

    /// <summary>Each returned build must differ from every earlier one by at least this many perks.</summary>
    public int MinPerkDifference { get; init; } = 2;

    /// <summary>Score used for owned perks that meta.json does not list.</summary>
    public double DefaultPerkScore { get; init; } = 1.0;

    /// <summary>Bonus for every coverage group (e.g. chase, healing, info) the build touches.</summary>
    public double CoverageBonusPerGroup { get; init; } = 0.5;

    /// <summary>Perks scoring at least this much get a "Top-tier" reason.</summary>
    public double TopTierThreshold { get; init; } = 8.0;

    /// <summary>A coverage group with at least this many perks gets a "Strong ..." reason.</summary>
    public int StrongGroupMinPerks { get; init; } = 2;

    /// <summary>An archetype needs at least this many matching perks to name the build; otherwise it is "Balanced".</summary>
    public int MinArchetypeMatches { get; init; } = 2;

    public int MaxReasons { get; init; } = 5;
}
