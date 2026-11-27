using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using PinterestWallpaper.Core.Config;
using PinterestWallpaper.Core.Diagnostics;
using PinterestWallpaper.Core.Media;
using PinterestWallpaper.Core.Pinterest;
using PinterestWallpaper.Core.Storage;
using Application = System.Windows.Application;

namespace PinterestWallpaper.App.Pinterest;

public sealed record SyncResult(bool Ran, int PinsAdded, string Message);
public sealed record PinActionResult(bool Success, bool FallbackToBrowser, string Message);

public sealed class PinterestSyncService : IDisposable
{
    public const string SessionStatusKey = "pinterest_session_status";
    public const string CachedBoardsKey = "user_boards_json";

    private readonly AppPaths _paths;
    private readonly StateDatabase _db;
    private readonly RateLimitGovernor _governor;
    private readonly ActionLogger _log;
    private readonly SemaphoreSlim _syncLock = new(1, 1);
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private DateTimeOffset _lastWriteActionUtc = DateTimeOffset.MinValue;

    public PinterestSyncService(AppPaths paths, StateDatabase db, RateLimitGovernor governor)
    {
        _paths = paths;
        _db = db;
        _governor = governor;
        _log = new ActionLogger(paths.ActionLogFile);
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
                ? _governor.CanSync(config.Fetching, now, isManual: true)
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

            var poolGap = Math.Max(1, config.Fetching.TargetPoolSize - _db.GetUnseenCount());
            var batchLimit = Math.Clamp(config.Fetching.MaxPinsPerSync, 1, 100);
            var needed = isManual ? batchLimit : Math.Min(poolGap, batchLimit);
            var collectResult = await CollectCandidatesViaWebViewAsync(targets, config, needed, ct);

            if (collectResult.RateLimited)
            {
                var delay = _governor.RecordRateLimitOrChallenge(DateTimeOffset.UtcNow, "rate_limited");
                return new SyncResult(true, 0, $"Pinterest rate-limited or challenged the session. Backing off for {delay.TotalHours:F0}h.");
            }

            if (collectResult.LoginRequired)
            {
                _db.SetStateValue(SessionStatusKey, "expired");
                _governor.RecordRateLimitOrChallenge(DateTimeOffset.UtcNow, "login_required");
                return new SyncResult(true, 0, "Pinterest session expired. Please sign in again.");
            }

            _db.SetStateValue(SessionStatusKey, "signed_in");
            var stats = await DownloadAndFilterSequentiallyAsync(collectResult.Candidates, config, needed, ct);
            CacheManager.EnforceMaxCacheSize(_db, _paths.CacheDirectory, config.Fetching.MaxCacheMb);
            _governor.RecordSuccess(stats.Added, DateTimeOffset.UtcNow);

            var skippedParts = new List<string>();
            if (stats.TooSmall > 0) skippedParts.Add($"{stats.TooSmall} too small");
            if (stats.WrongAspectOrOrientation > 0) skippedParts.Add($"{stats.WrongAspectOrOrientation} aspect/orient");
            if (stats.Duplicates > 0) skippedParts.Add($"{stats.Duplicates} dupes");

            var msg = skippedParts.Count > 0
                ? $"Synced {stats.Added} wallpaper(s) ({string.Join(", ", skippedParts)})."
                : $"Synced {stats.Added} new wallpaper(s) into local cache.";
            return new SyncResult(true, stats.Added, msg);
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

            _db.DeleteStateValue(SessionStatusKey);
            _db.DeleteStateValue(CachedBoardsKey);
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

    public IReadOnlyList<UserBoard> GetCachedUserBoards()
    {
        var raw = _db.GetStateValue(CachedBoardsKey);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<UserBoard>>(raw) ?? [];
        }
        catch
        {
            return [];
        }
    }

    public async Task<(bool Success, IReadOnlyList<UserBoard> Boards, string Message)> TryFetchUserBoardsAsync(CancellationToken ct = default)
    {
        if (!await _syncLock.WaitAsync(TimeSpan.FromSeconds(2), ct))
        {
            return (false, GetCachedUserBoards(), "Pinterest is busy syncing; try again in a moment.");
        }

        try
        {
            var result = await FetchUserBoardsViaWebViewAsync(ct);
            if (result.LoginRequired)
            {
                _db.SetStateValue(SessionStatusKey, "expired");
                return (false, GetCachedUserBoards(), "Session expired. Please sign in to Pinterest first.");
            }

            if (result.Boards.Count > 0)
            {
                _db.SetStateValue(SessionStatusKey, "signed_in");
                _db.SetStateValue(CachedBoardsKey, JsonSerializer.Serialize(result.Boards));
                return (true, result.Boards, $"Loaded {result.Boards.Count} board(s) from your Pinterest account.");
            }

            return (false, GetCachedUserBoards(), "No boards found yet. Make sure you are signed in to Pinterest.");
        }
        finally
        {
            _syncLock.Release();
        }
    }

