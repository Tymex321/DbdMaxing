using System.Diagnostics;
using DbdMaxing.Core.Builds;

namespace DbdMaxing.Core.Tests;

public class BuildEngineTests
{
    // ---------- helpers ----------

    private static Perk S(string id, params string[] tags) => new(id, Title(id), Role.Survivor, null, tags);
    private static Perk K(string id, params string[] tags) => new(id, Title(id), Role.Killer, null, tags);

    private static string Title(string id) =>
        string.Join(' ', id.Split('-').Select(w => w.Length == 0 ? w : char.ToUpperInvariant(w[0]) + w[1..]));

    private static MetaData Meta(params (string Id, double Score)[] scores)
    {
        var meta = new MetaData { Version = "test" };
        foreach (var (id, score) in scores) meta.Perks[id] = new PerkMeta { Score = score };
        return meta;
    }

    private static void Synergy(MetaData meta, string a, string b, double bonus, string reason = "combo") =>
        meta.Synergies.Add(new SynergyRule { A = a, B = b, Bonus = bonus, Reason = reason });

    private static HashSet<string> Ids(Build b) => b.Perks.Select(p => p.Id).ToHashSet();

    // ---------- tests ----------

    [Fact]
    public void Uses_only_owned_perks()
    {
        var meta = Meta(("dead-hard", 9.5), ("sprint-burst", 9), ("adrenaline", 8), ("kindred", 7),
            ("bond", 4), ("resilience", 3), ("botany", 3), ("spine-chill", 3), ("lithe", 2));
        var owned = new[] { S("bond"), S("resilience"), S("botany"), S("spine-chill"), S("lithe") };

        var builds = new BuildEngine(meta).Recommend(Role.Survivor, owned, 5);

        Assert.NotEmpty(builds);
        var ownedIds = owned.Select(p => p.Id).ToHashSet();
        Assert.All(builds, b => Assert.All(b.Perks, p => Assert.Contains(p.Id, ownedIds)));
        Assert.All(builds, b => Assert.Equal(4, b.Perks.Count));
    }

    [Fact]
    public void Respects_role()
    {
        var meta = Meta(("pain-res", 10), ("pop", 10), ("bbq", 10), ("lethal", 10), ("nowhere", 10),
            ("bond", 2), ("kindred", 2), ("botany", 2), ("lithe", 2));
        var owned = new[]
        {
            K("pain-res"), K("pop"), K("bbq"), K("lethal"), K("nowhere"),
            S("bond"), S("kindred"), S("botany"), S("lithe"),
        };
        var engine = new BuildEngine(meta);

        var survivor = engine.Recommend(Role.Survivor, owned, 3);
        Assert.Single(survivor); // only 4 survivor perks -> exactly one combination
        Assert.All(survivor, b => Assert.Equal(Role.Survivor, b.Role));
        Assert.All(survivor[0].Perks, p => Assert.Equal(Role.Survivor, p.Role));

        var killer = engine.Recommend(Role.Killer, owned, 3);
        Assert.All(killer, b => Assert.All(b.Perks, p => Assert.Equal(Role.Killer, p.Role)));
    }

    [Fact]
    public void Exhaustion_limit_is_enforced()
    {
        var meta = Meta(("dead-hard", 9), ("sprint-burst", 8.5), ("lithe", 8), ("balanced-landing", 7.5),
            ("bond", 5), ("kindred", 5), ("botany", 5), ("resilience", 5));
        meta.TagLimits.Add(new TagLimit { Tag = "exhaustion", Max = 1, PenaltyPerExtra = 6 });
        var owned = new[]
        {
            S("dead-hard", "exhaustion"), S("sprint-burst", "exhaustion"), S("lithe", "exhaustion"),
            S("balanced-landing", "exhaustion"), S("bond"), S("kindred"), S("botany"), S("resilience"),
        };

        var builds = new BuildEngine(meta).Recommend(Role.Survivor, owned, 3);

        var best = builds[0];
        Assert.Equal(1, best.Perks.Count(p => p.Tags.Contains("exhaustion")));
        Assert.Contains(best.Perks, p => p.Id == "dead-hard");
    }

