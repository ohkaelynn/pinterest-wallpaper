using System.IO.Pipes;
using System.Text;
using PinterestWallpaper.Core.Rotation;

namespace PinterestWallpaper.Core.Ipc;

public sealed class NamedPipeCommandServer : IDisposable
{
    public const string DefaultPipeName = "PinterestWallpaper_IPC_v1";

    private readonly string _pipeName;
    private readonly Action<AppCommand> _onCommand;
    private readonly CancellationTokenSource _cts = new();
    private Task? _listenTask;

    public NamedPipeCommandServer(Action<AppCommand> onCommand, string pipeName = DefaultPipeName)
    {
        _onCommand = onCommand;
        _pipeName = pipeName;
    }

    public void Start()
    {
        _listenTask = Task.Run(() => ListenLoopAsync(_cts.Token));
    }

    private async Task ListenLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(
                    _pipeName,
                    PipeDirection.In,
                    maxNumberOfServerInstances: 1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

                await server.WaitForConnectionAsync(ct).ConfigureAwait(false);

                using var reader = new StreamReader(server, Encoding.UTF8, leaveOpen: true);
                var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                var command = CommandParser.Parse(line);
                if (command != AppCommand.None)
                {
                    _onCommand(command);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                // Brief pause before reopening pipe if a client disconnects abruptly.
                try { await Task.Delay(100, ct).ConfigureAwait(false); } catch { break; }
            }
        }
    }

    public static bool TrySendCommand(AppCommand command, string pipeName = DefaultPipeName, int timeoutMs = 1500)
    {
        var arg = CommandParser.ToArgument(command);
        if (string.IsNullOrEmpty(arg))
        {
            return false;
        }

        try
        {
            using var client = new NamedPipeClientStream(
                ".",
                pipeName,
                PipeDirection.Out,
                PipeOptions.CurrentUserOnly);

            client.Connect(timeoutMs);

            using var writer = new StreamWriter(client, new UTF8Encoding(false), leaveOpen: true)
            {
                AutoFlush = true
            };
            writer.WriteLine(arg);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _listenTask?.Wait(500); } catch { }
        _cts.Dispose();
    }
}
