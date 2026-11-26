using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using PinterestWallpaper.Core.Config;
using PinterestWallpaper.Core.Media;
using PinterestWallpaper.Core.Pinterest;
using PinterestWallpaper.Core.Storage;
using Application = System.Windows.Application;

namespace PinterestWallpaper.App.Pinterest;

public sealed record SyncResult(bool Ran, int PinsAdded, string Message);

public sealed class PinterestSyncService : IDisposable
{
    private readonly AppPaths _paths;
    private readonly StateDatabase _db;
    private readonly RateLimitGovernor _governor;
    private readonly SemaphoreSlim _syncLock = new(1, 1);
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(25) };

    public PinterestSyncService(AppPaths paths, StateDatabase db, RateLimitGovernor governor)
    {
        _paths = paths;
        _db = db;
        _governor = governor;
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36 Edg/131.0.0.0");
    }

    public async Task<SyncResult> TrySyncAsync(AppConfig config, bool isManual, CancellationToken ct = default)
    {
        if (!await _syncLock.WaitAsync(0, ct))
        {
            return new SyncResult(false, 0, "A sync is already running.");
        }

        try
        {
            var now = DateTimeOffset.UtcNow;
            var decision = isManual
                ? _governor.CanSync(config.Fetching, now)
                : _governor.ShouldAutoSync(config.Fetching, now);

            if (!decision.Allowed)
            {
                return new SyncResult(false, 0, decision.Reason);
            }

            var targets = BuildSourceTargets(config.Sources);
            if (targets.Count == 0)
            {
                return new SyncResult(false, 0, "No enabled Pinterest boards, search terms, or home feed configured.");
            }

            var needed = Math.Max(1, config.Fetching.TargetPoolSize - _db.GetUnseenCount());
            var collectResult = await CollectCandidatesViaWebViewAsync(targets, config, needed, ct);

            if (collectResult.RateLimited)
            {
                var delay = _governor.RecordRateLimitOrChallenge(DateTimeOffset.UtcNow, "rate_limited");
                return new SyncResult(true, 0, $"Pinterest rate-limited or challenged the session. Backing off for {delay.TotalHours:F0}h.");
            }

            if (collectResult.LoginRequired)
            {
                _governor.RecordRateLimitOrChallenge(DateTimeOffset.UtcNow, "login_required");
                return new SyncResult(true, 0, "Pinterest session expired. Please click 'Sign in to Pinterest...' in the tray menu.");
            }

            var added = await DownloadAndFilterSequentiallyAsync(collectResult.Candidates, config, needed, ct);
            CacheManager.EnforceMaxCacheSize(_db, _paths.CacheDirectory, config.Fetching.MaxCacheMb);
            _governor.RecordSuccess(added, DateTimeOffset.UtcNow);

            return new SyncResult(true, added, $"Synced {added} new wallpaper(s) into local cache.");
        }
        finally
        {
            _syncLock.Release();
        }
    }

    private static List<(string SourceType, string SourceValue, string Url)> BuildSourceTargets(SourcesConfig sources)
    {
        var list = new List<(string, string, string)>();

        foreach (var board in sources.Boards.Where(b => b.Enabled && !string.IsNullOrWhiteSpace(b.Url)))
        {
            var url = board.Url.Trim();
            if (url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                list.Add(("board", url, url));
            }
        }

        foreach (var search in sources.Searches.Where(s => s.Enabled && !string.IsNullOrWhiteSpace(s.Query)))
        {
            var query = search.Query.Trim();
            var url = $"https://www.pinterest.com/search/pins/?q={Uri.EscapeDataString(query)}";
            list.Add(("search", query, url));
        }

        if (sources.HomeFeed.Enabled)
        {
            list.Add(("home_feed", "home", "https://www.pinterest.com/"));
        }

        return list;
    }

    private sealed record CollectResult(
        List<(string SourceType, string SourceValue, ExtractedPin Pin)> Candidates,
        bool RateLimited,
        bool LoginRequired);

    private Task<CollectResult> CollectCandidatesViaWebViewAsync(
        List<(string SourceType, string SourceValue, string Url)> targets,
        AppConfig config,
        int targetCandidateCount,
        CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<CollectResult>(TaskCreationOptions.RunContinuationsAsynchronously);

        Application.Current.Dispatcher.InvokeAsync(async () =>
        {
            Window? hostWindow = null;
            WebView2? webView = null;

            try
            {
                var candidates = new ConcurrentQueue<(string SourceType, string SourceValue, ExtractedPin Pin)>();
                var seenIds = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
                var rateLimited = false;
                var loginRequired = false;
                var currentSourceType = string.Empty;
                var currentSourceValue = string.Empty;

                webView = new WebView2();
                hostWindow = new Window
                {
                    Width = 1280,
                    Height = 900,
                    Left = -20000,
                    Top = -20000,
                    ShowInTaskbar = false,
                    ShowActivated = false,
                    WindowStyle = WindowStyle.None,
                    Opacity = 0,
                    Content = webView
                };

                hostWindow.Show();

                var env = await CoreWebView2Environment.CreateAsync(userDataFolder: _paths.WebView2Directory);
                await webView.EnsureCoreWebView2Async(env);

                webView.CoreWebView2.WebResourceResponseReceived += async (_, args) =>
                {
                    try
                    {
                        var url = args.Request.Uri;
                        if (!url.Contains("pinterest.com", StringComparison.OrdinalIgnoreCase))
                        {
                            return;
                        }

                        var status = args.Response.StatusCode;
                        if (status is 429 or 403)
                        {
                            rateLimited = true;
                            return;
                        }

                        if (!url.Contains("/resource/", StringComparison.OrdinalIgnoreCase) &&
                            !url.Contains("/graphql", StringComparison.OrdinalIgnoreCase))
                        {
                            return;
                        }

                        var contentType = args.Response.Headers.Contains("content-type")
                            ? args.Response.Headers.GetHeader("content-type")
                            : string.Empty;
                        if (!contentType.Contains("json", StringComparison.OrdinalIgnoreCase))
                        {
                            return;
                        }

                        using var stream = await args.Response.GetContentAsync();
                        using var reader = new StreamReader(stream);
                        var json = await reader.ReadToEndAsync();

                        foreach (var pin in PinterestPinExtractor.ExtractFromJson(json, config.Filters))
                        {
                            if (seenIds.TryAdd(pin.PinId, 0))
                            {
                                candidates.Enqueue((currentSourceType, currentSourceValue, pin));
                            }
                        }
                    }
                    catch
                    {
                        // Ignore unreadable responses (e.g., cancelled or empty bodies).
                    }
                };

                var maxScrolls = Math.Clamp(config.Fetching.MaxScrollStepsPerSource, 1, 5);

                foreach (var (sourceType, sourceValue, url) in targets)
                {
                    if (ct.IsCancellationRequested || rateLimited || loginRequired || candidates.Count >= targetCandidateCount * 2)
                    {
                        break;
                    }

                    currentSourceType = sourceType;
                    currentSourceValue = sourceValue;

                    await NavigateAndWaitAsync(webView, url, ct);
                    await Task.Delay(_governor.GetHumanStepDelay(), ct);

                    var currentUrl = webView.CoreWebView2.Source ?? string.Empty;
                    if (currentUrl.Contains("/login", StringComparison.OrdinalIgnoreCase))
                    {
                        loginRequired = true;
                        break;
                    }

                    // Extract any SSR-embedded initial JSON payload on the page.
                    await ExtractEmbeddedPageJsonAsync(webView, config.Filters, sourceType, sourceValue, seenIds, candidates);

                    for (var step = 0; step < maxScrolls; step++)
                    {
                        if (ct.IsCancellationRequested || rateLimited || candidates.Count >= targetCandidateCount * 2)
                        {
                            break;
                        }

                        await webView.CoreWebView2.ExecuteScriptAsync("window.scrollBy(0, 1100);");
                        await Task.Delay(_governor.GetHumanStepDelay(), ct);
                    }
                }

                tcs.TrySetResult(new CollectResult(candidates.ToList(), rateLimited, loginRequired));
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
            finally
            {
                webView?.Dispose();
                hostWindow?.Close();
            }
        });

        return tcs.Task;
    }

    private static async Task ExtractEmbeddedPageJsonAsync(
        WebView2 webView,
        FilterConfig filters,
        string sourceType,
        string sourceValue,
        ConcurrentDictionary<string, byte> seenIds,
        ConcurrentQueue<(string SourceType, string SourceValue, ExtractedPin Pin)> candidates)
    {
        try
        {
            const string script = """
                (() => {
                    const el = document.querySelector('#__PWS_INITIAL_PROPS__, #__PWS_DATA__');
                    return el ? el.textContent : '';
                })()
                """;
            var rawResult = await webView.CoreWebView2.ExecuteScriptAsync(script);
            if (string.IsNullOrWhiteSpace(rawResult) || rawResult == "null" || rawResult == "\"\"")
            {
                return;
            }

            var json = JsonSerializer.Deserialize<string>(rawResult);
            if (string.IsNullOrWhiteSpace(json))
            {
                return;
            }

            foreach (var pin in PinterestPinExtractor.ExtractFromJson(json, filters))
            {
                if (seenIds.TryAdd(pin.PinId, 0))
                {
                    candidates.Enqueue((sourceType, sourceValue, pin));
                }
            }
        }
        catch
        {
            // Embedded JSON is optional; XHR interception handles the rest.
        }
    }

    private static Task NavigateAndWaitAsync(WebView2 webView, string url, CancellationToken ct)
    {
        var navTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        void Handler(object? sender, CoreWebView2NavigationCompletedEventArgs args)
        {
            webView.CoreWebView2.NavigationCompleted -= Handler;
            navTcs.TrySetResult();
        }

        webView.CoreWebView2.NavigationCompleted += Handler;
        using var reg = ct.Register(() =>
        {
            webView.CoreWebView2.NavigationCompleted -= Handler;
            navTcs.TrySetCanceled(ct);
        });

        webView.CoreWebView2.Navigate(url);
        return Task.WhenAny(navTcs.Task, Task.Delay(TimeSpan.FromSeconds(20), ct));
    }

    private async Task<int> DownloadAndFilterSequentiallyAsync(
        List<(string SourceType, string SourceValue, ExtractedPin Pin)> candidates,
        AppConfig config,
        int maxToAdd,
        CancellationToken ct)
    {
        Directory.CreateDirectory(_paths.CacheDirectory);
        var existingHashes = _db.GetAllPerceptualHashes().ToList();
        var added = 0;

        foreach (var (sourceType, sourceValue, pin) in candidates)
        {
            if (ct.IsCancellationRequested || added >= maxToAdd)
            {
                break;
            }

            if (_db.IsBlocked(pin.PinId) || _db.GetPinById(pin.PinId) is not null)
            {
                continue;
            }

            var ext = Path.GetExtension(new Uri(pin.ImageUrl).AbsolutePath);
            if (string.IsNullOrWhiteSpace(ext) || !ImageFilter.HasSupportedExtension("x" + ext))
            {
                ext = ".jpg";
            }

            var tempPath = Path.Combine(_paths.CacheDirectory, $"tmp_{pin.PinId}{ext.ToLowerInvariant()}");
            var finalPath = Path.Combine(_paths.CacheDirectory, $"{pin.PinId}{ext.ToLowerInvariant()}");

            try
            {
                using var response = await _http.GetAsync(pin.ImageUrl, HttpCompletionOption.ResponseHeadersRead, ct);
                if (!response.IsSuccessStatusCode)
                {
                    continue;
                }

                await using (var fs = File.Create(tempPath))
                {
                    await response.Content.CopyToAsync(fs, ct);
                }

                var check = ImageFilter.ValidateFile(tempPath, config.Filters, existingHashes);
                if (!check.IsValid)
                {
                    File.Delete(tempPath);
                    continue;
                }

                File.Move(tempPath, finalPath, overwrite: true);
                _db.UpsertPin(new PinRecord(
                    PinId: pin.PinId,
                    SourceType: sourceType,
                    SourceValue: sourceValue,
                    PinUrl: pin.PinUrl,
                    LocalPath: finalPath,
                    Width: check.Width,
                    Height: check.Height,
                    PerceptualHash: check.PerceptualHash,
                    AddedAtUtc: DateTimeOffset.UtcNow,
                    LastShownAtUtc: null,
                    IsFavorite: false));

                existingHashes.Add(check.PerceptualHash);
                added++;

                // Sequential human-paced download delay.
                await Task.Delay(_governor.GetImageDownloadDelay(), ct);
            }
            catch
            {
                if (File.Exists(tempPath))
                {
                    try { File.Delete(tempPath); } catch { }
                }
            }
        }

        return added;
    }

    public void Dispose()
    {
        _http.Dispose();
        _syncLock.Dispose();
    }
}
