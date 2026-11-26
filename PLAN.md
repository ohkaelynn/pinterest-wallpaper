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
    "minSyncCooldownMinutes": 60,
    "maxSyncsPerDay": 12,
    "maxScrollStepsPerSource": 4
  },
  "actions": {
    "defaultSaveBoardName": "",
    "likeWhenSaving": true
  }
}
```

---

## Milestones

1. **Phase 1 — Core App & Local Rotation** *(Done)*
   - .NET 8 solution, config loader, SQLite state (`pins`, `history`, `blocklist`), SkiaSharp image filter (resolution/aspect/dHash dedupe), `IDesktopWallpaper` wrapper, and rotation timer (with fullscreen/battery pause).

2. **Phase 2 — Desktop Right-Click Menu** *(Done)*
   - Single-instance Named Pipe IPC (`--next`, `--prev`, `--like-pin`, `--save-pin`, `--open-pin`, `--skip`, `--toggle-pause`).
   - Toggleable `HKCU` Registry installer/uninstaller for the desktop right-click submenu.

3. **Phase 3 — Personal Pinterest Sync (WebView2)** *(Done)*
   - Embedded WebView2 for interactive login (`pinterest.com/login`) and gentle background sync.
   - Rate-Limit Governor (cooldowns, daily cap, backoff on `429`/challenges), high-res `originals` pin extraction + DOM fallback, and LRU cache eviction.

4. **Phase 4 — Settings HUD, Background Like/Save & Polish** *(Done)*
   - Dark Fluent WPF Settings HUD with resolution presets, board loader & dropdowns, login status, Clear Cache / Open Cache Folder, and Start with Windows (`HKCU\...\Run`).
   - Verified silent background **Like on Pinterest** and **Save to Board**, dark tray menu, and custom `PW` app icon.
   - Strict `pinterest.*` / `*.pinimg.com` URL allowlist, 50 MB download cap, and Sign Out & Clear Session.

5. **Phase 5 — Release & Optional Win11 Top-Level Menu** *(Done — Win11 Sparse MSIX optional)*
   - Per-user Inno Setup installer (`installer/PinterestWallpaper.iss`) + Portable ZIP + GitHub Actions CI & Release workflows (`.github/workflows/ci.yml`, `.github/workflows/release.yml`) with `SHA256SUMS.txt`.
   - Optional future enhancement: Sparse MSIX + `IExplorerCommand` so *Pinterest Wallpaper* appears directly in Windows 11's top-level context menu.
