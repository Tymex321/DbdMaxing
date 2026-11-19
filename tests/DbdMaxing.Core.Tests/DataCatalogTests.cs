using System.Net;
using System.Text.Json.Nodes;
using DbdMaxing.Core.Data;

namespace DbdMaxing.Core.Tests;

public sealed class DataCatalogTests : IDisposable
{
    private static readonly string FixtureDir = Path.Combine(AppContext.BaseDirectory, "Fixtures", "catalog");
    private static string FixturePerks => File.ReadAllText(Path.Combine(FixtureDir, "perks.json"));
    private static string FixtureCharacters => File.ReadAllText(Path.Combine(FixtureDir, "characters.json"));

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dbdmaxing-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void ParseApi_maps_perks_from_fixture()
    {
        var (perks, _) = PerkCatalog.ParseApi(FixturePerks, FixtureCharacters);

        Assert.Equal(8, perks.Count);
        var ids = perks.Select(p => p.Id).ToHashSet();
        Assert.Superset(ids, new HashSet<string>
        {
            "adrenaline", "sprint-burst", "hex-ruin", "scourge-hook-pain-resonance", "barbecue-chili", "kindred", "dead-hard", "deja-vu",
        });

        var pr = perks.Single(p => p.Id == "scourge-hook-pain-resonance");
        Assert.Equal(Role.Killer, pr.Role);
        Assert.Equal("268435481", pr.CharacterId);
        Assert.False(pr.IsGeneral);

        var kindred = perks.Single(p => p.Id == "kindred");
        Assert.Equal(Role.Survivor, kindred.Role);
        Assert.Null(kindred.CharacterId);
        Assert.True(kindred.IsGeneral);

        foreach (var p in perks)
        {
            Assert.False(string.IsNullOrWhiteSpace(p.Description));
            Assert.DoesNotContain("<", p.Description);
            Assert.DoesNotContain("{", p.Description);
            Assert.StartsWith("https://", p.IconUrl);
            Assert.EndsWith(".png", p.IconUrl);
            Assert.NotEmpty(p.Tags);
        }

        var adrenaline = perks.Single(p => p.Id == "adrenaline");
        Assert.Contains("Haste", adrenaline.Description);
        Assert.Contains("60/50/40", adrenaline.Description); // tunables substituted per tier
    }

    [Fact]
    public void ParseApi_maps_characters_from_fixture()
    {
        var (_, characters) = PerkCatalog.ParseApi(FixturePerks, FixtureCharacters);

        Assert.Equal(6, characters.Count);
        var blight = characters.Single(c => c.Id == "268435476");
        Assert.Equal("The Blight", blight.Name);
        Assert.Equal(Role.Killer, blight.Role);
        Assert.Equal("Yemen", blight.DlcId);

        var meg = characters.Single(c => c.Id == "1");
        Assert.Equal(Role.Survivor, meg.Role);
        Assert.Null(meg.DlcId);
    }

    [Fact]
    public void ParseApi_tolerates_unknown_roles_and_missing_fields()
    {
        const string perks = """{"A":{"name":"Thing","role":"spectator"},"B":{"role":"killer"},"C":{"name":"Plain Perk","role":"killer","character":null}}""";
        var (parsed, chars) = PerkCatalog.ParseApi(perks, "{}");
        var only = Assert.Single(parsed);
        Assert.Equal("plain-perk", only.Id);
        Assert.Empty(chars);
    }

    [Fact]
    public async Task LoadAsync_fetches_from_network_and_writes_cache()
    {
        var handler = new FakeHandler(ServeFixtures);
        var catalog = new PerkCatalog(new HttpClient(handler), _dir);

        await catalog.LoadAsync();

        Assert.Equal(CatalogSource.Network, catalog.Source);
        Assert.Equal(8, catalog.Perks.Count);
        Assert.NotNull(catalog.Find("sprint-burst"));
        Assert.True(File.Exists(Path.Combine(_dir, "catalog.json")));
        Assert.Contains(handler.Requests, u => u == PerkCatalog.PerksUrl);
        Assert.Contains(handler.Requests, u => u == PerkCatalog.CharactersUrl);
    }

    [Fact]
    public async Task LoadAsync_uses_fresh_cache_without_network()
    {
        WriteCache(DateTimeOffset.UtcNow.AddHours(-1));
        var handler = new FakeHandler((Func<HttpRequestMessage, HttpResponseMessage>)(_ => throw new InvalidOperationException("network must not be used")));
        var catalog = new PerkCatalog(new HttpClient(handler), _dir);

        await catalog.LoadAsync();

        Assert.Equal(CatalogSource.Cache, catalog.Source);
        Assert.Empty(handler.Requests);
        Assert.Equal(8, catalog.Perks.Count);
    }

    [Fact]
    public async Task LoadAsync_refreshes_stale_cache()
    {
        WriteCache(DateTimeOffset.UtcNow.AddDays(-4));
        var handler = new FakeHandler(ServeFixtures);
        var catalog = new PerkCatalog(new HttpClient(handler), _dir);

        await catalog.LoadAsync();

        Assert.Equal(CatalogSource.Network, catalog.Source);
        Assert.NotEmpty(handler.Requests);
        Assert.True(catalog.FetchedAt > DateTimeOffset.UtcNow.AddMinutes(-1));
    }

    [Fact]
    public async Task LoadAsync_falls_back_to_stale_cache_when_offline()
    {
        WriteCache(DateTimeOffset.UtcNow.AddDays(-10));
        var catalog = new PerkCatalog(new HttpClient(new FakeHandler(Offline)), _dir);

        await catalog.LoadAsync();

        Assert.Equal(CatalogSource.StaleCache, catalog.Source);
        Assert.Equal(8, catalog.Perks.Count);
    }

