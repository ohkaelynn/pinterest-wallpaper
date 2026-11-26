<img src="assets/icon.svg" width="64" height="64" alt="Pinterest Wallpaper Swapper">

# Pinterest Wallpaper Swapper

A lightweight Windows wallpaper swapper—like Windows' built-in slideshow—powered by your **personal Pinterest boards**, **custom search terms**, and optional **home feed**.

> **Open-Source & AI-Generated:** Built transparently with AI assistance using **C# / .NET 8 + WebView2**.  
> **Unofficial Personal-Use Tool:** Not affiliated with Pinterest. Uses your own logged-in WebView2 browser session with gentle background syncing—never aggressive scraping.

## Features

- **Personal Account, No API Needed** — Sign in once inside Microsoft Edge WebView2 (supports 2FA/SSO; your password is never seen or stored). Pulls your own boards (including private/secret boards) right into the Settings dropdowns.
- **Instant Offline Rotation** — Wallpapers rotate from a local SQLite-tracked cache. Clicking **Next Wallpaper** never hits the network.
- **Desktop & Tray Right-Click Menus** — Trigger **Next Wallpaper**, **Previous Wallpaper**, **Like on Pinterest**, **Save to Board**, **Open in Browser**, **Skip & Hide**, or **Pause / Resume** directly from your desktop or dark-themed system tray menu.
- **Background Like & Save** — Like or save the current wallpaper to *My Saves* or any of your Pinterest boards silently in the background.
- **Dark Fluent Settings HUD** — Manage boards, search terms, Home Feed, resolution presets (`720p` to `4K` / `Ultrawide`), orientation, aspect ratio, multi-monitor fit modes, fullscreen/battery pause, and Start with Windows.
- **Zero Admin Rights Required** — Desktop right-click menu and Windows startup use `HKEY_CURRENT_USER` only and can be toggled cleanly from Settings.

## Requirements

- Windows 10 or Windows 11 (x64)
- [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/en-us/download/dotnet/8.0) & Microsoft Edge WebView2 Runtime (pre-installed on Windows 11)

## Build & Run from Source

```powershell
# Run offline unit tests
dotnet test PinterestWallpaper.sln

# Run the app
dotnet run --project src\PinterestWallpaper.App

# Publish a portable Release build
dotnet publish src\PinterestWallpaper.App -c Release -r win-x64 --self-contained false -o publish\win-x64
```

## Quick Start

1. Launch `PinterestWallpaper.exe` — look for the purple **PW** icon in your system tray.
2. Right-click the tray icon → **Settings...** → **Account & Status** → **Sign in to Pinterest...**.
3. Go to **Sources & Save**, click **Load My Boards** to pick your boards (or add search terms / enable Home Feed), and click **Save Changes**.
4. Click **Sync Now** to populate your local wallpaper pool, then right-click your desktop (on Windows 11: *Show more options* → **Pinterest Wallpaper**) or double-click the tray icon to swap wallpapers!

## Docs

- [PLAN.md](PLAN.md) — Roadmap and config reference
- [ARCHITECTURE.md](ARCHITECTURE.md) — How the components fit together
- [AGENTS.md](AGENTS.md) — Rules for AI agents contributing to this repo
- [CONTRIBUTING.md](CONTRIBUTING.md) — Contributing guidelines
- [SECURITY.md](SECURITY.md) — Privacy & local session handling

## License

[MIT](LICENSE)
