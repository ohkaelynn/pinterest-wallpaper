using PinterestWallpaper.Core.Storage;

namespace PinterestWallpaper.Core.Media;

public static class CacheManager
{
    public static int EnforceMaxCacheSize(StateDatabase db, string cacheDirectory, int maxCacheMb)
    {
        if (!Directory.Exists(cacheDirectory))
        {
            return 0;
        }

        var maxBytes = Math.Max(1L, maxCacheMb) * 1024L * 1024L;
        var currentBytes = GetDirectorySizeBytes(cacheDirectory);
        if (currentBytes <= maxBytes)
        {
            return 0;
        }

        var evicted = 0;
        foreach (var pin in db.GetEvictablePins())
        {
            if (currentBytes <= maxBytes)
            {
                break;
            }

            try
            {
                if (File.Exists(pin.LocalPath))
                {
                    var len = new FileInfo(pin.LocalPath).Length;
                    File.Delete(pin.LocalPath);
                    currentBytes -= len;
                }

                db.RemovePin(pin.PinId);
                evicted++;
            }
            catch
            {
                // Skip locked files and continue.
            }
        }

        return evicted;
    }

    public static int ClearCache(StateDatabase db, string cacheDirectory)
    {
        var deleted = 0;
        if (Directory.Exists(cacheDirectory))
        {
            foreach (var file in Directory.EnumerateFiles(cacheDirectory))
            {
                try
                {
                    File.Delete(file);
                    deleted++;
                }
                catch
                {
                    // Skip locked files.
                }
            }
        }

        db.ClearAllPins();
        return deleted;
    }

    private static long GetDirectorySizeBytes(string directory)
    {
        long total = 0;
        foreach (var file in Directory.EnumerateFiles(directory))
        {
            try
            {
                total += new FileInfo(file).Length;
            }
            catch
            {
                // Ignore transient files.
            }
        }

        return total;
    }
}
