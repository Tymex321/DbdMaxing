using DbdMaxing.Core.Steam;

namespace DbdMaxing.Core.Tests;

/// <summary>Copies Fixtures/steam into a temp dir and fills in the library paths.</summary>
internal sealed class SteamFixture : IDisposable
{
    public string Dir { get; } = Path.Combine(Path.GetTempPath(), "dbdmaxing-steam-" + Guid.NewGuid().ToString("N"));
    public string Root => Path.Combine(Dir, "root");
    public string Library2 => Path.Combine(Dir, "library2");

    public SteamFixture()
    {
        var src = Path.Combine(AppContext.BaseDirectory, "Fixtures", "steam");
        foreach (var file in Directory.GetFiles(src, "*", SearchOption.AllDirectories))
        {
            var dest = Path.Combine(Dir, Path.GetRelativePath(src, file));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(file, dest);
        }

        var lf = Path.Combine(Root, "steamapps", "libraryfolders.vdf");
        File.WriteAllText(lf, File.ReadAllText(lf)
            .Replace("__ROOT__", Escape(Root))
            .Replace("__LIB2__", Escape(Library2)));
    }

    public string LoginUsers => Path.Combine(Root, "config", "loginusers.vdf");

    private static string Escape(string path) => path.Replace("\\", "\\\\");

    public void Dispose()
    {
        try { Directory.Delete(Dir, recursive: true); } catch (IOException) { }
    }
}

public class SteamAccountReaderTests
{
    [Fact]
    public void Override_path_is_used()
    {
        using var fx = new SteamFixture();
        Assert.Equal(fx.Root, new SteamAccountReader(fx.Root).SteamPath);
    }

    [Fact]
    public void Picks_MostRecent_account_even_if_not_newest_timestamp()
    {
        using var fx = new SteamFixture();
        var account = new SteamAccountReader(fx.Root).GetCurrentAccount();

        Assert.NotNull(account);
        Assert.Equal("76561198012345678", account.SteamId64);
        Assert.Equal("dwight_main", account.AccountName);
        Assert.Equal("Dwight \"Leader\" Fairfield", account.PersonaName);
    }

    [Fact]
    public void Falls_back_to_highest_timestamp_without_MostRecent()
    {
        using var fx = new SteamFixture();
        File.WriteAllText(fx.LoginUsers, File.ReadAllText(fx.LoginUsers).Replace("\"MostRecent\"\t\t\"1\"", "\"MostRecent\"\t\t\"0\""));

        var account = new SteamAccountReader(fx.Root).GetCurrentAccount();
        Assert.Equal("76561198099999999", account?.SteamId64);
        Assert.Equal("killer_alt", account?.AccountName);
    }

    [Fact]
    public void Missing_or_corrupt_loginusers_returns_null()
    {
        using var fx = new SteamFixture();
        File.WriteAllText(fx.LoginUsers, "\"users\" { \"7656\" { ");
        Assert.Null(new SteamAccountReader(fx.Root).GetCurrentAccount());

        File.Delete(fx.LoginUsers);
        Assert.Null(new SteamAccountReader(fx.Root).GetCurrentAccount());
    }

    [Fact]
    public void Nonexistent_steam_dir_is_safe_everywhere()
    {
        var reader = new SteamAccountReader(Path.Combine(Path.GetTempPath(), "no-steam-" + Guid.NewGuid()));
        Assert.Null(reader.GetCurrentAccount());
        Assert.False(reader.IsDbdInstalled());
        Assert.Empty(reader.GetOwnedDlcIds());
    }

    [Fact]
    public void Discovers_all_library_folders()
    {
        using var fx = new SteamFixture();
        var libs = new SteamAccountReader(fx.Root).GetLibraryFolders();
        Assert.Equal(new[] { fx.Root, fx.Library2 }, libs); // root appears once despite being listed twice
    }

    [Fact]
    public void Finds_dbd_in_secondary_library()
    {
        using var fx = new SteamFixture();
        var reader = new SteamAccountReader(fx.Root);
        Assert.True(reader.IsDbdInstalled());
        Assert.Equal(Path.Combine(fx.Library2, "steamapps", "appmanifest_381210.acf"), reader.FindAppManifest("381210"));
    }

    [Fact]
    public void Not_installed_when_manifest_absent()
    {
        using var fx = new SteamFixture();
        File.Delete(Path.Combine(fx.Library2, "steamapps", "appmanifest_381210.acf"));
        Assert.False(new SteamAccountReader(fx.Root).IsDbdInstalled());
    }

    [Fact]
    public void Old_style_libraryfolders_is_supported()
    {
        using var fx = new SteamFixture();
        File.WriteAllText(Path.Combine(fx.Root, "steamapps", "libraryfolders.vdf"),
            $"\"LibraryFolders\" {{ \"TimeNextStatsReport\" \"1731000000\" \"ContentStatsID\" \"-123\" \"1\" \"{fx.Library2.Replace("\\", "\\\\")}\" }}");
        var reader = new SteamAccountReader(fx.Root);
        Assert.Equal(new[] { fx.Root, fx.Library2 }, reader.GetLibraryFolders());
        Assert.True(reader.IsDbdInstalled());
    }

    [Fact]
    public void Owned_dlc_comes_from_installed_depot_dlcappid()
    {
        using var fx = new SteamFixture();
        var dlc = new SteamAccountReader(fx.Root).GetOwnedDlcIds();
        Assert.Equal(new HashSet<string> { "381212", "1199460" }, dlc.ToHashSet());
    }

    [Fact]
    public void Corrupt_manifest_gives_empty_dlc_set_but_still_installed()
    {
        using var fx = new SteamFixture();
        File.WriteAllText(Path.Combine(fx.Library2, "steamapps", "appmanifest_381210.acf"), "\"AppState\" {");
        var reader = new SteamAccountReader(fx.Root);
        Assert.Empty(reader.GetOwnedDlcIds());
        Assert.True(reader.IsDbdInstalled());
    }

    [Theory]
    [InlineData("76561198012345678", "52079950")]
    [InlineData("76561197960265728", "0")]
    [InlineData("123", null)]
    [InlineData("abc", null)]
    public void Converts_steamid64_to_account_id(string id64, string? expected)
    {
        Assert.Equal(expected, SteamAccountReader.ToAccountId32(id64));
    }
}
