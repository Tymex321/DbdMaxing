namespace DbdMaxing.Core.Models;

/// <summary>A single perk. <see cref="Id"/> is the slug of the display name (see <see cref="PerkIds.Slug"/>).</summary>
public sealed record Perk(
    string Id,
    string Name,
    Role Role,
    string? CharacterId,
    IReadOnlyList<string> Tags,
    string? Description = null,
    string? IconUrl = null)
{
    /// <summary>True for the general perks every character can unlock in the bloodweb.</summary>
    public bool IsGeneral => CharacterId is null;
}
