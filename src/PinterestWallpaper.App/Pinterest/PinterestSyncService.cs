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
public sealed record PinActionResult(bool Success, bool FallbackToBrowser, string Message);

public sealed class PinterestSyncService : IDisposable
{
    private readonly AppPaths _paths;
    private readonly StateDatabase _db;
    private readonly RateLimitGovernor _governor;
    private readonly SemaphoreSlim _syncLock = new(1, 1);
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private DateTimeOffset _lastWriteActionUtc = DateTimeOffset.MinValue;

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
            return new SyncResult(false, 0, "A Pinterest sync or action is already running.");
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

    public Task<PinActionResult> TryLikePinAsync(PinRecord pin, CancellationToken ct = default) =>
        RunPinWriteActionAsync(pin, likeOnly: true, boardName: string.Empty, ct);

    public Task<PinActionResult> TrySavePinAsync(PinRecord pin, ActionsConfig actions, CancellationToken ct = default) =>
        RunPinWriteActionAsync(pin, likeOnly: false, boardName: actions.DefaultSaveBoardName?.Trim() ?? string.Empty, ct);

    private async Task<PinActionResult> RunPinWriteActionAsync(
        PinRecord pin,
        bool likeOnly,
        string boardName,
        CancellationToken ct)
    {
        if (!PinterestUrlGuard.IsAllowedPinterestPageUrl(pin.PinUrl))
        {
            return new PinActionResult(false, false, "Marked favorite locally (not a Pinterest pin).");
        }

        var backoffUntil = _governor.GetBackoffUntilUtc();
        if (backoffUntil is not null && DateTimeOffset.UtcNow < backoffUntil.Value)
        {
            return new PinActionResult(false, true, "Pinterest is in rate-limit backoff; opening pin in browser instead.");
        }

        if (!await _syncLock.WaitAsync(TimeSpan.FromSeconds(2), ct))
        {
            return new PinActionResult(false, false, "Pinterest is busy syncing; try again in a moment.");
        }

        try
        {
            var sinceLast = DateTimeOffset.UtcNow - _lastWriteActionUtc;
            if (sinceLast < TimeSpan.FromSeconds(5))
            {
                await Task.Delay(TimeSpan.FromSeconds(5) - sinceLast, ct);
            }

            var result = await ExecutePinActionInWebViewAsync(pin.PinUrl, likeOnly, boardName, ct);
            _lastWriteActionUtc = DateTimeOffset.UtcNow;
            return result;
        }
        finally
        {
            _syncLock.Release();
        }
    }

    public async Task<bool> TryClearSessionAsync(CancellationToken ct = default)
    {
        if (!await _syncLock.WaitAsync(TimeSpan.FromSeconds(3), ct))
        {
            return false;
        }

        try
        {
            if (Directory.Exists(_paths.WebView2Directory))
            {
                Directory.Delete(_paths.WebView2Directory, recursive: true);
            }

            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            _syncLock.Release();
        }
    }

