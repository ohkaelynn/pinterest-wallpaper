using PinterestWallpaper.Core.Storage;
using Xunit;

namespace PinterestWallpaper.Tests;

public sealed class StateDatabaseTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "pinwall-db-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void UpsertRecordShownAndBlock_UpdatesStateCorrectly()
    {
        var dbPath = Path.Combine(_tempDir, "state.db");
        using var db = new StateDatabase(dbPath);

        var pin1 = new PinRecord("pin1", "board", "b1", "https://pinterest.com/pin/1", "a.jpg", 1920, 1080, 123UL, DateTimeOffset.UtcNow, null, false);
        var pin2 = new PinRecord("pin2", "search", "s1", "https://pinterest.com/pin/2", "b.jpg", 2560, 1440, 456UL, DateTimeOffset.UtcNow, null, false);

        db.UpsertPin(pin1);
        db.UpsertPin(pin2);
        Assert.Equal(2, db.GetUnseenCount());

        db.RecordShown("pin1", DateTimeOffset.UtcNow);
        Assert.Equal(1, db.GetUnseenCount());

        db.BlockPin("pin2");
        Assert.True(db.IsBlocked("pin2"));
        Assert.Single(db.GetAvailablePins());
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
