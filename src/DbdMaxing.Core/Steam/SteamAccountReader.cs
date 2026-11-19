using System.Globalization;
using System.Runtime.Versioning;
using DbdMaxing.Core.Models;

namespace DbdMaxing.Core.Steam;

/// <summary>
/// Reads Steam's own text files on disk (never the game, never the network) to find the logged-in
/// account, whether Dead by Daylight is installed, and a best-effort set of owned DLC.
/// Every public member is exception-safe: IO/parse failures yield null, false or an empty set.
/// </summary>
public sealed class SteamAccountReader : ISteamAccountReader
{
    public const string DbdAppId = "381210";

    /// <summary>Offset between a SteamID64 and the 32-bit account id used for userdata folders.</summary>
    public const ulong SteamId64Base = 76561197960265728UL;

    internal const string DefaultWindowsSteamPath = @"C:\Program Files (x86)\Steam";

    private readonly Lazy<string?> _steamPath;

    /// <param name="steamPathOverride">
    /// Steam install directory to use instead of the registry lookup (used by tests and settings).
    /// </param>
    public SteamAccountReader(string? steamPathOverride = null)
    {
        _steamPath = new Lazy<string?>(() => ResolveSteamPath(steamPathOverride));
    }

    /// <inheritdoc />
    public string? SteamPath => _steamPath.Value;

    /// <summary>
    /// Parses <c>config/loginusers.vdf</c> and returns the user marked <c>MostRecent "1"</c>, falling back
    /// to the user with the highest <c>Timestamp</c>. Null if Steam or the file can't be found.
    /// </summary>
    public SteamAccount? GetCurrentAccount()
    {
        try
        {
            if (SteamPath is not { } steam) return null;
            var root = VdfParser.TryParseFile(Path.Combine(steam, "config", "loginusers.vdf"));
            var users = root?["users"];
            if (users is null) return null;

            var candidates = users.Children
                .Where(u => u.IsObject && ulong.TryParse(u.Key, NumberStyles.None, CultureInfo.InvariantCulture, out _))
                .ToList();
            if (candidates.Count == 0) return null;

            var chosen = candidates.FirstOrDefault(u => u.GetValue("MostRecent") == "1")
                         ?? candidates.OrderByDescending(u => ParseLong(u.GetValue("Timestamp"))).First();

            return new SteamAccount(
                chosen.Key,
                chosen.GetValue("AccountName") ?? "",
                chosen.GetValue("PersonaName") ?? "");
        }
        catch (Exception e) when (IsExpected(e))
        {
            return null;
        }
    }

    /// <summary>True if <c>appmanifest_381210.acf</c> exists in any Steam library folder.</summary>
    public bool IsDbdInstalled()
    {
        try
        {
            return FindAppManifest(DbdAppId) is not null;
        }
        catch (Exception e) when (IsExpected(e))
        {
            return false;
        }
    }