    private Task<PinActionResult> ExecutePinActionInWebViewAsync(
        string pinUrl,
        bool likeOnly,
        string boardName,
        CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<PinActionResult>(TaskCreationOptions.RunContinuationsAsynchronously);

        Application.Current.Dispatcher.InvokeAsync(async () =>
        {
            Window? hostWindow = null;
            WebView2? webView = null;

            try
            {
                var rateLimited = false;
                var repinConfirmedByNetwork = false;

                webView = new WebView2();
                hostWindow = CreateHiddenHostWindow(webView);
                hostWindow.Show();

                var env = await CoreWebView2Environment.CreateAsync(userDataFolder: _paths.WebView2Directory);
                await webView.EnsureCoreWebView2Async(env);

                webView.CoreWebView2.WebResourceResponseReceived += (_, args) =>
                {
                    if (!PinterestUrlGuard.IsAllowedPinterestPageUrl(args.Request.Uri))
                    {
                        return;
                    }

                    if (args.Response.StatusCode is 429 or 403)
                    {
                        rateLimited = true;
                        return;
                    }

                    if (args.Response.StatusCode == 200 &&
                        (args.Request.Uri.Contains("RepinResource/create", StringComparison.OrdinalIgnoreCase) ||
                         args.Request.Uri.Contains("PinSave", StringComparison.OrdinalIgnoreCase)))
                    {
                        repinConfirmedByNetwork = true;
                    }
                };

                await NavigateAndWaitAsync(webView, pinUrl, ct);
                await Task.Delay(TimeSpan.FromMilliseconds(2200), ct);

                if (rateLimited)
                {
                    _governor.RecordRateLimitOrChallenge(DateTimeOffset.UtcNow, "rate_limited");
                    tcs.TrySetResult(new PinActionResult(false, true, "Pinterest rate-limited the action; backing off."));
                    return;
                }

                var currentUrl = webView.CoreWebView2.Source ?? string.Empty;
                if (currentUrl.Contains("/login", StringComparison.OrdinalIgnoreCase))
                {
                    tcs.TrySetResult(new PinActionResult(false, true, "Pinterest session expired; sign in again."));
                    return;
                }

                if (likeOnly)
                {
                    const string likeScript = """
                        (() => {
                            const btn = document.querySelector('[data-test-id="reactions-button"], button[aria-label*="React"], button[aria-label*="Like"]');
                            if (!btn) return 'missing';
                            const pressed = btn.getAttribute('aria-pressed');
                            if (pressed === 'true') return 'already';
                            btn.click();
                            return 'clicked';
                        })()
                        """;
                    var likeRaw = await webView.CoreWebView2.ExecuteScriptAsync(likeScript);
                    var likeStatus = JsonSerializer.Deserialize<string>(likeRaw);
                    await Task.Delay(TimeSpan.FromMilliseconds(1200), ct);

                    var msg = likeStatus switch
                    {
                        "clicked" => "Liked wallpaper on Pinterest.",
                        "already" => "Already liked on Pinterest.",
                        _ => "Could not locate Like button; opening pin in browser."
                    };
                    tcs.TrySetResult(new PinActionResult(likeStatus is "clicked" or "already", likeStatus == "missing", msg));
                    return;
                }

                // Save to Pinterest (either default My Saves or a specific board by name).
                var alreadySaved = false;
                var clickedSave = false;

                if (string.IsNullOrEmpty(boardName))
                {
                    const string saveDefaultScript = """
                        (() => {
                            const candidates = [
                                ...document.querySelectorAll('[data-test-id="PinBetterSaveButton"] button'),
                                ...document.querySelectorAll('[data-test-id="PinBetterSaveButton"]'),
                                ...document.querySelectorAll('button[aria-label="Save"]'),
                                ...document.querySelectorAll('button')
                            ];
                            const btn = candidates.find(el => {
                                const t = (el.innerText || el.getAttribute('aria-label') || '').trim().toLowerCase();
                                return t === 'save' || t === 'saved';
                            });
                            if (!btn) return 'missing';
                            const txt = (btn.innerText || '').trim().toLowerCase();
                            if (txt === 'saved') return 'already';
                            btn.click();
                            return 'clicked';
                        })()
                        """;
                    var saveRaw = await webView.CoreWebView2.ExecuteScriptAsync(saveDefaultScript);
                    var saveStatus = JsonSerializer.Deserialize<string>(saveRaw);
                    alreadySaved = saveStatus == "already";
                    clickedSave = saveStatus == "clicked";
                }
                else
                {
                    const string openDropdownScript = """
                        (() => {
                            const btn = document.querySelector('[data-test-id="board-dropdown-select-button"], button[aria-label*="Select a board"], button[aria-label*="board"]');
                            if (!btn) return false;
                            btn.click();
                            return true;
                        })()
                        """;
                    var opened = await webView.CoreWebView2.ExecuteScriptAsync(openDropdownScript);
                    if (opened == "true")
                    {
                        await Task.Delay(TimeSpan.FromMilliseconds(1000), ct);
                        var escapedBoard = JsonSerializer.Serialize(boardName);
                        var searchBoardScript = $$"""
                            (() => {
                                const input = document.querySelector('input[aria-label="Search"], input[placeholder="Search"]');
                                if (input) {
                                    const setter = Object.getOwnPropertyDescriptor(window.HTMLInputElement.prototype, 'value').set;
                                    setter.call(input, {{escapedBoard}});
                                    input.dispatchEvent(new Event('input', { bubbles: true }));
                                }
                                return true;
                            })()
                            """;
                        await webView.CoreWebView2.ExecuteScriptAsync(searchBoardScript);
                        await Task.Delay(TimeSpan.FromMilliseconds(1000), ct);

                        var clickRowScript = $$"""
                            (() => {
                                const target = {{escapedBoard}}.toLowerCase();
                                const rows = Array.from(document.querySelectorAll('[data-test-id^="board-row-"], [role="listitem"], [role="button"]'));
                                for (const row of rows) {
                                    const text = (row.innerText || '').trim().toLowerCase();
                                    if (text === target || text.startsWith(target + '\n') || text.includes('\n' + target)) {
                                        const innerSave = row.querySelector('button');
                                        (innerSave || row).click();
                                        return 'clicked';
                                    }
                                }
                                return 'missing';
                            })()
                            """;
                        var rowRaw = await webView.CoreWebView2.ExecuteScriptAsync(clickRowScript);
                        clickedSave = JsonSerializer.Deserialize<string>(rowRaw) == "clicked";
                    }
                }

                await Task.Delay(TimeSpan.FromMilliseconds(2000), ct);

                // Verify save state either via network RepinResource 200 or DOM showing "Saved".
                const string verifySavedScript = """
                    (() => {
                        const btns = Array.from(document.querySelectorAll('[data-test-id="PinBetterSaveButton"], [data-test-id="PinBetterSaveButton"] button, button'));
                        return btns.some(b => (b.innerText || '').trim().toLowerCase() === 'saved');
                    })()
                    """;
                var domSaysSaved = await webView.CoreWebView2.ExecuteScriptAsync(verifySavedScript) == "true";

                var dest = string.IsNullOrEmpty(boardName) ? "My Saves" : $"board '{boardName}'";
                if (alreadySaved)
                {
                    tcs.TrySetResult(new PinActionResult(true, false, $"Already saved on Pinterest ({dest})."));
                }
                else if (repinConfirmedByNetwork || (clickedSave && domSaysSaved))
                {
                    tcs.TrySetResult(new PinActionResult(true, false, $"Saved to Pinterest ({dest})."));
                }
                else
                {
                    tcs.TrySetResult(new PinActionResult(false, true, $"Could not confirm save to {dest}; opening pin in browser."));
                }
            }
            catch (Exception ex)
            {
                tcs.TrySetResult(new PinActionResult(false, true, $"Background action failed ({ex.Message}); opening in browser."));
            }
            finally
            {
                webView?.Dispose();
                hostWindow?.Close();
            }
        });

        return tcs.Task;
    }