    [Fact]
    public void Exhaustion_penalty_is_explained_when_unavoidable()
    {
        var meta = Meta(("dead-hard", 9), ("sprint-burst", 8.5), ("bond", 5), ("kindred", 5));
        meta.TagLimits.Add(new TagLimit { Tag = "exhaustion", Max = 1, PenaltyPerExtra = 3 });
        var owned = new[] { S("dead-hard", "exhaustion"), S("sprint-burst", "Exhaustion"), S("bond"), S("kindred") };

        var build = Assert.Single(new BuildEngine(meta).Recommend(Role.Survivor, owned));

        Assert.Contains(build.Reasons, r => r.StartsWith("Two exhaustion perks: only one can be active"));
        Assert.Equal(9 + 8.5 + 5 + 5 - 3 + 0.5, build.Score, 6); // + chase coverage group
    }

    [Fact]
    public void Synergy_pairs_are_preferred()
    {
        var meta = Meta(("pentimento", 5), ("undying", 5), ("c", 6), ("d", 6), ("e", 6), ("f", 6));
        Synergy(meta, "undying", "pentimento", 4, "totem protection");
        var owned = new[] { K("pentimento"), K("undying"), K("c"), K("d"), K("e"), K("f") };

        var best = new BuildEngine(meta).Recommend(Role.Killer, owned, 1).Single();

        Assert.Contains("pentimento", Ids(best));
        Assert.Contains("undying", Ids(best));
        Assert.Contains("Pentimento + Undying: totem protection (+4.0)", best.Reasons);
    }

    [Fact]
    public void Anti_synergy_pairs_are_avoided()
    {
        var meta = Meta(("a", 9), ("b", 9), ("c", 6), ("d", 6), ("e", 6));
        Synergy(meta, "a", "b", -5, "conflict");
        var owned = new[] { K("a"), K("b"), K("c"), K("d"), K("e") };

        var best = new BuildEngine(meta).Recommend(Role.Killer, owned, 1).Single();

        Assert.False(Ids(best).Contains("a") && Ids(best).Contains("b"));
    }

    [Fact]
    public void Synergy_partner_outside_top_k_is_still_considered()
    {
        var meta = new MetaData();
        var owned = new List<Perk>();
        for (var i = 0; i < 40; i++)
        {
            var id = $"p{i:00}";
            meta.Perks[id] = new PerkMeta { Score = 8 - i * 0.1 };
            owned.Add(K(id));
        }
        meta.Perks["sleeper"] = new PerkMeta { Score = 0.5 };
        owned.Add(K("sleeper"));
        Synergy(meta, "p00", "sleeper", 10, "hidden gem");

        var best = new BuildEngine(meta).Recommend(Role.Killer, owned, 1).Single();

        Assert.Contains("sleeper", Ids(best));
        Assert.Contains("p00", Ids(best));
    }

    [Fact]
    public void Returned_builds_are_diverse_and_ordered()
    {
        var meta = new MetaData();
        var owned = new List<Perk>();
        for (var i = 0; i < 12; i++)
        {
            var id = $"perk-{i:00}";
            meta.Perks[id] = new PerkMeta { Score = 10 - i * 0.5 };
            owned.Add(S(id));
        }

        var builds = new BuildEngine(meta).Recommend(Role.Survivor, owned, 4);

        Assert.Equal(4, builds.Count);
        for (var i = 0; i < builds.Count; i++)
        for (var j = i + 1; j < builds.Count; j++)
        {
            Assert.True(Ids(builds[i]).Intersect(Ids(builds[j])).Count() <= 2,
                $"builds {i} and {j} share more than 2 perks");
            Assert.True(builds[i].Score >= builds[j].Score);
        }
    }

    [Fact]
    public void Perks_are_ordered_by_score_desc()
    {
        var meta = Meta(("low", 2), ("mid", 5), ("high", 9), ("top", 9.5));
        var owned = new[] { S("low"), S("mid"), S("high"), S("top") };

        var build = new BuildEngine(meta).Recommend(Role.Survivor, owned).Single();

        Assert.Equal(new[] { "top", "high", "mid", "low" }, build.Perks.Select(p => p.Id));
        Assert.Contains("Top-tier: Top (9.5)", build.Reasons);
    }

