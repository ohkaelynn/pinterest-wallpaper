using PinterestWallpaper.Core.Ipc;
using PinterestWallpaper.Core.Rotation;
using Xunit;

namespace PinterestWallpaper.Tests;

public sealed class CommandAndIpcTests
{
    [Theory]
    [InlineData("--next", AppCommand.Next)]
    [InlineData("--prev", AppCommand.Previous)]
    [InlineData("--like-pin", AppCommand.LikeOnPinterest)]
    [InlineData("--save-pin", AppCommand.SaveToPinterest)]
    [InlineData("--open-pin", AppCommand.OpenPin)]
    [InlineData("--skip", AppCommand.SkipAndHide)]
    [InlineData("--toggle-pause", AppCommand.TogglePause)]
    [InlineData("--unknown", AppCommand.None)]
    public void CommandParser_ParsesAndRoundTrips(string arg, AppCommand expected)
    {
        Assert.Equal(expected, CommandParser.Parse([arg]));
        if (expected != AppCommand.None)
        {
            Assert.Equal(arg, CommandParser.ToArgument(expected));
        }
    }

    [Fact]
    public async Task NamedPipeIpc_DeliversCommandToServer()
    {
        var pipeName = "PinterestWallpaper_Test_" + Guid.NewGuid().ToString("N");
        var received = new TaskCompletionSource<AppCommand>(TaskCreationOptions.RunContinuationsAsynchronously);

        using var server = new NamedPipeCommandServer(cmd => received.TrySetResult(cmd), pipeName);
        server.Start();

        // Give the background listener a moment to open the pipe.
        await Task.Delay(50);

        Assert.True(NamedPipeCommandServer.TrySendCommand(AppCommand.SaveToPinterest, pipeName, timeoutMs: 2000));

        var completed = await Task.WhenAny(received.Task, Task.Delay(2000));
        Assert.Same(received.Task, completed);
        Assert.Equal(AppCommand.SaveToPinterest, await received.Task);
    }
}
