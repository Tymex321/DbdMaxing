namespace DbdMaxing.Core.Models;

/// <summary>Contents of Data/meta.json: hand-maintained rankings that drive the build engine. All keys are perk ids.</summary>
public sealed class MetaData
{
    public string Version { get; set; } = "";

    public Dictionary<string, PerkMeta> Perks { get; set; } = new();

    public List<SynergyRule> Synergies { get; set; } = new();

    /// <summary>Caps on how many perks of one tag a build should run (e.g. max 1 "exhaustion").</summary>
    public List<TagLimit> TagLimits { get; set; } = new();

    /// <summary>Optional named archetypes, e.g. "Gen Slowdown", "Chase", used to label builds.</summary>
    public List<Archetype> Archetypes { get; set; } = new();
}

/// <summary>Score is a 0–10 tier value. KillerBonus adds/subtracts per killer character id.</summary>
public sealed class PerkMeta
{
    public double Score { get; set; }
    public Dictionary<string, double>? KillerBonus { get; set; }
    public string? Note { get; set; }
}

public sealed class SynergyRule
{
    public string A { get; set; } = "";
    public string B { get; set; } = "";
    /// <summary>Positive for synergy, negative for anti-synergy.</summary>
    public double Bonus { get; set; }
    public string Reason { get; set; } = "";
}

public sealed class TagLimit
{
    public string Tag { get; set; } = "";
    public int Max { get; set; }
    public double PenaltyPerExtra { get; set; }
}

public sealed class Archetype
{
    public string Name { get; set; } = "";
    public Role Role { get; set; }
    /// <summary>Tags that identify this archetype; a build matching most of them gets this name.</summary>
    public List<string> Tags { get; set; } = new();
}