    [Fact]
    public async Task LoadAsync_falls_back_to_bundled_when_offline_without_cache()
    {
        var catalog = new PerkCatalog(new HttpClient(new FakeHandler(Offline)), _dir);

        await catalog.LoadAsync();

        Assert.Equal(CatalogSource.Bundled, catalog.Source);
        Assert.True(catalog.Perks.Count > 250);
        Assert.True(catalog.Characters.Count > 80);
        Assert.NotNull(catalog.Find("sprint-burst"));
        Assert.NotNull(catalog.Find("scourge-hook-pain-resonance"));
    }

    [Fact]
    public async Task LoadAsync_falls_back_to_bundled_when_cache_is_corrupt_and_api_errors()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "catalog.json"), "{ not json");
        var catalog = new PerkCatalog(new HttpClient(new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError))), _dir);

        await catalog.LoadAsync();

        Assert.Equal(CatalogSource.Bundled, catalog.Source);
    }

    [Fact]
    public async Task EnsureIconsAsync_downloads_missing_icons_with_bounded_parallelism()
    {
        WriteCache(DateTimeOffset.UtcNow);
        var inFlight = 0;
        var maxInFlight = 0;
        var handler = new FakeHandler(async req =>
        {
            var now = Interlocked.Increment(ref inFlight);
            InterlockedMax(ref maxInFlight, now);
            await Task.Delay(30);
            Interlocked.Decrement(ref inFlight);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Png) };
        });
        var catalog = new PerkCatalog(new HttpClient(handler), _dir);
        await catalog.LoadAsync();

        var reports = new List<double>();
        await catalog.EnsureIconsAsync(new SyncProgress(reports.Add));

        Assert.All(catalog.Perks, p => Assert.NotNull(catalog.GetIconPath(p.Id)));
        Assert.Equal(Path.Combine(_dir, "icons", "sprint-burst.png"), catalog.GetIconPath("sprint-burst"));
        Assert.InRange(maxInFlight, 1, PerkCatalog.MaxParallelDownloads);
        Assert.Equal(1.0, reports.Max(), 3);
        Assert.Equal(8, handler.Requests.Count);

        // Second run: nothing missing, nothing downloaded.
        await catalog.EnsureIconsAsync();
        Assert.Equal(8, handler.Requests.Count);
    }

    [Fact]
    public async Task EnsureIconsAsync_skips_failed_downloads()
    {
        WriteCache(DateTimeOffset.UtcNow);
        var handler = new FakeHandler(req => req.RequestUri!.AbsoluteUri.Contains("sprint", StringComparison.OrdinalIgnoreCase)
            ? new HttpResponseMessage(HttpStatusCode.NotFound)
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Png) });
        var catalog = new PerkCatalog(new HttpClient(handler), _dir);
        await catalog.LoadAsync();

        await catalog.EnsureIconsAsync();

        Assert.Null(catalog.GetIconPath("sprint-burst"));
        Assert.NotNull(catalog.GetIconPath("adrenaline"));
        Assert.Null(catalog.GetIconPath("not-a-perk"));
    }

    [Fact]
    public void Bundled_catalog_is_consistent()
    {
        var bundle = PerkCatalog.BundledCatalog;
        Assert.True(bundle.Perks.Count > 250);
        Assert.Equal(bundle.Perks.Count, bundle.Perks.Select(p => p.Id).Distinct().Count());

        var characterIds = bundle.Characters.Select(c => c.Id).ToHashSet();
        foreach (var p in bundle.Perks)
        {
            Assert.Equal(PerkIds.Slug(p.Name), p.Id);
            Assert.StartsWith(PerkText.WikiImageBase, p.Icon);
            if (p.Character is not null)
                Assert.Contains(p.Character, characterIds);
        }
        Assert.Contains(bundle.Characters, c => c.Name == "The Blight" && c.Role == Role.Killer);
    }

    // ---- helpers -----------------------------------------------------------------------------

    private static readonly byte[] Png = { 0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0 };

    private void WriteCache(DateTimeOffset fetchedAt)
    {
        Directory.CreateDirectory(_dir);
        var node = new JsonObject
        {
            ["fetchedAt"] = fetchedAt.ToString("O"),
            ["perks"] = JsonNode.Parse(FixturePerks),
            ["characters"] = JsonNode.Parse(FixtureCharacters),
        };
        File.WriteAllText(Path.Combine(_dir, "catalog.json"), node.ToJsonString());
    }

    private static HttpResponseMessage ServeFixtures(HttpRequestMessage req)
    {
        var body = req.RequestUri!.AbsoluteUri switch
        {
            PerkCatalog.PerksUrl => FixturePerks,
            PerkCatalog.CharactersUrl => FixtureCharacters,
            _ => null,
        };
        return body is null
            ? new HttpResponseMessage(HttpStatusCode.NotFound)
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) };
    }

    private static HttpResponseMessage Offline(HttpRequestMessage _) => throw new HttpRequestException("offline");

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        while (value > (current = Volatile.Read(ref target)) && Interlocked.CompareExchange(ref target, value, current) != current) { }
    }

    private sealed class SyncProgress(Action<double> report) : IProgress<double>
    {
        private readonly object _gate = new();
        public void Report(double value) { lock (_gate) report(value); }
    }

    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _respond;
        private readonly object _gate = new();
        public List<string> Requests { get; } = new();

        public FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = r => Task.FromResult(respond(r));
        public FakeHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) => _respond = respond;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            lock (_gate) Requests.Add(request.RequestUri!.AbsoluteUri);
            return _respond(request);
        }
    }
}
