namespace PinterestWallpaper.Core.Diagnostics;

/// <summary>
/// Lightweight local diagnostic logger for sync/like/save steps.
/// Never logs cookies, auth headers, passwords, or personal board URLs.
/// </summary>
public sealed class ActionLogger
{
    private const long MaxLogBytes = 256 * 1024; // 256 KB cap
    private readonly string _logPath;
    private readonly object _gate = new();

    public ActionLogger(string logPath)
    {
        _logPath = logPath;
    }

    public void Info(string category, string message)
    {
        try
        {
            lock (_gate)
            {
                var dir = Path.GetDirectoryName(_logPath);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                if (File.Exists(_logPath) && new FileInfo(_logPath).Length > MaxLogBytes)
                {
                    var tail = File.ReadLines(_logPath).TakeLast(150).ToList();
                    File.WriteAllLines(_logPath, tail);
                }

                var line = $"[{DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm:ss}Z] [{category}] {message}{Environment.NewLine}";
                File.AppendAllText(_logPath, line);
            }
        }
        catch
        {
            // Logging must never crash the app.
        }
    }
}
