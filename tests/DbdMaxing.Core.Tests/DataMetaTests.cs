using DbdMaxing.Core.Data;

namespace DbdMaxing.Core.Tests;

public sealed class DataMetaTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dbdmaxing-meta-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    private static readonly MetaData Meta = MetaProvider.LoadEmbedded();
    private static readonly Dictionary<string, BundlePerk> Perks = PerkCatalog.BundledCatalog.Perks.ToDictionary(p => p.Id);

    [Fact]
    public void Every_meta_perk_id_exists_in_bundled_catalog()
    {
        var unknown = Meta.Perks.Keys.Where(id => !Perks.ContainsKey(id)).ToList();
        Assert.True(unknown.Count == 0, "Unknown perk ids in meta.json: " + string.Join(", ", unknown));
    }

    [Fact]
    public void Every_synergy_references_known_perks()
    {
        var unknown = Meta.Synergies.SelectMany(s => new[] { s.A, s.B }).Where(id => !Perks.ContainsKey(id)).Distinct().ToList();
        Assert.True(unknown.Count == 0, "Unknown perk ids in synergies: " + string.Join(", ", unknown));
        Assert.All(Meta.Synergies, s =>
        {
            Assert.NotEqual(s.A, s.B);
            Assert.Equal(Perks[s.A].Role, Perks[s.B].Role);
            Assert.False(string.IsNullOrWhiteSpace(s.Reason));
        });
        Assert.True(Meta.Synergies.Count >= 40);
        Assert.Contains(Meta.Synergies, s => s.Bonus < 0);
    }

    [Fact]
    public void Killer_bonus_keys_are_killer_ids_on_killer_perks()
    {
        var killers = PerkCatalog.BundledCatalog.Characters.Where(c => c.Role == Role.Killer).Select(c => c.Id).ToHashSet();
        foreach (var (id, meta) in Meta.Perks.Where(kv => kv.Value.KillerBonus is not null))
        {
            Assert.Equal(Role.Killer, Perks[id].Role);
            Assert.All(meta.KillerBonus!.Keys, k => Assert.Contains(k, killers));
        }
        Assert.True(Meta.Perks["scourge-hook-pain-resonance"].KillerBonus!["268435476"] > 0); // Blight
    }

    [Fact]
    public void Scores_are_in_range_and_cover_both_roles()
    {
        Assert.All(Meta.Perks.Values, m => Assert.InRange(m.Score, 0, 10));
        Assert.True(Meta.Perks.Keys.Count(id => Perks[id].Role == Role.Survivor) >= 60);
        Assert.True(Meta.Perks.Keys.Count(id => Perks[id].Role == Role.Killer) >= 60);
    }

    [Fact]
    public void Tag_limits_and_archetypes_use_known_tags()
    {
        var knownTags = Perks.Values.SelectMany(p => p.Tags).ToHashSet();
        Assert.All(Meta.TagLimits, l => Assert.Contains(l.Tag, knownTags));
        Assert.All(Meta.Archetypes, a => Assert.All(a.Tags, t => Assert.Contains(t, knownTags)));

        var exhaustion = Meta.TagLimits.Single(l => l.Tag == "exhaustion");
        Assert.Equal(1, exhaustion.Max);
        Assert.Contains(Meta.Archetypes, a => a.Name == "Gen Slowdown" && a.Role == Role.Killer);
        Assert.Contains(Meta.Archetypes, a => a.Name == "Meta Loop" && a.Role == Role.Survivor);
        Assert.Equal(6, Meta.Archetypes.Count(a => a.Role == Role.Survivor));
        Assert.Equal(6, Meta.Archetypes.Count(a => a.Role == Role.Killer));
    }

    [Fact]
    public void Override_file_wins_when_present()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "meta.override.json");
        File.WriteAllText(path, """{ "Version": "override", "perks": { "sprint-burst": { "score": 1.5 } }, "archetypes": [ { "name": "X", "role": "Killer", "tags": ["chase"] } ] }""");
        var provider = new MetaProvider(path);

        var meta = provider.Load();

        Assert.True(provider.UsedOverride);
        Assert.Equal("override", meta.Version);
        Assert.Equal(1.5, meta.Perks["sprint-burst"].Score);
        Assert.Equal(Role.Killer, meta.Archetypes.Single().Role);
    }

    [Fact]
    public void Missing_or_broken_override_falls_back_to_embedded()
    {
        var missing = new MetaProvider(Path.Combine(_dir, "nope.json"));
        Assert.Equal(Meta.Version, missing.Load().Version);
        Assert.False(missing.UsedOverride);

        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "meta.override.json");
        File.WriteAllText(path, "{ broken");
        var broken = new MetaProvider(path);
        Assert.Equal(Meta.Version, broken.Load().Version);
        Assert.False(broken.UsedOverride);
    }
}
