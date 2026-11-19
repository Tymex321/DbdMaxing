using System.Text.Json;
using DbdMaxing.Core.Data;

namespace DbdMaxing.Core.Tests;

public class DataTagTests
{
    [Theory]
    [InlineData("Sprint Burst", "sprint-burst")]
    [InlineData("Barbecue & Chili", "barbecue-chili")]
    [InlineData("Scourge Hook: Pain Resonance", "scourge-hook-pain-resonance")]
    [InlineData("Hex: Ruin", "hex-ruin")]
    [InlineData("Dead Man's Switch", "dead-mans-switch")]
    [InlineData("Coup de Grâce", "coup-de-grace")]
    [InlineData("Quick & Quiet", "quick-quiet")]
    [InlineData("Self-Care", "self-care")]
    public void Slugs_match_catalog_ids(string name, string id) => Assert.Equal(id, PerkIds.Slug(name));

    [Theory]
    [InlineData("sprint-burst", "exhaustion,chase,haste")]
    [InlineData("lithe", "exhaustion")]
    [InlineData("dead-hard", "exhaustion,endurance,anti-tunnel")]
    [InlineData("off-the-record", "anti-tunnel,endurance,stealth")]
    [InlineData("kindred", "aura,info")]
    [InlineData("boon-circle-of-healing", "boon,healing,totem")]
    [InlineData("deja-vu", "gen-speed,aura")]
    [InlineData("hex-ruin", "hex,totem,gen-slowdown,regression,slowdown")]
    [InlineData("scourge-hook-pain-resonance", "scourge-hook,gen-slowdown,regression")]
    [InlineData("grim-embrace", "gen-slowdown,obsession")]
    [InlineData("deadlock", "gen-slowdown")]
    [InlineData("barbecue-chili", "aura,tracking,info")]
    [InlineData("hex-no-one-escapes-death", "hex,endgame,exposed")]
    [InlineData("no-way-out", "endgame")]
    [InlineData("sloppy-butcher", "slowdown,healing")]
    public void Bundled_perks_have_expected_tags(string id, string expected)
    {
        var perk = PerkCatalog.BundledCatalog.Perks.Single(p => p.Id == id);
        foreach (var tag in expected.Split(','))
            Assert.Contains(tag, perk.Tags);
    }

    [Theory]
    [InlineData("adrenaline", "exhaustion")]     // ignores Exhausted, not an exhaustion perk
    [InlineData("vigil", "exhaustion")]          // recovers from Exhausted
    [InlineData("rapid-response", "exhaustion")] // reacts to Exhausted
    [InlineData("hex-ruin", "chase")]
    [InlineData("kindred", "gen-slowdown")]
    public void Bundled_perks_do_not_get_wrong_tags(string id, string tag)
    {
        var perk = PerkCatalog.BundledCatalog.Perks.Single(p => p.Id == id);
        Assert.DoesNotContain(tag, perk.Tags);
    }

    [Fact]
    public void Infer_is_role_aware()
    {
        const string desc = "Whenever you hook a Survivor, the Generator with the most progress explodes and starts regressing.";
        Assert.Contains("gen-slowdown", PerkTagger.Infer("Test", Role.Killer, desc));
        Assert.DoesNotContain("gen-slowdown", PerkTagger.Infer("Test", Role.Survivor, desc));
        Assert.Contains("hex", PerkTagger.Infer("Hex: Anything", Role.Killer, ""));
        Assert.Contains("boon", PerkTagger.Infer("Boon: Anything", Role.Survivor, ""));
    }

    [Fact]
    public void Infer_returns_sorted_distinct_tags()
    {
        var tags = PerkTagger.Infer("Hex: Thing", Role.Killer, "You see the Auras of Survivors. Survivors suffer from Exposed and Hindered.");
        Assert.Equal(tags.OrderBy(t => t, StringComparer.Ordinal), tags);
        Assert.Equal(tags.Distinct().Count(), tags.Count);
    }

    [Fact]
    public void Clean_substitutes_tunables_keywords_and_strips_html()
    {
        using var doc = JsonDocument.Parse("""{"hasteduration":[4],"exhaustion%":[60,50,40]}""");
        var text = PerkText.Clean(
            "Gain {Keyword.Haste} for {Tunable.S02P03.HasteDuration}s.<ul><li>{Keyword.DeepWound} &amp; {Tunable.X.Exhaustion%}%</li></ul><br>Press {Input.ActivatableButton1}.",
            doc.RootElement);
        Assert.Equal("Gain Haste for 4s.\n• Deep Wound & 60/50/40%\n\nPress the Ability button.", text);
    }

    [Fact]
    public void WikiIconUrl_follows_wiki_naming()
    {
        Assert.Equal("https://deadbydaylight.wiki.gg/images/IconPerks_hexRuin.png", PerkText.WikiIconUrl("Hex: Ruin"));
        Assert.Equal("https://deadbydaylight.wiki.gg/images/IconPerks_self-Care.png", PerkText.WikiIconUrl("Self-Care"));
        Assert.Equal("https://deadbydaylight.wiki.gg/images/IconPerks_deadMansSwitch.png", PerkText.WikiIconUrl("Dead Man's Switch"));
    }
}
