using DbdMaxing.Core.Storage;

namespace DbdMaxing.Core.Tests;

public class StorageProfileStoreTests : IDisposable
{
    private const string Id = "76561198012345678";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dbdmaxing-profiles-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void Missing_profile_loads_empty()
    {
        var set = new JsonProfileStore(_dir).Load(Id);
        Assert.Empty(set.Perks);
        Assert.Empty(set.ManuallyExcluded);
    }

    [Fact]
    public void Round_trips_perks_sources_and_exclusions()
    {
        var store = new JsonProfileStore(_dir);
        var set = new OwnedPerkSet();
        set.Add("sprint_burst", PerkSource.Scanned);
        set.Add("dead_hard", PerkSource.Manual);
        set.Add("barbecue_and_chilli", PerkSource.Dlc);
        set.RemoveManually("dead_hard");
        store.Save(Id, set);

        var loaded = new JsonProfileStore(_dir).Load(Id);
        Assert.Equal(new[] { "barbecue_and_chilli", "sprint_burst" }, loaded.Perks.Keys.Order());
        Assert.Equal(PerkSource.Scanned, loaded.Perks["sprint_burst"].Source);
        Assert.Equal(PerkSource.Dlc, loaded.Perks["barbecue_and_chilli"].Source);
        Assert.Equal(set.Perks["sprint_burst"].FirstSeen, loaded.Perks["sprint_burst"].FirstSeen);
        Assert.Contains("dead_hard", loaded.ManuallyExcluded);
        Assert.False(loaded.Add("dead_hard", PerkSource.Scanned)); // exclusion survived the round trip

        Assert.Equal(new[] { Id + ".json" }, Directory.GetFiles(_dir).Select(Path.GetFileName)); // no temp files left
    }

    [Fact]
    public void Save_overwrites_existing()
    {
        var store = new JsonProfileStore(_dir);
        var a = new OwnedPerkSet();
        a.Add("one", PerkSource.Manual);
        store.Save(Id, a);
        var b = new OwnedPerkSet();
        b.Add("two", PerkSource.Manual);
        store.Save(Id, b);

        Assert.Equal(new[] { "two" }, store.Load(Id).Perks.Keys);
    }

    [Theory]
    [InlineData("{ this is not json")]
    [InlineData("null")]
    [InlineData("[1,2,3]")]
    [InlineData("")]
    public void Corrupt_file_is_backed_up_and_empty_returned(string content)
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, Id + ".json");
        File.WriteAllText(path, content);

        var set = new JsonProfileStore(_dir).Load(Id);

        Assert.Empty(set.Perks);
        Assert.False(File.Exists(path));
        Assert.Equal(content, File.ReadAllText(path + ".bak"));
    }

    [Fact]
    public void Profiles_are_separate_per_account()
    {
        var store = new JsonProfileStore(_dir);
        var a = new OwnedPerkSet();
        a.Add("adrenaline", PerkSource.Manual);
        store.Save("1", a);

        Assert.Empty(store.Load("2").Perks);
        Assert.Single(store.Load("1").Perks);
    }

    [Fact]
    public void Steam_id_cannot_escape_the_directory()
    {
        var store = new JsonProfileStore(_dir);
        var path = store.PathFor("../../evil");
        Assert.Equal(Path.GetFullPath(_dir), Path.GetDirectoryName(Path.GetFullPath(path)));
    }

    [Fact]
    public void Defaults_to_AppPaths_profiles_dir()
    {
        Assert.Equal(AppPaths.ProfilesDir, new JsonProfileStore().Directory);
    }

    [Fact]
    public async Task Concurrent_saves_and_loads_do_not_corrupt()
    {
        var store = new JsonProfileStore(_dir);
        var tasks = Enumerable.Range(0, 40).Select(i => Task.Run(() =>
        {
            var set = new OwnedPerkSet();
            set.Add("perk_" + i, PerkSource.Scanned);
            store.Save(Id, set);
            Assert.Single(store.Load(Id).Perks);
        }));
        await Task.WhenAll(tasks);

        Assert.Single(store.Load(Id).Perks);
        Assert.False(File.Exists(Path.Combine(_dir, Id + ".json.bak")));
    }
}
