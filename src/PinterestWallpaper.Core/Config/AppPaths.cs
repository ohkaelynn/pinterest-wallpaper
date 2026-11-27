namespace PinterestWallpaper.Core.Config;

public sealed class AppPaths
{
    public string ConfigDirectory { get; }
    public string DataDirectory { get; }
    public string ConfigFile => Path.Combine(ConfigDirectory, "config.json");
    public string DatabaseFile => Path.Combine(DataDirectory, "state.db");
    public string CacheDirectory => Path.Combine(DataDirectory, "cache");
    public string WebView2Directory => Path.Combine(DataDirectory, "WebView2");
    public string LogsDirectory => Path.Combine(DataDirectory, "logs");
    public string ActionLogFile => Path.Combine(LogsDirectory, "actions.log");

    public AppPaths(string? configDir = null, string? dataDir = null)
    {
        ConfigDirectory = configDir ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "PinterestWallpaper");

        DataDirectory = dataDir ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PinterestWallpaper");
    }

    public void EnsureDirectoriesExist()
    {
        Directory.CreateDirectory(ConfigDirectory);
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(CacheDirectory);
    }
}