    /// <summary>
    /// Best-effort list of Dead by Daylight DLC app ids that local Steam files show as owned.
    /// </summary>
    /// <remarks>
    /// <para>What this reads: the <c>InstalledDepots</c> section of <c>appmanifest_381210.acf</c>
    /// (in every library folder). Depots belonging to a DLC carry a <c>"dlcappid"</c> entry; Steam only
    /// downloads such depots when the account holds a licence for that DLC, so every id found there is
    /// owned. <c>StagedDepots</c> (a pending update) is read the same way.</para>
    /// <para>Limits: most Dead by Daylight chapters are licence-only unlocks with no content depot of
    /// their own (the content ships in the base game), so they never appear here. The result is
    /// therefore a lower bound and is usually empty or very small. Disabled optional DLC is also absent.</para>
    /// <para>Not used, because they don't reflect ownership: <c>userdata/&lt;accountid&gt;/config/localconfig.vdf</c>
    /// (play-time, launch options and cloud state per app, not licences); <c>appcache/appinfo.vdf</c> (binary,
    /// lists every DLC that exists, owned or not); <c>appcache/packageinfo.vdf</c> and the licence cache
    /// (binary or encrypted). Real ownership needs the Steam Web API or the Steamworks client (ISteamApps::BIsDlcInstalled),
    /// which run inside or against a logged-in session and are out of scope here.
    /// Perk ownership should come from the in-game scanner and manual input; treat this as a hint.</para>
    /// </remarks>
    public IReadOnlySet<string> GetOwnedDlcIds()
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            foreach (var manifestPath in FindAllAppManifests(DbdAppId))
            {
                var root = VdfParser.TryParseFile(manifestPath);
                var state = root?["AppState"];
                if (state is null) continue;
                CollectDlcAppIds(state["InstalledDepots"], result);
                CollectDlcAppIds(state["StagedDepots"], result);
            }
        }
        catch (Exception e) when (IsExpected(e))
        {
            // best-effort: return whatever was collected
        }
        result.Remove(DbdAppId);
        return result;
    }

    /// <summary>All Steam library folders (the Steam dir itself first), de-duplicated. Never throws.</summary>
    public IReadOnlyList<string> GetLibraryFolders()
    {
        var folders = new List<string>();
        try
        {
            if (SteamPath is not { } steam) return folders;
            folders.Add(steam);

            var root = VdfParser.TryParseFile(Path.Combine(steam, "steamapps", "libraryfolders.vdf"))
                       ?? VdfParser.TryParseFile(Path.Combine(steam, "config", "libraryfolders.vdf"));
            var lf = root?["libraryfolders"] ?? root?["LibraryFolders"];
            if (lf is not null)
            {
                foreach (var entry in lf.Children)
                {
                    // New format: "0" { "path" "D:\\SteamLibrary" ... }. Old format: "1" "D:\\SteamLibrary".
                    if (!entry.IsObject && !int.TryParse(entry.Key, out _)) continue; // e.g. "TimeNextStatsReport"
                    string? path = entry.IsObject ? entry.GetValue("path") : entry.Value;
                    if (!string.IsNullOrWhiteSpace(path)) folders.Add(NormalizePath(path));
                }
            }
        }
        catch (Exception e) when (IsExpected(e))
        {
            // fall through with what we have
        }

        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        return folders.Distinct(comparer).ToList();
    }

    /// <summary>Path to <c>appmanifest_{appId}.acf</c> in the first library that has it, or null.</summary>
    public string? FindAppManifest(string appId) => FindAllAppManifests(appId).FirstOrDefault();

    private IEnumerable<string> FindAllAppManifests(string appId)
    {
        var found = new List<string>();
        foreach (var lib in GetLibraryFolders())
        {
            try
            {
                var p = Path.Combine(lib, "steamapps", $"appmanifest_{appId}.acf");
                if (File.Exists(p)) found.Add(p);
            }
            catch (Exception e) when (IsExpected(e))
            {
            }
        }
        return found;
    }

    /// <summary>Converts a SteamID64 to the 32-bit account id used for <c>userdata/&lt;id&gt;</c>.</summary>
    public static string? ToAccountId32(string steamId64) =>
        ulong.TryParse(steamId64, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id >= SteamId64Base
            ? (id - SteamId64Base).ToString(CultureInfo.InvariantCulture)
            : null;

    private static void CollectDlcAppIds(VdfNode? depots, HashSet<string> into)
    {
        if (depots is null) return;
        foreach (var depot in depots.Children)
        {
            if (!depot.IsObject) continue;
            var dlc = depot.GetValue("dlcappid");
            if (dlc is not null && uint.TryParse(dlc, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id != 0)
                into.Add(id.ToString(CultureInfo.InvariantCulture));
        }
    }

    private static string? ResolveSteamPath(string? overridePath)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(overridePath))
                return NormalizePath(overridePath);

            if (OperatingSystem.IsWindows())
            {
                var fromRegistry = ReadRegistrySteamPath();
                if (!string.IsNullOrWhiteSpace(fromRegistry)) return NormalizePath(fromRegistry);
                return DefaultWindowsSteamPath;
            }
        }
        catch (Exception e) when (IsExpected(e))
        {
        }
        return null;
    }

    [SupportedOSPlatform("windows")]
    private static string? ReadRegistrySteamPath()
    {
        try
        {
            using var hkcu = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            if (hkcu?.GetValue("SteamPath") is string p && !string.IsNullOrWhiteSpace(p)) return p;
        }
        catch (Exception e) when (IsExpected(e) || e is System.Security.SecurityException)
        {
        }

        try
        {
            using var hklm = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Valve\Steam");
            if (hklm?.GetValue("InstallPath") is string p && !string.IsNullOrWhiteSpace(p)) return p;
        }
        catch (Exception e) when (IsExpected(e) || e is System.Security.SecurityException)
        {
        }
        return null;
    }

    /// <summary>Steam writes HKCU SteamPath with forward slashes ("c:/program files (x86)/steam").</summary>
    private static string NormalizePath(string path)
    {
        path = path.Trim();
        if (OperatingSystem.IsWindows()) path = path.Replace('/', '\\');
        return path.TrimEnd('\\', '/') is { Length: > 0 } trimmed && !trimmed.EndsWith(':') ? trimmed : path;
    }

    private static long ParseLong(string? s) =>
        long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : long.MinValue;

    private static bool IsExpected(Exception e) =>
        e is IOException or UnauthorizedAccessException or FormatException or ArgumentException
            or NotSupportedException or PlatformNotSupportedException or InvalidOperationException
            or System.Security.SecurityException;
}