    private Task<(List<UserBoard> Boards, bool LoginRequired)> FetchUserBoardsViaWebViewAsync(CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<(List<UserBoard>, bool)>(TaskCreationOptions.RunContinuationsAsynchronously);

        Application.Current.Dispatcher.InvokeAsync(async () =>
        {
            Window? hostWindow = null;
            WebView2? webView = null;

            try
            {
                var found = new ConcurrentDictionary<string, UserBoard>(StringComparer.OrdinalIgnoreCase);
                var loginRequired = false;

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

                        foreach (var b in PinterestBoardExtractor.ExtractFromJson(json))
                        {
                            found.TryAdd(b.Url, b);
                        }
                    }
                    catch
                    {
                        // Ignore unreadable responses.
                    }
                };

                await NavigateAndWaitAsync(webView, "https://www.pinterest.com/me/_saved/", ct);
                await Task.Delay(TimeSpan.FromMilliseconds(2200), ct);

                var currentUrl = webView.CoreWebView2.Source ?? string.Empty;
                if (currentUrl.Contains("/login", StringComparison.OrdinalIgnoreCase))
                {
                    loginRequired = true;
                }
                else
                {
                    // Also check embedded SSR JSON and scroll once to trigger board list XHR if needed.
                    const string ssrScript = """
                        (() => {
                            const el = document.querySelector('#__PWS_INITIAL_PROPS__, #__PWS_DATA__');
                            return el ? el.textContent : '';
                        })()
                        """;
                    var raw = await webView.CoreWebView2.ExecuteScriptAsync(ssrScript);
                    var json = string.IsNullOrWhiteSpace(raw) || raw == "null" ? null : JsonSerializer.Deserialize<string>(raw);
                    if (!string.IsNullOrWhiteSpace(json))
                    {
                        foreach (var b in PinterestBoardExtractor.ExtractFromJson(json))
                        {
                            found.TryAdd(b.Url, b);
                        }
                    }

                    await webView.CoreWebView2.ExecuteScriptAsync("window.scrollBy(0, 700);");
                    await Task.Delay(TimeSpan.FromMilliseconds(1500), ct);
                }

                var ordered = found.Values.OrderBy(b => b.Name, StringComparer.OrdinalIgnoreCase).ToList();
                tcs.TrySetResult((ordered, loginRequired));
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
            finally
            {
                await WebView2Cleanup.DisposeAndReleaseAsync(webView, hostWindow);
            }
        });

        return tcs.Task;
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
            var actionTag = likeOnly ? "like" : "save";

            try
            {
                var rateLimited = false;
                var likeClickStarted = false;
                var likeConfirmedByNetwork = false;
                var saveClickStarted = false;
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

                    if (args.Response.StatusCode == 429)
                    {
                        rateLimited = true;
                        _log.Info(actionTag, "HTTP 429 received.");
                        return;
                    }

                    if (args.Response.StatusCode == 403 &&
                        (args.Request.Uri.Contains("/resource/", StringComparison.OrdinalIgnoreCase) ||
                         args.Request.Uri.Contains("/graphql", StringComparison.OrdinalIgnoreCase)))
                    {
                        rateLimited = true;
                        _log.Info(actionTag, "HTTP 403 on API endpoint.");
                        return;
                    }

                    if (args.Response.StatusCode is >= 200 and < 300 &&
                        string.Equals(args.Request.Method, "POST", StringComparison.OrdinalIgnoreCase))
                    {
                        var uri = args.Request.Uri;
                        if (likeClickStarted &&
                            (uri.Contains("React", StringComparison.OrdinalIgnoreCase) ||
                             uri.Contains("Like", StringComparison.OrdinalIgnoreCase) ||
                             uri.Contains("/graphql", StringComparison.OrdinalIgnoreCase)))
                        {
                            likeConfirmedByNetwork = true;
                            _log.Info(actionTag, $"Confirmed by POST {args.Response.StatusCode}.");
                        }

                        if (saveClickStarted &&
                            (uri.Contains("RepinResource", StringComparison.OrdinalIgnoreCase) ||
                             uri.Contains("PinSave", StringComparison.OrdinalIgnoreCase) ||
                             uri.Contains("SavePin", StringComparison.OrdinalIgnoreCase) ||
                             uri.Contains("BoardPin", StringComparison.OrdinalIgnoreCase) ||
                             uri.Contains("/graphql", StringComparison.OrdinalIgnoreCase)))
                        {
                            repinConfirmedByNetwork = true;
                            _log.Info(actionTag, $"Confirmed by POST {args.Response.StatusCode}.");
                        }
                    }
                };

