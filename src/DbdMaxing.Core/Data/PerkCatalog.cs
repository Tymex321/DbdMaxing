using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using DbdMaxing.Core.Models;

namespace DbdMaxing.Core.Data;

/// <summary>Where the currently loaded catalog came from.</summary>
public enum CatalogSource
{
    None,
    Network,
    Cache,
    StaleCache,
    Bundled,
}

/// <summary>
/// Perk/character catalog backed by the public dbd.tricky.lol API, cached in <c>{cacheDir}/catalog.json</c>
/// (refreshed after 3 days), falling back to the stale cache and then to the embedded Data/perks.json.
/// Icons come from the DBD wiki (deadbydaylight.wiki.gg).
/// </summary>
public sealed class PerkCatalog : IPerkCatalog
{
    public const string PerksUrl = "https://dbd.tricky.lol/api/perks";
    public const string CharactersUrl = "https://dbd.tricky.lol/api/characters";
    public const string BundledResourceName = "DbdMaxing.Core.Data.perks.json";
    public const string CacheFileName = "catalog.json";
    public static readonly TimeSpan MaxCacheAge = TimeSpan.FromDays(3);
    public const int MaxParallelDownloads = 6;

    // wiki.gg rate-limits anonymous clients without an identifying User-Agent.
    private static readonly ProductInfoHeaderValue UserAgent = new("DbdMaxing", "1.0");
    private static readonly ProductInfoHeaderValue UserAgentComment = new("(+DBD perk build helper)");

    private static readonly Lazy<CatalogBundle> Bundled = new(LoadBundled);

    private readonly HttpClient _http;
    private readonly string _cacheDir;
    private readonly string _iconDir;
    private IReadOnlyList<Perk> _perks = Array.Empty<Perk>();
    private IReadOnlyList<GameCharacter> _characters = Array.Empty<GameCharacter>();
    private Dictionary<string, Perk> _byId = new(StringComparer.Ordinal);

    public PerkCatalog(HttpClient http, string? cacheDir = null)
    {
        _http = http;
        _cacheDir = cacheDir ?? AppPaths.CacheDir;
        _iconDir = cacheDir is null ? AppPaths.IconDir : Path.Combine(cacheDir, "icons");
    }

    public IReadOnlyList<Perk> Perks => _perks;
    public IReadOnlyList<GameCharacter> Characters => _characters;
    public CatalogSource Source { get; private set; }
    /// <summary>When the loaded data was fetched from the network (null for the bundled fallback).</summary>
    public DateTimeOffset? FetchedAt { get; private set; }
    public string CachePath => Path.Combine(_cacheDir, CacheFileName);
    public string IconDir => _iconDir;

    public Perk? Find(string perkId) => _byId.GetValueOrDefault(perkId);

    public string? GetIconPath(string perkId)
    {
        var path = IconFile(perkId);
        return File.Exists(path) ? path : null;
    }

    public async Task LoadAsync(CancellationToken ct = default)
    {
        var cache = TryReadCache();
        if (cache is { } fresh && DateTimeOffset.UtcNow - fresh.FetchedAt < MaxCacheAge)
        {
            Apply(fresh.Perks, fresh.Characters, CatalogSource.Cache, fresh.FetchedAt);
            return;
        }

        try
        {
            var perksJson = await GetStringAsync(PerksUrl, ct).ConfigureAwait(false);
            var charactersJson = await GetStringAsync(CharactersUrl, ct).ConfigureAwait(false);
            var (perks, characters) = ParseApi(perksJson, charactersJson);
            if (perks.Count == 0)
                throw new InvalidDataException("API returned no perks.");
            var now = DateTimeOffset.UtcNow;
            TryWriteCache(perksJson, charactersJson, now);
            Apply(perks, characters, CatalogSource.Network, now);
            return;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidDataException or IOException
                                   && !ct.IsCancellationRequested)
        {
            // Offline or API changed: fall through to the stale cache / bundled data.
        }

        if (cache is { } stale)
        {
            Apply(stale.Perks, stale.Characters, CatalogSource.StaleCache, stale.FetchedAt);
            return;
        }

        var bundle = Bundled.Value;
        Apply(bundle.ToPerks(), bundle.ToCharacters(), CatalogSource.Bundled, null);
    }

