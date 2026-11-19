using System.Text.Json;
using System.Text.Json.Serialization;
using DbdMaxing.Core.Models;

namespace DbdMaxing.Core.Data;

/// <summary>
/// The normalized catalog format of the bundled Data/perks.json (offline fallback).
/// <code>
/// { "version": "2026-11-19", "source": "...", "iconSource": "...",
///   "characters": [ { "id": "268435476", "name": "The Blight", "role": "killer", "dlc": "Descend" } ],
///   "perks": [ { "id": "hex-ruin", "name": "Hex: Ruin", "role": "killer", "character": "268435460",
///                "tags": ["gen-slowdown", "hex", ...], "icon": "https://...png", "description": "..." } ] }
/// </code>
/// </summary>
public sealed class CatalogBundle
{
    public string Version { get; set; } = "";
    public string? Source { get; set; }
    public string? IconSource { get; set; }
    public List<BundleCharacter> Characters { get; set; } = new();
    public List<BundlePerk> Perks { get; set; } = new();

    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static CatalogBundle Parse(string json) =>
        JsonSerializer.Deserialize<CatalogBundle>(json, JsonOptions) ?? new CatalogBundle();

    public string ToJson(bool indented = false) =>
        JsonSerializer.Serialize(this, new JsonSerializerOptions(JsonOptions) { WriteIndented = indented });

    public static CatalogBundle From(string version, IEnumerable<Perk> perks, IEnumerable<GameCharacter> characters,
        bool includeDescriptions = true) => new()
    {
        Version = version,
        Source = PerkCatalog.PerksUrl,
        IconSource = PerkText.WikiImageBase,
        Characters = characters.Select(c => new BundleCharacter { Id = c.Id, Name = c.Name, Role = c.Role, Dlc = c.DlcId }).ToList(),
        Perks = perks.Select(p => new BundlePerk
        {
            Id = p.Id,
            Name = p.Name,
            Role = p.Role,
            Character = p.CharacterId,
            Tags = p.Tags.ToList(),
            Icon = p.IconUrl,
            Description = includeDescriptions ? p.Description : null,
        }).ToList(),
    };

    public IReadOnlyList<Perk> ToPerks() =>
        Perks.Select(p => new Perk(p.Id, p.Name, p.Role, p.Character, p.Tags, p.Description, p.Icon)).ToList();

    public IReadOnlyList<GameCharacter> ToCharacters() =>
        Characters.Select(c => new GameCharacter(c.Id, c.Name, c.Role, c.Dlc)).ToList();
}

public sealed class BundleCharacter
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public Role Role { get; set; }
    public string? Dlc { get; set; }
}

public sealed class BundlePerk
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public Role Role { get; set; }
    public string? Character { get; set; }
    public List<string> Tags { get; set; } = new();
    public string? Icon { get; set; }
    public string? Description { get; set; }
}