                await NavigateAndWaitAsync(webView, pinUrl, ct);

                var currentUrl = webView.CoreWebView2.Source ?? string.Empty;
                if (currentUrl.Contains("/login", StringComparison.OrdinalIgnoreCase))
                {
                    _log.Info(actionTag, "Redirected to /login; session expired.");
                    tcs.TrySetResult(new PinActionResult(false, true, "Pinterest session expired; sign in again."));
                    return;
                }

                if (likeOnly)
                {
                    const string waitLikeBtnExpr = """
                        (() => {
                            const direct = document.querySelector('[data-test-id="reactions-button"], [data-test-id="reaction-button"]');
                            if (direct) return true;
                            return Array.from(document.querySelectorAll('button')).some(b => {
                                const r = b.getBoundingClientRect();
                                if (r.width === 0 || r.height === 0 || r.top < 0 || r.top > 500) return false;
                                const lbl = (b.getAttribute('aria-label') || '').toLowerCase();
                                return lbl.includes('react') || lbl.includes('like') || lbl.includes('love') || lbl.includes('heart');
                            });
                        })()
                        """;
                    await WaitForConditionAsync(webView, waitLikeBtnExpr, maxWaitMs: 7000, ct);

                    if (rateLimited)
                    {
                        _governor.RecordRateLimitOrChallenge(DateTimeOffset.UtcNow, "rate_limited");
                        tcs.TrySetResult(new PinActionResult(false, true, "Pinterest rate-limited the action; backing off."));
                        return;
                    }

                    const string likeScript = """
                        (() => {
                            const isLikedEl = (el) => {
                                if (el.getAttribute('aria-pressed') === 'true') return true;
                                const lbl = (el.getAttribute('aria-label') || '').toLowerCase();
                                if (lbl.includes('remove') || lbl.includes('unlike')) return true;
                                const svg = el.querySelector('svg');
                                if (svg) {
                                    const color = (window.getComputedStyle(svg).color || '').toLowerCase();
                                    const fill = (svg.getAttribute('fill') || window.getComputedStyle(svg).fill || '').toLowerCase();
                                    if (color.includes('230, 0, 35') || fill.includes('230, 0, 35') || fill.includes('#e60023')) return true;
                                }
                                return false;
                            };

                            const root = document.querySelector('[data-test-id="closeup-action-bar"], [data-test-id="closeup-body"]') || document;
                            let btn = root.querySelector('[data-test-id="reactions-button"], [data-test-id="reaction-button"]');
                            if (!btn) {
                                btn = Array.from(root.querySelectorAll('button')).find(b => {
                                    const r = b.getBoundingClientRect();
                                    if (r.width === 0 || r.height === 0 || r.top < 0 || r.top > 500) return false;
                                    const lbl = (b.getAttribute('aria-label') || '').toLowerCase();
                                    const tid = (b.getAttribute('data-test-id') || '').toLowerCase();
                                    if (tid.includes('count')) return false;
                                    return lbl.includes('react') || lbl.includes('like') || lbl.includes('love') || lbl.includes('heart');
                                });
                            }
                            if (!btn) return 'missing';
                            if (isLikedEl(btn)) return 'already';
                            btn.click();
                            return 'clicked';
                        })()
                        """;

                    likeClickStarted = true;
                    var likeRaw = await webView.CoreWebView2.ExecuteScriptAsync(likeScript);
                    var likeStatus = JsonSerializer.Deserialize<string>(likeRaw) ?? "missing";
                    _log.Info("like", $"clickStatus={likeStatus}");

                    if (likeStatus == "already")
                    {
                        tcs.TrySetResult(new PinActionResult(true, false, "Already liked on Pinterest."));
                        return;
                    }

                    if (likeStatus == "clicked")
                    {
                        const string verifyLikedScript = """
                            (() => {
                                const root = document.querySelector('[data-test-id="closeup-action-bar"], [data-test-id="closeup-body"]') || document;
                                const btn = root.querySelector('[data-test-id="reactions-button"], [data-test-id="reaction-button"]') ||
                                    Array.from(root.querySelectorAll('button')).find(b => {
                                        const r = b.getBoundingClientRect();
                                        if (r.width === 0 || r.height === 0 || r.top < 0 || r.top > 500) return false;
                                        const lbl = (b.getAttribute('aria-label') || '').toLowerCase();
                                        return lbl.includes('react') || lbl.includes('like') || lbl.includes('love') || lbl.includes('heart');
                                    });
                                if (!btn) return false;
                                if (btn.getAttribute('aria-pressed') === 'true') return true;
                                const lbl = (btn.getAttribute('aria-label') || '').toLowerCase();
                                if (lbl.includes('remove') || lbl.includes('unlike')) return true;
                                const svg = btn.querySelector('svg');
                                if (svg) {
                                    const color = (window.getComputedStyle(svg).color || '').toLowerCase();
                                    const fill = (svg.getAttribute('fill') || window.getComputedStyle(svg).fill || '').toLowerCase();
                                    if (color.includes('230, 0, 35') || fill.includes('230, 0, 35') || fill.includes('#e60023')) return true;
                                }
                                return false;
                            })()
                            """;

                        var domLiked = false;
                        for (var i = 0; i < 20; i++)
                        {
                            if (likeConfirmedByNetwork)
                            {
                                break;
                            }

                            domLiked = await webView.CoreWebView2.ExecuteScriptAsync(verifyLikedScript) == "true";
                            if (domLiked)
                            {
                                // Give the network POST a tiny moment to complete before disposing WebView2.
                                await Task.Delay(TimeSpan.FromMilliseconds(350), ct);
                                break;
                            }

                            await Task.Delay(TimeSpan.FromMilliseconds(150), ct);
                        }

                        _log.Info("like", $"confirmed net={likeConfirmedByNetwork} dom={domLiked}");
                        if (likeConfirmedByNetwork || domLiked)
                        {
                            tcs.TrySetResult(new PinActionResult(true, false, "Liked wallpaper on Pinterest."));
                            return;
                        }
                    }

                    tcs.TrySetResult(new PinActionResult(false, true, "Could not confirm Like on Pinterest; opening pin in browser."));
                    return;
                }