    public async Task EnsureIconsAsync(IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var missing = _perks.Where(p => !string.IsNullOrEmpty(p.IconUrl) && GetIconPath(p.Id) is null).ToList();
        if (missing.Count == 0)
        {
            progress?.Report(1.0);
            return;
        }

        Directory.CreateDirectory(_iconDir);
        var done = 0;
        var options = new ParallelOptions { MaxDegreeOfParallelism = MaxParallelDownloads, CancellationToken = ct };
        await Parallel.ForEachAsync(missing, options, async (perk, token) =>
        {
            try
            {
                await DownloadIconAsync(perk, token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException
                                       || (ex is TaskCanceledException && !token.IsCancellationRequested))
            {
                // A missing icon only degrades screen recognition for that perk; try again next run.
            }
            progress?.Report((double)Interlocked.Increment(ref done) / missing.Count);
        }).ConfigureAwait(false);
    }

    private async Task DownloadIconAsync(Perk perk, CancellationToken ct)
    {
        byte[]? bytes = null;
        for (var attempt = 1; bytes is null; attempt++)
        {
            using var request = NewRequest(perk.IconUrl!);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests && attempt < 3)
            {
                var wait = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(2 * attempt);
                await Task.Delay(wait > TimeSpan.FromSeconds(30) ? TimeSpan.FromSeconds(30) : wait, ct).ConfigureAwait(false);
                continue;
            }
            response.EnsureSuccessStatusCode();
            bytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        }
        if (!IsPng(bytes))
            throw new IOException($"Icon for {perk.Id} is not a PNG.");

        var target = IconFile(perk.Id);
        var temp = target + ".part";
        await File.WriteAllBytesAsync(temp, bytes, ct).ConfigureAwait(false);
        File.Move(temp, target, overwrite: true);
    }

    private static bool IsPng(byte[] b) =>
        b.Length > 8 && b[0] == 0x89 && b[1] == (byte)'P' && b[2] == (byte)'N' && b[3] == (byte)'G';

    private string IconFile(string perkId) => Path.Combine(_iconDir, perkId + ".png");

    private void Apply(IReadOnlyList<Perk> perks, IReadOnlyList<GameCharacter> characters, CatalogSource source, DateTimeOffset? fetchedAt)
    {
        var byId = new Dictionary<string, Perk>(StringComparer.Ordinal);
        foreach (var p in perks)
            byId.TryAdd(p.Id, p);
        _perks = byId.Values.OrderBy(p => p.Role).ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
        _characters = characters;
        _byId = byId;
        Source = source;
        FetchedAt = fetchedAt;
    }

    private async Task<string> GetStringAsync(string url, CancellationToken ct)
    {
        using var request = NewRequest(url);
        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
    }