    [Fact]
    public void Fewer_than_four_owned_returns_smaller_build()
    {
        var owned = new[] { S("bond"), S("kindred"), S("botany") };

        var builds = new BuildEngine(new MetaData()).Recommend(Role.Survivor, owned);

        var build = Assert.Single(builds);
        Assert.Equal(3, build.Perks.Count);
        Assert.Equal(3.0, build.Score, 6); // default score 1.0 each, no tags
    }

    [Fact]
    public void Empty_owned_returns_empty()
    {
        var engine = new BuildEngine(new MetaData());
        Assert.Empty(engine.Recommend(Role.Survivor, Array.Empty<Perk>()));
        Assert.Empty(engine.Recommend(Role.Survivor, new[] { K("pop"), K("bbq") }));
        Assert.Empty(engine.Recommend(Role.Survivor, new[] { S("bond") }, count: 0));
    }

    [Fact]
    public void Killer_bonus_changes_ranking()
    {
        var meta = Meta(("a", 6), ("b", 6), ("c", 6), ("d", 6), ("e", 6));
        meta.Perks["nurse-special"] = new PerkMeta
        {
            Score = 4,
            KillerBonus = new() { ["nurse"] = 4 },
        };
        var owned = new[] { K("a"), K("b"), K("c"), K("d"), K("e"), K("nurse-special") };
        var engine = new BuildEngine(meta);

        var generic = engine.Recommend(Role.Killer, owned, 1).Single();
        var nurse = engine.Recommend(Role.Killer, owned, 1, killerId: "nurse").Single();

        Assert.DoesNotContain("nurse-special", Ids(generic));
        Assert.Null(generic.Killer);
        Assert.Contains("nurse-special", Ids(nurse));
        Assert.Equal("nurse", nurse.Killer);
        Assert.Equal("nurse-special", nurse.Perks[0].Id);
        Assert.Contains("Nurse Special suits nurse (+4.0)", nurse.Reasons);
    }

    [Fact]
    public void Names_build_after_best_matching_archetype()
    {
        var meta = Meta(("pain-res", 9), ("grim", 8), ("pop", 8), ("bbq", 7), ("lethal", 1), ("nowhere", 1));
        meta.Archetypes.Add(new Archetype { Name = "Chase", Role = Role.Killer, Tags = new() { "chase" } });
        meta.Archetypes.Add(new Archetype { Name = "Gen Slowdown", Role = Role.Killer, Tags = new() { "gen-slowdown" } });
        meta.Archetypes.Add(new Archetype { Name = "Survivor Thing", Role = Role.Survivor, Tags = new() { "gen-slowdown" } });
        var owned = new[]
        {
            K("pain-res", "gen-slowdown"), K("grim", "gen-slowdown"), K("pop", "gen-slowdown"),
            K("bbq", "info"), K("lethal", "chase"), K("nowhere", "chase"),
        };

        var best = new BuildEngine(meta).Recommend(Role.Killer, owned, 1).Single();

        Assert.Equal("Gen Slowdown", best.Name);
        Assert.Contains("Strong gen slowdown (3 perks)", best.Reasons);
    }

    [Fact]
    public void Falls_back_to_balanced_name()
    {
        var owned = new[] { S("a"), S("b"), S("c"), S("d") };
        var build = new BuildEngine(new MetaData()).Recommend(Role.Survivor, owned).Single();
        Assert.Equal(BuildEngine.FallbackName, build.Name);
        Assert.Equal("Balanced", build.Name);
    }

    [Fact]
    public void Coverage_bonus_rewards_different_useful_tags()
    {
        // Same base scores; only tag coverage differs.
        var meta = Meta(("chase1", 5), ("chase2", 5), ("heal", 5), ("info", 5), ("x1", 5));
        var owned = new[] { S("chase1", "chase"), S("chase2", "chase"), S("heal", "healing"), S("info", "aura"), S("x1") };

        var best = new BuildEngine(meta).Recommend(Role.Survivor, owned, 1).Single();

        Assert.Contains("heal", Ids(best));
        Assert.Contains("info", Ids(best));
        Assert.Contains(best.Reasons, r => r.StartsWith("Covers chase, healing and info"));
    }

