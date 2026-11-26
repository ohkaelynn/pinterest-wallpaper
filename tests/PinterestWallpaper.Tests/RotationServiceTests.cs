using PinterestWallpaper.Core.Config;
using PinterestWallpaper.Core.Rotation;
using PinterestWallpaper.Core.Storage;
using Xunit;

namespace PinterestWallpaper.Tests;

public sealed class RotationServiceTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "pinwall-rot-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void NextPreviousAndSkipBlock_WorkOfflineFromCache()
    {
        Directory.CreateDirectory(_tempDir);
        var f1 = Path.Combine(_tempDir, "1.jpg");
        var f2 = Path.Combine(_tempDir, "2.jpg");
        File.WriteAllBytes(f1, [1, 2, 3]);
        File.WriteAllBytes(f2, [4, 5, 6]);

        using var db = new StateDatabase(Path.Combine(_tempDir, "state.db"));
        db.UpsertPin(new PinRecord("p1", "local", "dir", "", f1, 1920, 1080, 1UL, DateTimeOffset.UtcNow, null, false));
        db.UpsertPin(new PinRecord("p2", "local", "dir", "", f2, 1920, 1080, 2UL, DateTimeOffset.UtcNow, null, false));

        var applier = new FakeApplier(monitorCount: 1);
        var state = new FakeSystemState();
        var service = new RotationService(db, applier, state, new Random(42));
        var config = new AppConfig();

        Assert.True(service.NextWallpaper(config, isManual: true));
        var first = applier.LastApplied.Single();

        Assert.True(service.NextWallpaper(config, isManual: true));
        var second = applier.LastApplied.Single();
        Assert.NotEqual(first, second);

        Assert.True(service.PreviousWallpaper(config));
        Assert.Equal(first, applier.LastApplied.Single());

        // Scheduled rotation pauses when fullscreen or battery is active
        state.Fullscreen = true;
        Assert.False(service.NextWallpaper(config, isManual: false));

        // Pinning current wallpaper blocks scheduled rotation until unpinned or manually swapped
        state.Fullscreen = false;
        Assert.True(service.TogglePinCurrent());
        Assert.True(service.IsCurrentPinned);
        Assert.False(service.NextWallpaper(config, isManual: false));

        Assert.True(service.NextWallpaper(config, isManual: true));
        Assert.False(service.IsCurrentPinned);

        // Skip & block removes current pin and advances
        var blockedId = service.CurrentPins.Single().PinId;
        Assert.True(service.SkipAndBlockCurrent(config));
        Assert.True(db.IsBlocked(blockedId));
    }

    private sealed class FakeApplier(int monitorCount) : IWallpaperApplier
    {
        public IReadOnlyList<string> LastApplied { get; private set; } = [];
        public int GetMonitorCount() => monitorCount;
        public void ApplyWallpapers(IReadOnlyList<string> imagePaths, DisplayConfig displayConfig) => LastApplied = imagePaths;
    }

    private sealed class FakeSystemState : ISystemStateProvider
    {
        public bool Battery { get; set; }
        public bool Fullscreen { get; set; }
        public bool IsOnBattery() => Battery;
        public bool IsFullscreenAppActive() => Fullscreen;
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
