namespace DbdMaxing.Core;

public static class AppPaths
{
    public static string Root { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DbdMaxing");

    public static string CacheDir => Path.Combine(Root, "cache");
    public static string IconDir => Path.Combine(CacheDir, "icons");
    public static string ProfilesDir => Path.Combine(Root, "profiles");
    public static string SettingsFile => Path.Combine(Root, "settings.json");
    public static string LogFile => Path.Combine(Root, "dbdmaxing.log");
}
