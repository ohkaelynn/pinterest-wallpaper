using System.Diagnostics;
using System.Windows;
using Microsoft.Web.WebView2.Wpf;
using PinterestWallpaper.App.Windows;

namespace PinterestWallpaper.App.Pinterest;

public static class WebView2Cleanup
{
    /// <summary>
    /// Cleanly disposes a WebView2 host window and ensures its private msedgewebview2.exe
    /// browser process tree exits (preventing Pinterest's sw.js service worker from keeping
    /// ~300 MB of WebView2 processes alive while the tray app is idle).
    /// </summary>
    public static void DisposeAndRelease(WebView2? webView, Window? hostWindow = null)
    {
        uint browserPid = 0;
        try
        {
            browserPid = webView?.CoreWebView2?.BrowserProcessId ?? 0;
        }
        catch
        {
            // Ignore if CoreWebView2 was never initialized.
        }

        try { webView?.Dispose(); } catch { }
        try { hostWindow?.Close(); } catch { }

        if (browserPid == 0)
        {
            MemoryTrimmer.TrimIdleMemory();
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                // Give Chromium a moment to flush cookies/session state to disk and exit naturally.
                using var proc = Process.GetProcessById((int)browserPid);
                for (var i = 0; i < 15 && !proc.HasExited; i++)
                {
                    await Task.Delay(150);
                }

                if (!proc.HasExited &&
                    proc.ProcessName.Equals("msedgewebview2", StringComparison.OrdinalIgnoreCase))
                {
                    proc.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                // Already exited.
            }
            finally
            {
                MemoryTrimmer.TrimIdleMemory();
            }
        });
    }
}
