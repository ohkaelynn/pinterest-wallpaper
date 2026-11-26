using System.Text.Json.Serialization;

namespace PinterestWallpaper.Core.Config;

public sealed class AppConfig
{
    public RotationConfig Rotation { get; set; } = new();
    public DisplayConfig Display { get; set; } = new();
    public SourcesConfig Sources { get; set; } = new();
    public FilterConfig Filters { get; set; } = new();
    public FetchingConfig Fetching { get; set; } = new();
    public ActionsConfig Actions { get; set; } = new();
    public ShellConfig Shell { get; set; } = new();
}

public sealed class ActionsConfig
{
    public bool BackgroundSaveEnabled { get; set; } = true;
    public string DefaultSaveBoardName { get; set; } = string.Empty;
    public bool LikeWhenSaving { get; set; } = true;
}

public sealed class ShellConfig
{
    public bool DesktopContextMenu { get; set; } = true;
    public bool StartWithWindows { get; set; } = false;
}

public sealed class RotationConfig
{
    public bool Enabled { get; set; } = true;
    public int IntervalMinutes { get; set; } = 30;
    public int JitterPercent { get; set; } = 15;
    public bool PauseOnBattery { get; set; } = true;
    public bool PauseOnFullscreen { get; set; } = true;
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum WallpaperFitMode
{
    Center,
    Tile,
    Stretch,
    Fit,
    Fill,
    Span
}

public sealed class DisplayConfig
{
    public WallpaperFitMode FitMode { get; set; } = WallpaperFitMode.Fill;
    public bool PerMonitorWallpapers { get; set; } = true;
}

public sealed class SourcesConfig
{
    public List<BoardSource> Boards { get; set; } = [];
    public List<SearchSource> Searches { get; set; } = [];
    public HomeFeedSource HomeFeed { get; set; } = new();
}

public sealed class BoardSource
{
    public string Url { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public int Weight { get; set; } = 50;
}

public sealed class SearchSource
{
    public string Query { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public int Weight { get; set; } = 35;
}

public sealed class HomeFeedSource
{
    public bool Enabled { get; set; } = false;
    public int Weight { get; set; } = 15;
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ImageOrientation
{
    Any,
    Landscape,
    Portrait
}

public sealed class FilterConfig
{
    public int MinWidth { get; set; } = 1280;
    public int MinHeight { get; set; } = 720;
    public int MaxWidth { get; set; } = 10000;
    public int MaxHeight { get; set; } = 10000;
    public ImageOrientation Orientation { get; set; } = ImageOrientation.Landscape;
    public double AspectRatioMin { get; set; } = 1.25;
    public double AspectRatioMax { get; set; } = 3.6;
    public bool DeduplicateSimilar { get; set; } = true;
}

public sealed class FetchingConfig
{
    public int LowWaterMark { get; set; } = 10;
    public int TargetPoolSize { get; set; } = 50;
    public int MaxPinsPerSync { get; set; } = 25;
    public int MaxCacheMb { get; set; } = 500;
    public int MinSyncCooldownMinutes { get; set; } = 30;
    public int MaxSyncsPerDay { get; set; } = 24;
    public int MaxScrollStepsPerSource { get; set; } = 6;
}
