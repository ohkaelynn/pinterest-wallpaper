using PinterestWallpaper.Core.Config;
using PinterestWallpaper.Core.Media;
using PinterestWallpaper.Core.Pinterest;
using PinterestWallpaper.Core.Storage;
using Xunit;

namespace PinterestWallpaper.Tests;

public sealed class PinterestPinExtractorTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "pinwall-ext-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void ExtractFromJson_FindsOrigHighResPinsAndSkipsLowResAndVideos()
    {
        const string samplePayload = """
        {
          "resource_response": {
            "data": [
              {
                "id": "1234567890",
                "images": {
                  "236x": { "width": 236, "height": 133, "url": "https://i.pinimg.com/236x/a/b/c.jpg" },
                  "orig": { "width": 3840, "height": 2160, "url": "https://i.pinimg.com/originals/a/b/c.jpg" }
                }
              },
              {
                "id": "9999999999",
                "images": {
                  "orig": { "width": 800, "height": 600, "url": "https://i.pinimg.com/originals/x/y/small.jpg" }
                }
              },
              {
                "id": "5555555555",
                "videos": { "video_list": {} },
                "images": {
                  "orig": { "width": 1920, "height": 1080, "url": "https://i.pinimg.com/originals/v/i/vid.jpg" }
                }
              }
            ]
          }
        }
        """;

        var filter = new FilterConfig { MinWidth = 1920, MinHeight = 1080 };
        var pins = PinterestPinExtractor.ExtractFromJson(samplePayload, filter);

        var pin = Assert.Single(pins);
        Assert.Equal("1234567890", pin.PinId);
        Assert.Equal("https://www.pinterest.com/pin/1234567890/", pin.PinUrl);
        Assert.Equal("https://i.pinimg.com/originals/a/b/c.jpg", pin.ImageUrl);
        Assert.Equal(3840, pin.Width);
        Assert.Equal(2160, pin.Height);
    }

    [Fact]
    public void CacheManager_EvictsOldestShownNonFavoritesWhenOverLimit()
    {
        Directory.CreateDirectory(_tempDir);
        var f1 = Path.Combine(_tempDir, "old.jpg");
        var f2 = Path.Combine(_tempDir, "fav.jpg");
        File.WriteAllBytes(f1, new byte[800_000]);
        File.WriteAllBytes(f2, new byte[800_000]);

        using var db = new StateDatabase(Path.Combine(_tempDir, "state.db"));
        var t0 = DateTimeOffset.UtcNow.AddHours(-5);
        db.UpsertPin(new PinRecord("p1", "board", "b", "", f1, 1920, 1080, 1UL, t0, t0.AddHours(1), IsFavorite: false));
        db.UpsertPin(new PinRecord("p2", "board", "b", "", f2, 1920, 1080, 2UL, t0, t0.AddMinutes(10), IsFavorite: true));

        var evicted = CacheManager.EnforceMaxCacheSize(db, _tempDir, maxCacheMb: 1);

        Assert.Equal(1, evicted);
        Assert.False(File.Exists(f1));
        Assert.True(File.Exists(f2));
        Assert.Null(db.GetPinById("p1"));
        Assert.NotNull(db.GetPinById("p2"));
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }
}
