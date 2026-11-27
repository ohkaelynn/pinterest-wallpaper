using System.Diagnostics;
using System.Windows;
using Microsoft.Web.WebView2.Wpf;
using PinterestWallpaper.App.Windows;

namespace PinterestWallpaper.App.Pinterest;

public static class WebView2Cleanup
{
    /// <summary>
    /// Fire-and-forget cleanup for standalone windows (e.g. Login window).
    /// </summary>
    public static void DisposeAndRelease(WebView2? webView, Window? hostWindow = null)
    {
        _ = DisposeAndReleaseAsync(webView, hostWindow);
    }

    /// <summary>
    /// Disposes a WebView2 host window and waits for its private msedgewebview2.exe
    /// browser process tree to exit before returning, preventing race conditions with
    /// back-to-back sync/like/save operations and stopping Pinterest's sw.js from lingering.
    /// </summary>
    public static async Task DisposeAndReleaseAsync(WebView2? webView, Window? hostWindow = null)
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

        await Task.Run(async () =>
        {
            try
            {
                // Give Chromium a moment to flush cookies/session state to disk and exit naturally.
                using var proc = Process.GetProcessById((int)browserPid);
                for (var i = 0; i < 12 && !proc.HasExited; i++)
                {
                    await Task.Delay(150);
                }

                if (!proc.HasExited &&
                    proc.ProcessName.Equals("msedgewebview2", StringComparison.OrdinalIgnoreCase))
                {
                    proc.Kill(entireProcessTree: true);
                    proc.WaitForExit(1000);
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
        }).ConfigureAwait(false);
    }
}
