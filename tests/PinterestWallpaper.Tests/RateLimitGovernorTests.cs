using PinterestWallpaper.Core.Config;
using PinterestWallpaper.Core.Pinterest;
using PinterestWallpaper.Core.Storage;
using Xunit;

namespace PinterestWallpaper.Tests;

public sealed class RateLimitGovernorTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "pinwall-gov-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void EnforcesCooldownDailyCapAndExponentialBackoff()
    {
        var dbPath = Path.Combine(_tempDir, "state.db");
        using var db = new StateDatabase(dbPath);
        var gov = new RateLimitGovernor(db, new Random(1));
        var cfg = new FetchingConfig
        {
            MinSyncCooldownMinutes = 240,
            MaxSyncsPerDay = 2,
            LowWaterMark = 5
        };

        var t0 = new DateTimeOffset(2026, 11, 26, 12, 0, 0, TimeSpan.Zero);

        // Initial sync allowed when pool is empty
        Assert.True(gov.ShouldAutoSync(cfg, t0).Allowed);

        // After a sync, cooldown blocks for 4 hours
        gov.RecordSuccess(10, t0);
        Assert.False(gov.CanSync(cfg, t0.AddHours(2)).Allowed);
        Assert.True(gov.CanSync(cfg, t0.AddHours(4)).Allowed);

        // Second sync reaches MaxSyncsPerDay = 2
        gov.RecordSuccess(5, t0.AddHours(4));
        var dailyBlocked = gov.CanSync(cfg, t0.AddHours(9));
        Assert.False(dailyBlocked.Allowed);
        Assert.Contains("Daily sync budget", dailyBlocked.Reason);

        // Next day: simulate rate limit -> 1h backoff, then 4h backoff
        var day2 = t0.AddHours(29);
        var b1 = gov.RecordRateLimitOrChallenge(day2);
        Assert.Equal(TimeSpan.FromHours(1), b1);
        Assert.False(gov.CanSync(cfg, day2.AddMinutes(30)).Allowed);

        var b2 = gov.RecordRateLimitOrChallenge(day2.AddHours(1));
        Assert.Equal(TimeSpan.FromHours(4), b2);
        Assert.Equal(2, gov.GetConsecutiveFailures());
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