    [Fact]
    public void Reasons_are_capped()
    {
        var meta = Meta(("a", 9), ("b", 9), ("c", 9), ("d", 9));
        Synergy(meta, "a", "b", 1); Synergy(meta, "a", "c", 1); Synergy(meta, "a", "d", 1);
        Synergy(meta, "b", "c", 1); Synergy(meta, "b", "d", 1); Synergy(meta, "c", "d", 1);
        var owned = new[] { K("a"), K("b"), K("c"), K("d") };

        var build = new BuildEngine(meta).Recommend(Role.Killer, owned).Single();

        Assert.Equal(5, build.Reasons.Count);
    }

    [Fact]
    public void Is_deterministic_regardless_of_input_order()
    {
        var (meta, owned) = BigCatalog(60, seed: 7);
        var engine = new BuildEngine(meta);
        var shuffled = owned.OrderBy(_ => Guid.NewGuid()).ToList();

        var a = engine.Recommend(Role.Killer, owned, 5);
        var b = engine.Recommend(Role.Killer, shuffled, 5);

        Assert.Equal(a.Count, b.Count);
        for (var i = 0; i < a.Count; i++)
        {
            Assert.Equal(a[i].Perks.Select(p => p.Id), b[i].Perks.Select(p => p.Id));
            Assert.Equal(a[i].Score, b[i].Score);
            Assert.Equal(a[i].Reasons, b[i].Reasons);
        }
    }

    [Fact]
    public void Ties_break_by_perk_id()
    {
        var owned = new[] { S("e"), S("c"), S("a"), S("d"), S("b") };
        var build = new BuildEngine(new MetaData()).Recommend(Role.Survivor, owned, 1).Single();
        Assert.Equal(new[] { "a", "b", "c", "d" }, build.Perks.Select(p => p.Id));
    }

    [Fact]
    public void Handles_150_perks_quickly()
    {
        var (meta, owned) = BigCatalog(150, seed: 42);
        var engine = new BuildEngine(meta);

        var sw = Stopwatch.StartNew();
        var builds = engine.Recommend(Role.Killer, owned, 5, killerId: "nurse");
        sw.Stop();

        Assert.Equal(5, builds.Count);
        Assert.True(sw.ElapsedMilliseconds < 500, $"took {sw.ElapsedMilliseconds} ms");
    }

    private static (MetaData, List<Perk>) BigCatalog(int n, int seed)
    {
        var rng = new Random(seed);
        string[] tagPool = { "gen-slowdown", "chase", "info", "aura", "hex", "exhaustion", "endgame", "regression" };
        var meta = new MetaData();
        meta.TagLimits.Add(new TagLimit { Tag = "hex", Max = 1, PenaltyPerExtra = 2 });
        meta.TagLimits.Add(new TagLimit { Tag = "gen-slowdown", Max = 3, PenaltyPerExtra = 1.5 });
        meta.Archetypes.Add(new Archetype { Name = "Gen Slowdown", Role = Role.Killer, Tags = new() { "gen-slowdown", "regression" } });
        meta.Archetypes.Add(new Archetype { Name = "Chase", Role = Role.Killer, Tags = new() { "chase" } });
        var owned = new List<Perk>();
        for (var i = 0; i < n; i++)
        {
            var id = $"perk-{i:000}";
            var tags = tagPool.Where(_ => rng.NextDouble() < 0.2).ToArray();
            owned.Add(K(id, tags));
            if (rng.NextDouble() < 0.8)
                meta.Perks[id] = new PerkMeta
                {
                    Score = Math.Round(rng.NextDouble() * 10, 1),
                    KillerBonus = rng.NextDouble() < 0.1 ? new() { ["nurse"] = 1.5 } : null,
                };
        }
        for (var i = 0; i < 60; i++)
            Synergy(meta, $"perk-{rng.Next(n):000}", $"perk-{rng.Next(n):000}", Math.Round(rng.NextDouble() * 6 - 2, 1));
        return (meta, owned);
    }
}
