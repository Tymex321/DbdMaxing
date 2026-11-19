using System.Text.Json;
using System.Text.Json.Serialization;
using DbdMaxing.Core.Models;

namespace DbdMaxing.Core.Storage;

/// <summary>
/// Stores one <c>{steamId64}.json</c> per account. Writes are atomic (temp file + move), a corrupt
/// file is moved aside to <c>{steamId64}.json.bak</c> and treated as empty, and all access is serialised.
/// </summary>
public sealed class JsonProfileStore : IProfileStore
{
    // Shared across instances so two stores pointing at the same folder don't race.
    private static readonly object Gate = new();

    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _dir;

    /// <param name="dir">Folder for profile files; defaults to <see cref="AppPaths.ProfilesDir"/>.</param>
    public JsonProfileStore(string? dir = null)
    {
        _dir = string.IsNullOrWhiteSpace(dir) ? AppPaths.ProfilesDir : dir;
    }

    public string Directory => _dir;

    public OwnedPerkSet Load(string steamId64)
    {
        var path = PathFor(steamId64);
        lock (Gate)
        {
            if (!File.Exists(path)) return new OwnedPerkSet();

            try
            {
                var json = File.ReadAllText(path);
                var set = JsonSerializer.Deserialize<OwnedPerkSet>(json, JsonOptions)
                          ?? throw new JsonException("Profile file contained null.");
                set.Perks ??= new();
                set.ManuallyExcluded ??= new();
                // Drop entries whose value failed to bind.
                foreach (var key in set.Perks.Where(kv => kv.Value is null).Select(kv => kv.Key).ToList())
                    set.Perks.Remove(key);
                return set;
            }
            catch (JsonException)
            {
                MoveAsideCorrupt(path);
                return new OwnedPerkSet();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Locked or unreadable: don't destroy it, just behave as empty for now.
                return new OwnedPerkSet();
            }
        }
    }

    public void Save(string steamId64, OwnedPerkSet perks)
    {
        ArgumentNullException.ThrowIfNull(perks);
        var path = PathFor(steamId64);
        lock (Gate)
        {
            System.IO.Directory.CreateDirectory(_dir);
            var json = JsonSerializer.Serialize(perks, JsonOptions);
            var tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(tmp, json);
                File.Move(tmp, path, overwrite: true);
            }
            finally
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    internal string PathFor(string steamId64)
    {
        if (string.IsNullOrWhiteSpace(steamId64))
            throw new ArgumentException("Steam id is required.", nameof(steamId64));

        // SteamID64s are digits; sanitise anything else so it can't escape the folder.
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string(steamId64.Trim().Select(c => invalid.Contains(c) || c == '.' ? '_' : c).ToArray());
        return Path.Combine(_dir, safe + ".json");
    }

    private static void MoveAsideCorrupt(string path)
    {
        try
        {
            File.Move(path, path + ".bak", overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }
}
