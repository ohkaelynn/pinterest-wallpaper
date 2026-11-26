using PinterestWallpaper.Core.Config;

namespace PinterestWallpaper.Core.Rotation;

public interface IWallpaperApplier
{
    int GetMonitorCount();
    void ApplyWallpapers(IReadOnlyList<string> imagePaths, DisplayConfig displayConfig);
}
