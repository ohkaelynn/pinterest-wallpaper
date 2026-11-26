using System.Text.Json;

namespace PinterestWallpaper.Core.Config;

public sealed class ConfigStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private readonly string _configPath;

    public ConfigStore(string configPath)
    {
        _configPath = configPath;
    }

    public AppConfig LoadOrCreate()
    {
        if (!File.Exists(_configPath))
        {
            var defaults = new AppConfig();
            Save(defaults);
            return defaults;
        }

        var json = File.ReadAllText(_configPath);
        var config = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions) ?? new AppConfig();
        ClampSafeDefaults(config);
        return config;
    }

    public void Save(AppConfig config)
    {
        ClampSafeDefaults(config);
        var dir = Path.GetDirectoryName(_configPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var json = JsonSerializer.Serialize(config, JsonOptions);
        File.WriteAllText(_configPath, json);
    }

    private static void ClampSafeDefaults(AppConfig config)
    {
        config.Rotation.IntervalMinutes = Math.Clamp(config.Rotation.IntervalMinutes, 1, 1440);
        config.Rotation.JitterPercent = Math.Clamp(config.Rotation.JitterPercent, 0, 50);
        config.Fetching.MinSyncCooldownMinutes = Math.Max(config.Fetching.MinSyncCooldownMinutes, 5);
        config.Fetching.MaxSyncsPerDay = Math.Clamp(config.Fetching.MaxSyncsPerDay, 1, 48);
        config.Fetching.MaxPinsPerSync = Math.Clamp(config.Fetching.MaxPinsPerSync, 1, 100);
        config.Fetching.MaxScrollStepsPerSource = Math.Clamp(config.Fetching.MaxScrollStepsPerSource, 1, 8);
        config.Fetching.MaxCacheMb = Math.Clamp(config.Fetching.MaxCacheMb, 50, 10000);
    }
}
