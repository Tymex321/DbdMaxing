using DbdMaxing.Core.Models;

namespace DbdMaxing.Core.Steam;

public interface ISteamAccountReader
{
    /// <summary>Steam install dir (registry on Windows, or the path passed to the constructor), or null.</summary>
    string? SteamPath { get; }

    /// <summary>The account marked MostRecent in config/loginusers.vdf.</summary>
    SteamAccount? GetCurrentAccount();

    /// <summary>Best-effort set of owned DLC ids for Dead by Daylight (app 381210). Empty if unknown.</summary>
    IReadOnlySet<string> GetOwnedDlcIds();

    bool IsDbdInstalled();
}
