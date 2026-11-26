# Implementation Plan

## Stack: C# / .NET 8 + WebView2 (Windows)

- **App & UI:** .NET 8 WPF (or WinUI 3 / lightweight WPF) with a Windows system tray icon and settings window.
- **Pinterest Session:** Microsoft **WebView2** (built into Windows 10/11). You log in normally once; the app reuses that real Edge session to gently fetch pin data when the local cache needs refilling.
- **Wallpaper Engine:** Native Windows `IDesktopWallpaper` COM API (smooth transitions, fit modes, multi-monitor support) with `SystemParametersInfoW` fallback.
- **Desktop Right-Click Menu:** `HKCU\Software\Classes\DesktopBackground\Shell` (no admin required) calling the running app via a local Named Pipe (`--next`, `--prev`, `--open-pin`, `--skip`). Optional Windows 11 top-level menu (`IExplorerCommand` + Sparse MSIX) later.
- **Storage:** `config.json` in `%APPDATA%\PinterestWallpaper\`, SQLite DB + image cache + WebView2 user profile in `%LOCALAPPDATA%\PinterestWallpaper\`.

---

## Config Sketch (`config.json`)

```json
{
  "rotation": {
    "enabled": true,
    "intervalMinutes": 30,
    "jitterPercent": 15,
    "pauseOnBattery": true,
    "pauseOnFullscreen": true
  },
  "display": {
    "fitMode": "Fill",
    "perMonitorWallpapers": true
  },
  "sources": {
    "boards": [
      { "url": "https://www.pinterest.com/user/wallpapers/", "enabled": true, "weight": 50 }
    ],
    "searches": [
      { "query": "minimalist landscape wallpaper 4k", "enabled": true, "weight": 35 }
    ],
    "homeFeed": { "enabled": false, "weight": 15 }
  },
  "filters": {
    "minWidth": 1920,
    "minHeight": 1080,
    "orientation": "Landscape",
    "aspectRatioMin": 1.3,
    "aspectRatioMax": 3.6
  },
  "fetching": {
    "lowWaterMark": 15,
    "targetPoolSize": 50,
    "maxCacheMb": 500,
    "minSyncCooldownMinutes": 240,
    "maxSyncsPerDay": 4,
    "maxScrollStepsPerSource": 3
  }
}
```

---

## Milestones

1. **Phase 1 — Core App & Local Rotation**
   - Scaffold .NET 8 solution, config loader, SQLite state (`pins`, `history`, `blocklist`), image filter (resolution/aspect/dedupe), `IDesktopWallpaper` wrapper, and rotation timer (with fullscreen/battery pause).
   - *Done when:* App rotates local test wallpapers reliably on a timer and via tray menu.

2. **Phase 2 — Desktop Right-Click Menu**
   - Add single-instance Named Pipe IPC so invoking `PinterestWallpaper.exe --next` (or `--prev`, `--open-pin`, `--skip`, `--toggle-pin`, `--toggle-pause`) tells the running app to act immediately (<150ms).
   - Add **Pin Current Wallpaper** option to lock the current wallpaper against timed swaps until unpinned.
   - Add toggleable `HKCU` Registry installer/uninstaller for the desktop right-click submenu.
   - *Done when:* Right-clicking the desktop swaps, pins, or skips wallpapers instantly.

3. **Phase 3 — Personal Pinterest Sync (WebView2)**
   - Embed WebView2 for one-time interactive login (`pinterest.com/login`).
   - Add **Rate-Limit Governor** (cooldowns, daily sync cap, randomized delays, sequential image downloads, backoff on `429`/challenges).
   - Extract high-res (`originals`) image URLs from configured boards, search terms, and optional home feed into the local cache.
   - *Done when:* Local cache refills naturally in the background without aggressive requests.

4. **Phase 4 — Settings UI & Polish**
   - Clean WPF settings window: manage boards/searches/weights, timings, resolution filters, cache size, history/blocklist, and view sync status ("Last sync · daily budget · cached count").
   - Start-with-Windows toggle (`HKCU\...\Run`).

5. **Phase 5 — Release & Optional Win11 Top-Level Menu**
   - Unit tests (governor, filters, config, SQLite—all mocked, no live Pinterest calls).
   - GitHub Actions release build (portable ZIP / installer).
   - Optional: Sparse MSIX + `IExplorerCommand` so "Next Wallpaper" appears directly in Windows 11's top-level context menu.
