using PinterestWallpaper.Core.Diagnostics;
using Xunit;

namespace PinterestWallpaper.Tests;

public sealed class ActionLoggerTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "pinwall-log-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Info_CreatesDirectoryAndWritesTimestampedEntries()
    {
        var logFile = Path.Combine(_tempDir, "logs", "actions.log");
        var logger = new ActionLogger(logFile);

        logger.Info("like", "clickStatus=clicked");
        logger.Info("save", "done net=True dom=True");

        var text = File.ReadAllText(logFile);
        Assert.Contains("[like] clickStatus=clicked", text);
        Assert.Contains("[save] done net=True dom=True", text);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }
}