                // Wait specifically for the main Pin closeup's Save button (and board dropdown if saving to a named board).
                var waitSaveExpr = string.IsNullOrEmpty(boardName)
                    ? "!!document.querySelector('[data-test-id=\"PinBetterSaveButton\"], button[aria-label=\"Save\"]')"
                    : "!!document.querySelector('[data-test-id=\"board-dropdown-select-button\"]') || !!document.querySelector('[data-test-id=\"PinBetterSaveButton\"]')";
                await WaitForConditionAsync(webView, waitSaveExpr, maxWaitMs: 7000, ct);

                if (rateLimited)
                {
                    _governor.RecordRateLimitOrChallenge(DateTimeOffset.UtcNow, "rate_limited");
                    tcs.TrySetResult(new PinActionResult(false, true, "Pinterest rate-limited the action; backing off."));
                    return;
                }

                // Save to Pinterest (either default My Saves or a specific board by name).
                var alreadySaved = false;
                var clickedSave = false;

                // Scoped strictly to the main closeup pin action bar (top < 500px) so we never click "More like this" pins below.
                const string saveMainButtonScript = """
                    (() => {
                        const root = document.querySelector('[data-test-id="closeup-action-bar"], [data-test-id="closeup-body"]') || document;
                        const candidates = [
                            ...root.querySelectorAll('[data-test-id="PinBetterSaveButton"] button'),
                            ...root.querySelectorAll('[data-test-id="PinBetterSaveButton"]'),
                            ...root.querySelectorAll('button[aria-label="Save"]'),
                            ...root.querySelectorAll('button')
                        ];
                        const btn = candidates.find(el => {
                            const r = el.getBoundingClientRect();
                            if (r.width === 0 || r.height === 0 || r.top < 0 || r.top > 500) return false;
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

                if (string.IsNullOrEmpty(boardName))
                {
                    saveClickStarted = true;
                    var saveRaw = await webView.CoreWebView2.ExecuteScriptAsync(saveMainButtonScript);
                    var saveStatus = JsonSerializer.Deserialize<string>(saveRaw) ?? "missing";
                    _log.Info("save", $"defaultSave={saveStatus}");
                    alreadySaved = saveStatus == "already";
                    clickedSave = saveStatus == "clicked";
                }
                else
                {
                    // Check if the board picker pill is already set to our target board — if so, just click Save directly!
                    var escapedBoard = JsonSerializer.Serialize(boardName);
                    var checkCurrentBoardScript = $$"""
                        (() => {
                            const root = document.querySelector('[data-test-id="closeup-action-bar"], [data-test-id="closeup-body"]') || document;
                            const pill = root.querySelector('[data-test-id="board-dropdown-select-button"]');
                            if (!pill) return 'no_pill';
                            const txt = (pill.innerText || '').trim().toLowerCase();
                            const target = {{escapedBoard}}.toLowerCase();
                            return txt === target ? 'already_selected' : 'different';
                        })()
                        """;
                    var pillState = JsonSerializer.Deserialize<string>(
                        await webView.CoreWebView2.ExecuteScriptAsync(checkCurrentBoardScript)) ?? "no_pill";
                    _log.Info("save", $"pillState={pillState}");

                    if (pillState == "already_selected")
                    {
                        saveClickStarted = true;
                        var saveRaw = await webView.CoreWebView2.ExecuteScriptAsync(saveMainButtonScript);
                        var saveStatus = JsonSerializer.Deserialize<string>(saveRaw) ?? "missing";
                        _log.Info("save", $"directSave={saveStatus}");
                        alreadySaved = saveStatus == "already";
                        clickedSave = saveStatus == "clicked";
                    }
                    else
                    {
                        const string openDropdownScript = """
                            (() => {
                                const root = document.querySelector('[data-test-id="closeup-action-bar"], [data-test-id="closeup-body"]') || document;
                                const btn =
                                    root.querySelector('[data-test-id="board-dropdown-select-button"]') ||
                                    Array.from(root.querySelectorAll('button')).find(b => {
                                        const r = b.getBoundingClientRect();
                                        if (r.width === 0 || r.height === 0 || r.top < 0 || r.top > 500) return false;
                                        const lbl = (b.getAttribute('aria-label') || '').toLowerCase();
                                        const t = (b.innerText || '').trim().toLowerCase();
                                        return lbl.includes('select a board') || t === 'my saves' || t === 'profile';
                                    });
                                if (!btn) return false;
                                btn.click();
                                return true;
                            })()
                            """;
                        var opened = await webView.CoreWebView2.ExecuteScriptAsync(openDropdownScript) == "true";
                        _log.Info("save", $"dropdownOpened={opened}");

                        if (opened)
                        {
                            // Wait for the Save popover (search input or board rows), strictly excluding Pinterest's header search bar.
                            const string findPopoverExpr = """
                                (() => {
                                    if (document.querySelector('[data-test-id^="board-row-"], #pickerSearchField, [data-test-id="board-picker-search-input"] input')) return true;
                                    const inputs = Array.from(document.querySelectorAll('input[aria-label="Search"], input[placeholder*="Search"]'));
                                    return inputs.some(i => i.id !== 'searchBoxInput' && !i.closest('header') && !i.closest('[data-test-id="search-box-input"]'));
                                })()
                                """;
                            await WaitForConditionAsync(webView, findPopoverExpr, maxWaitMs: 3500, ct);

                            var searchBoardScript = $$"""
                                (() => {
                                    const inputs = Array.from(document.querySelectorAll('#pickerSearchField, [data-test-id="board-picker-search-input"] input, [role="dialog"] input, input[aria-label="Search"], input[placeholder*="Search"]'));
                                    const input = inputs.find(i => i.id !== 'searchBoxInput' && !i.closest('header') && !i.closest('[data-test-id="search-box-input"]'));
                                    if (!input) return false;
                                    input.focus();
                                    const setter = Object.getOwnPropertyDescriptor(window.HTMLInputElement.prototype, 'value').set;
                                    setter.call(input, {{escapedBoard}});
                                    input.dispatchEvent(new Event('input', { bubbles: true }));
                                    input.dispatchEvent(new Event('change', { bubbles: true }));
                                    return true;
                                })()
                                """;
                            var searched = await webView.CoreWebView2.ExecuteScriptAsync(searchBoardScript) == "true";
                            _log.Info("save", $"popoverSearched={searched}");

                            // Poll for the matching board row inside the popover (excluding the dropdown trigger button & header).
                            var waitRowExpr = $$"""
                                (() => {
                                    const target = {{escapedBoard}}.toLowerCase();
                                    const rows = Array.from(document.querySelectorAll('[data-test-id^="board-row-"], [role="dialog"] [role="listitem"], [role="dialog"] [role="button"]'));
                                    return rows.some(row => {
                                        if (row.closest('header') || row.closest('[data-test-id="board-dropdown-select-button"]')) return false;
                                        const tid = (row.getAttribute('data-test-id') || '').toLowerCase();
                                        if (tid === 'board-row-' + target) return true;
                                        const lines = (row.innerText || '').trim().toLowerCase().split('\n').map(l => l.trim());
                                        return lines.includes(target);
                                    });
                                })()
                                """;
                            await WaitForConditionAsync(webView, waitRowExpr, maxWaitMs: 2500, ct);

                            var hoverRowScript = $$"""
                                (() => {
                                    const target = {{escapedBoard}}.toLowerCase();
                                    const rows = Array.from(document.querySelectorAll('[data-test-id^="board-row-"], [role="dialog"] [role="listitem"], [role="dialog"] [role="button"]'));
                                    for (const row of rows) {
                                        if (row.closest('header') || row.closest('[data-test-id="board-dropdown-select-button"]')) continue;
                                        const tid = (row.getAttribute('data-test-id') || '').toLowerCase();
                                        const lines = (row.innerText || '').trim().toLowerCase().split('\n').map(l => l.trim());
                                        if (tid === 'board-row-' + target || lines.includes(target)) {
                                            row.dispatchEvent(new PointerEvent('pointerenter', { bubbles: true }));
                                            row.dispatchEvent(new PointerEvent('pointerover', { bubbles: true }));
                                            row.dispatchEvent(new MouseEvent('mouseenter', { bubbles: true }));
                                            row.dispatchEvent(new MouseEvent('mouseover', { bubbles: true }));
                                            row.setAttribute('data-pw-target-board', '1');
                                            return true;
                                        }
                                    }
                                    return false;
                                })()
                                """;
                            var hovered = await webView.CoreWebView2.ExecuteScriptAsync(hoverRowScript) == "true";
                            _log.Info("save", $"rowMatched={hovered}");

                            if (hovered)
                            {
                                await Task.Delay(TimeSpan.FromMilliseconds(250), ct);
                                saveClickStarted = true;
                                const string clickBoardSaveScript = """
                                    (() => {
                                        const row = document.querySelector('[data-pw-target-board="1"]');
                                        if (!row) return 'missing';
                                        const saveBtn = Array.from(row.querySelectorAll('button')).find(b =>
                                            (b.innerText || b.getAttribute('aria-label') || '').trim().toLowerCase() === 'save');
                                        if (saveBtn) {
                                            saveBtn.click();
                                            return 'clicked_save';
                                        }
                                        row.click();
                                        return 'clicked_row';
                                    })()
                                    """;
                                var rowRaw = await webView.CoreWebView2.ExecuteScriptAsync(clickBoardSaveScript);
                                var rowResult = JsonSerializer.Deserialize<string>(rowRaw) ?? "missing";
                                _log.Info("save", $"rowClick={rowResult}");
                                clickedSave = rowResult is "clicked_save" or "clicked_row";

                                // If clicking the row only selected the board in the dropdown, click the main Save button too.
                                if (rowResult == "clicked_row")
                                {
                                    await Task.Delay(TimeSpan.FromMilliseconds(600), ct);
                                    if (!repinConfirmedByNetwork)
                                    {
                                        var fallbackSave = JsonSerializer.Deserialize<string>(
                                            await webView.CoreWebView2.ExecuteScriptAsync(saveMainButtonScript)) ?? "missing";
                                        _log.Info("save", $"fallbackMainSave={fallbackSave}");
                                    }
                                }
                            }
                        }
                    }
                }

                // Wait for network POST confirmation or main closeup DOM "Saved" state (exits immediately once confirmed).
                var domSaysSaved = false;
                if (!alreadySaved && clickedSave)
                {
                    const string verifySavedScript = """
                        (() => {
                            const root = document.querySelector('[data-test-id="closeup-action-bar"], [data-test-id="closeup-body"]') || document;
                            const btns = Array.from(root.querySelectorAll('[data-test-id="PinBetterSaveButton"], [data-test-id="PinBetterSaveButton"] button, button'));
                            return btns.some(b => {
                                const r = b.getBoundingClientRect();
                                return r.width > 0 && r.top >= 0 && r.top < 500 &&
                                       (b.innerText || '').trim().toLowerCase() === 'saved';
                            });
                        })()
                        """;
                    for (var i = 0; i < 20; i++)
                    {
                        if (repinConfirmedByNetwork)
                        {
                            break;
                        }

                        domSaysSaved = await webView.CoreWebView2.ExecuteScriptAsync(verifySavedScript) == "true";
                        if (domSaysSaved)
                        {
                            await Task.Delay(TimeSpan.FromMilliseconds(350), ct);
                            break;
                        }

                        await Task.Delay(TimeSpan.FromMilliseconds(150), ct);
                    }
                }

                _log.Info("save", $"done already={alreadySaved} clicked={clickedSave} net={repinConfirmedByNetwork} dom={domSaysSaved}");

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
                _log.Info(actionTag, $"exception={ex.GetType().Name}: {ex.Message}");
                tcs.TrySetResult(new PinActionResult(false, true, $"Background action failed ({ex.Message}); opening in browser."));
            }
            finally
            {
                await WebView2Cleanup.DisposeAndReleaseAsync(webView, hostWindow);
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
        Opacity = 1,
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
                var verifiedCandidates = new ConcurrentQueue<(string SourceType, string SourceValue, ExtractedPin Pin)>();
                var domFallbackCandidates = new ConcurrentQueue<(string SourceType, string SourceValue, ExtractedPin Pin)>();
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
                        if (status == 429)
                        {
                            rateLimited = true;
                            return;
                        }

                        var isApi = url.Contains("/resource/", StringComparison.OrdinalIgnoreCase) ||
                                    url.Contains("/graphql", StringComparison.OrdinalIgnoreCase);
                        if (status == 403 && isApi)
                        {
                            rateLimited = true;
                            return;
                        }

                        if (!isApi)
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
                            if (!_db.IsBlocked(pin.PinId) && _db.GetPinById(pin.PinId) is null && seenIds.TryAdd(pin.PinId, 0))
                            {
                                verifiedCandidates.Enqueue((currentSourceType, currentSourceValue, pin));
                            }
                        }
                    }
                    catch
                    {
                        // Ignore unreadable responses (e.g., cancelled or empty bodies).
                    }
                };

                var maxScrolls = Math.Clamp(config.Fetching.MaxScrollStepsPerSource, 1, 8);
                var collectCap = targetCandidateCount + 5;

                foreach (var (sourceType, sourceValue, url) in targets)
                {
                    if (ct.IsCancellationRequested || rateLimited || loginRequired || verifiedCandidates.Count >= collectCap)
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

                    await ExtractEmbeddedPageJsonAsync(webView, config.Filters, sourceType, sourceValue, seenIds, verifiedCandidates);

                    for (var step = 0; step < maxScrolls; step++)
                    {
                        if (ct.IsCancellationRequested || rateLimited || verifiedCandidates.Count >= collectCap)
                        {
                            break;
                        }

                        await webView.CoreWebView2.ExecuteScriptAsync("window.scrollBy(0, 1400);");
                        await Task.Delay(_governor.GetHumanStepDelay(), ct);
                    }

                    // Only collect unverified DOM thumbnails if XHR/SSR didn't already give us enough verified pins.
                    if (verifiedCandidates.Count < targetCandidateCount && !ct.IsCancellationRequested && !rateLimited)
                    {
                        await ExtractDomPinsAsync(webView, config.Filters, sourceType, sourceValue, seenIds, domFallbackCandidates);
                    }
                }

                // Always prioritize dimension-verified XHR/SSR pins first; only append capped DOM fallback if needed.
                var ordered = verifiedCandidates.ToList();
                if (ordered.Count < targetCandidateCount)
                {
                    var fallbackBudget = Math.Min(domFallbackCandidates.Count, (targetCandidateCount - ordered.Count) * 2);
                    ordered.AddRange(domFallbackCandidates.Take(fallbackBudget));
                }

                tcs.TrySetResult(new CollectResult(ordered, rateLimited, loginRequired));
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
            finally
            {
                await WebView2Cleanup.DisposeAndReleaseAsync(webView, hostWindow);
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

    private static async Task ExtractDomPinsAsync(
        WebView2 webView,
        FilterConfig filters,
        string sourceType,
        string sourceValue,
        ConcurrentDictionary<string, byte> seenIds,
        ConcurrentQueue<(string SourceType, string SourceValue, ExtractedPin Pin)> candidates)
    {
        try
        {
            const string domScript = """
                (() => {
                    const out = [];
                    const links = document.querySelectorAll('a[href*="/pin/"]');
                    for (const a of links) {
                        const m = (a.getAttribute('href') || '').match(/\/pin\/(\d+)/);
                        if (!m) continue;
                        const img = a.querySelector('img[src*="pinimg.com"]');
                        if (!img) continue;
                        const src = img.getAttribute('src') || '';
                        const w = img.naturalWidth || img.width || 0;
                        const h = img.naturalHeight || img.height || 0;
                        out.push({ id: m[1], src, w, h });
                    }
                    return JSON.stringify(out);
                })()
                """;
            var rawResult = await webView.CoreWebView2.ExecuteScriptAsync(domScript);
            if (string.IsNullOrWhiteSpace(rawResult) || rawResult == "null")
            {
                return;
            }

            var json = JsonSerializer.Deserialize<string>(rawResult);
            if (string.IsNullOrWhiteSpace(json))
            {
                return;
            }

            using var doc = JsonDocument.Parse(json);
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                var id = item.GetProperty("id").GetString();
                var src = item.GetProperty("src").GetString();
                var w = item.GetProperty("w").GetInt32();
                var h = item.GetProperty("h").GetInt32();

                if (string.IsNullOrWhiteSpace(id) || !PinterestUrlGuard.IsAllowedPinterestImageUrl(src))
                {
                    continue;
                }

                // Quick orientation pre-filter on thumbnail ratio (skip portrait phone wallpapers immediately).
                if (w > 0 && h > 0)
                {
                    if (filters.Orientation == ImageOrientation.Landscape && w < h) continue;
                    if (filters.Orientation == ImageOrientation.Portrait && h < w) continue;
                }

                // Upgrade Pinterest thumbnail URL (/236x/, /474x/, /564x/, /736x/) to /originals/.
                var origUrl = System.Text.RegularExpressions.Regex.Replace(
                    src!,
                    @"/(?:236x|474x|564x|736x)/",
                    "/originals/");

                if (seenIds.TryAdd(id, 0))
                {
                    candidates.Enqueue((sourceType, sourceValue, new ExtractedPin(
                        PinId: id,
                        PinUrl: $"https://www.pinterest.com/pin/{id}/",
                        ImageUrl: origUrl,
                        Width: 0,
                        Height: 0)));
                }
            }
        }
        catch
        {
            // DOM fallback is best-effort.
        }
    }

    private static async Task WaitForConditionAsync(WebView2 webView, string jsBoolExpr, int maxWaitMs, CancellationToken ct)
    {
        var elapsed = 0;
        const int step = 150;
        while (elapsed < maxWaitMs && !ct.IsCancellationRequested)
        {
            try
            {
                if (await webView.CoreWebView2.ExecuteScriptAsync(jsBoolExpr) == "true")
                {
                    return;
                }
            }
            catch
            {
                return;
            }

            await Task.Delay(step, ct);
            elapsed += step;
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

    private sealed record DownloadStats(int Added, int TooSmall, int WrongAspectOrOrientation, int Duplicates);

    private async Task<DownloadStats> DownloadAndFilterSequentiallyAsync(
        List<(string SourceType, string SourceValue, ExtractedPin Pin)> candidates,
        AppConfig config,
        int maxToAdd,
        CancellationToken ct)
    {
        Directory.CreateDirectory(_paths.CacheDirectory);
        var existingHashes = _db.GetAllPerceptualHashes().ToList();
        var added = 0;
        var tooSmall = 0;
        var wrongAspect = 0;
        var duplicates = 0;

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
                    var reason = check.RejectionReason ?? string.Empty;
                    if (reason.Contains("Duplicate", StringComparison.OrdinalIgnoreCase))
                        duplicates++;
                    else if (reason.Contains("resolution", StringComparison.OrdinalIgnoreCase))
                        tooSmall++;
                    else
                        wrongAspect++;
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

        return new DownloadStats(added, tooSmall, wrongAspect, duplicates);
    }

    public void Dispose()
    {
        _http.Dispose();
        _syncLock.Dispose();
    }
}