    private HttpRequestMessage NewRequest(string url)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (_http.DefaultRequestHeaders.UserAgent.Count == 0)
        {
            request.Headers.UserAgent.Add(UserAgent);
            request.Headers.UserAgent.Add(UserAgentComment);
        }
        return request;
    }

    // ---- cache -------------------------------------------------------------------------------

    private sealed record CacheContent(IReadOnlyList<Perk> Perks, IReadOnlyList<GameCharacter> Characters, DateTimeOffset FetchedAt);

    private CacheContent? TryReadCache()
    {
        try
        {
            if (!File.Exists(CachePath))
                return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(CachePath));
            var root = doc.RootElement;
            var fetchedAt = root.TryGetProperty("fetchedAt", out var f) && f.TryGetDateTimeOffset(out var at)
                ? at
                : new DateTimeOffset(File.GetLastWriteTimeUtc(CachePath), TimeSpan.Zero);
            var (perks, characters) = ParseApi(root.GetProperty("perks"), root.GetProperty("characters"));
            return perks.Count == 0 ? null : new CacheContent(perks, characters, fetchedAt);
        }
        catch (Exception ex) when (ex is JsonException or IOException or KeyNotFoundException or InvalidOperationException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private void TryWriteCache(string perksJson, string charactersJson, DateTimeOffset fetchedAt)
    {
        try
        {
            Directory.CreateDirectory(_cacheDir);
            var node = new JsonObject
            {
                ["fetchedAt"] = fetchedAt.ToString("O", CultureInfo.InvariantCulture),
                ["source"] = PerksUrl,
                ["perks"] = JsonNode.Parse(perksJson),
                ["characters"] = JsonNode.Parse(charactersJson),
            };
            var temp = CachePath + ".tmp";
            File.WriteAllText(temp, node.ToJsonString());
            File.Move(temp, CachePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Cache is best effort.
        }
    }

    // ---- API parsing -------------------------------------------------------------------------

    /// <summary>Parses the raw /api/perks and /api/characters JSON (objects keyed by internal id).</summary>
    public static (IReadOnlyList<Perk> Perks, IReadOnlyList<GameCharacter> Characters) ParseApi(string perksJson, string charactersJson)
    {
        using var perks = JsonDocument.Parse(perksJson);
        using var characters = JsonDocument.Parse(charactersJson);
        return ParseApi(perks.RootElement, characters.RootElement);
    }

    public static (IReadOnlyList<Perk> Perks, IReadOnlyList<GameCharacter> Characters) ParseApi(JsonElement perksRoot, JsonElement charactersRoot)
    {
        var characters = new List<GameCharacter>();
        if (charactersRoot.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in charactersRoot.EnumerateObject())
            {
                var c = prop.Value;
                if (c.ValueKind != JsonValueKind.Object || ParseRole(Str(c, "role")) is not { } role)
                    continue;
                var name = Str(c, "name");
                if (string.IsNullOrWhiteSpace(name))
                    continue;
                var dlc = c.TryGetProperty("dlc", out var d) ? d.ValueKind switch
                {
                    JsonValueKind.String => d.GetString(),
                    JsonValueKind.Number => d.GetRawText(),
                    _ => null,
                } : null;
                characters.Add(new GameCharacter(prop.Name, name.Trim(), role, string.IsNullOrWhiteSpace(dlc) ? null : dlc));
            }
        }

        var icons = Bundled.Value.Perks
            .Where(p => !string.IsNullOrEmpty(p.Icon))
            .GroupBy(p => p.Id)
            .ToDictionary(g => g.Key, g => g.First().Icon!, StringComparer.Ordinal);

        var perks = new List<Perk>();
        if (perksRoot.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in perksRoot.EnumerateObject())
            {
                var p = prop.Value;
                if (p.ValueKind != JsonValueKind.Object || ParseRole(Str(p, "role")) is not { } role)
                    continue;
                var name = Str(p, "name")?.Trim();
                if (string.IsNullOrEmpty(name))
                    continue;
                var id = PerkIds.Slug(name);
                if (id.Length == 0)
                    continue;

                string? characterId = p.TryGetProperty("character", out var ch) ? ch.ValueKind switch
                {
                    JsonValueKind.Number => ch.GetRawText(),
                    JsonValueKind.String when !string.IsNullOrWhiteSpace(ch.GetString()) => ch.GetString(),
                    _ => null,
                } : null;

                JsonElement? tunables = p.TryGetProperty("tunables", out var t) ? t : null;
                var description = PerkText.Clean(Str(p, "description"), tunables);
                var tags = PerkTagger.Infer(name, role, description);
                var icon = icons.TryGetValue(id, out var known) ? known : PerkText.WikiIconUrl(name);
                perks.Add(new Perk(id, name, role, characterId, tags, description, icon));
            }
        }

        return (perks, characters);
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static Role? ParseRole(string? role) => role?.Trim().ToLowerInvariant() switch
    {
        "survivor" => Role.Survivor,
        "killer" => Role.Killer,
        _ => null,
    };

    // ---- bundled fallback --------------------------------------------------------------------

    private static CatalogBundle LoadBundled()
    {
        using var stream = typeof(PerkCatalog).Assembly.GetManifestResourceStream(BundledResourceName);
        if (stream is null)
            return new CatalogBundle();
        using var reader = new StreamReader(stream);
        return CatalogBundle.Parse(reader.ReadToEnd());
    }

    /// <summary>The embedded fallback catalog (Data/perks.json).</summary>
    public static CatalogBundle BundledCatalog => Bundled.Value;
}
