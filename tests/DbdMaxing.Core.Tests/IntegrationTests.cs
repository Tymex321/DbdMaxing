using DbdMaxing.Core.Builds;
using DbdMaxing.Core.Data;

namespace DbdMaxing.Core.Tests;

/// <summary>Runs the real bundled catalog and meta.json through the build engine.</summary>
public class IntegrationTests
{
    [Theory]
    [InlineData(Role.Survivor)]
    [InlineData(Role.Killer)]
    public void Bundled_data_produces_five_distinct_builds_per_role(Role role)
    {
        var perks = PerkCatalog.BundledCatalog.ToPerks();
        var engine = new BuildEngine(new MetaProvider().Load());

        var builds = engine.Recommend(role, perks, 5);

        Assert.Equal(5, builds.Count);
        Assert.All(builds, b =>
        {
            Assert.Equal(4, b.Perks.Count);
            Assert.All(b.Perks, p => Assert.Equal(role, p.Role));
            Assert.NotEmpty(b.Reasons);
        });
        Assert.True(builds[0].Score >= builds[^1].Score);
    }

    [Fact]
    public void Killer_tuning_uses_catalog_character_ids()
    {
        var bundle = PerkCatalog.BundledCatalog;
        var blight = bundle.ToCharacters().Single(c => c.Name == "The Blight");
        var engine = new BuildEngine(new MetaProvider().Load());

        var builds = engine.Recommend(Role.Killer, bundle.ToPerks(), 3, blight.Id);

        Assert.All(builds, b => Assert.Equal(blight.Id, b.Killer));
    }
}