    private static List<(string SourceType, string SourceValue, string Url)> BuildSourceTargets(SourcesConfig sources)
    {
        var list = new List<(string, string, string)>();

        foreach (var board in sources.Boards.Where(b => b.Enabled && !string.IsNullOrWhiteSpace(b.Url)))
        {
            var url = board.Url.Trim();
            if (PinterestUrlGuard.IsAllowedPinterestPageUrl(url))
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

    private static Window CreateHiddenHostWindow(WebView2 webView) => new()
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
                hostWindow = CreateHiddenHostWindow(webView);
                hostWindow.Show();

                var env = await CoreWebView2Environment.CreateAsync(userDataFolder: _paths.WebView2Directory);
                await webView.EnsureCoreWebView2Async(env);

                webView.CoreWebView2.WebResourceResponseReceived += async (_, args) =>
                {
                    try
                    {
                        var url = args.Request.Uri;
                        if (!PinterestUrlGuard.IsAllowedPinterestPageUrl(url))
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

                var maxScrolls = Math.Clamp(config.Fetching.MaxScrollStepsPerSource, 1, 8);

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

            if (!PinterestUrlGuard.IsAllowedPinterestImageUrl(pin.ImageUrl))
            {
                continue;
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

                if (response.Content.Headers.ContentLength is long contentLen &&
                    contentLen > PinterestUrlGuard.MaxImageDownloadBytes)
                {
                    continue;
                }

                var exceededLimit = false;
                await using (var remoteStream = await response.Content.ReadAsStreamAsync(ct))
                await using (var fs = File.Create(tempPath))
                {
                    var buffer = new byte[81920];
                    long totalRead = 0;
                    int read;
                    while ((read = await remoteStream.ReadAsync(buffer, ct)) > 0)
                    {
                        totalRead += read;
                        if (totalRead > PinterestUrlGuard.MaxImageDownloadBytes)
                        {
                            exceededLimit = true;
                            break;
                        }

                        await fs.WriteAsync(buffer.AsMemory(0, read), ct);
                    }
                }

                if (exceededLimit)
                {
                    File.Delete(tempPath);
                    continue;
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
