using PinterestWallpaper.Core.Config;
using Xunit;

namespace PinterestWallpaper.Tests;

public sealed class ConfigStoreTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "pinwall-tests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void LoadOrCreate_CreatesDefaultsAndClampsUnsafeValues()
    {
        var path = Path.Combine(_tempDir, "config.json");
        var store = new ConfigStore(path);

        var created = store.LoadOrCreate();
        Assert.True(File.Exists(path));
        Assert.Equal(15, created.Rotation.IntervalMinutes);
        Assert.Equal(30, created.Fetching.MinSyncCooldownMinutes);

        created.Fetching.MinSyncCooldownMinutes = 1; // Attempt to set aggressively low cooldown
        created.Fetching.MaxSyncsPerDay = 99;
        store.Save(created);

        var reloaded = store.LoadOrCreate();
        Assert.Equal(5, reloaded.Fetching.MinSyncCooldownMinutes);
        Assert.Equal(48, reloaded.Fetching.MaxSyncsPerDay);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }
}
