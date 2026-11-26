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
- **Zero Admin Rights Required** — Installer, desktop right-click menu, and Windows startup all use `HKEY_CURRENT_USER` / `%LOCALAPPDATA%` only.

## Download & Install

Grab the latest release from the **Releases** page (built automatically via GitHub Actions with `SHA256SUMS.txt`):

- **Per-User Installer (`*-win-x64-setup.exe`)** — Installs to `%LOCALAPPDATA%\Programs\PinterestWallpaper` with Start Menu shortcuts and clean uninstall (no admin required).
- **Portable ZIP (`*-win-x64-portable.zip`)** — Unzip anywhere and run `PinterestWallpaper.exe`.

> **Windows SmartScreen Note:** Because this is an unsigned indie open-source app, Windows may show *"Windows protected your PC"*. Click **More info → Run anyway**, verify against `SHA256SUMS.txt`, or build directly from source below.

## Quick Start

1. Launch **Pinterest Wallpaper** — look for the purple **PW** icon in your system tray.
2. Right-click the tray icon → **Settings...** → **Account & Status** → **Sign in to Pinterest...**.
3. Go to **Sources & Save**, click **Load My Boards** to pick your boards (or add search terms / enable Home Feed), and click **Save Changes**.
4. Click **Sync Now** to populate your local wallpaper pool, then right-click your desktop (on Windows 11: *Show more options* → **Pinterest Wallpaper**) or double-click the tray icon to swap wallpapers.

## Build from Source

Requires Windows 10/11 (x64) and the [.NET 8 SDK](https://dotnet.microsoft.com/en-us/download/dotnet/8.0):

```powershell
# Run offline unit tests
dotnet test PinterestWallpaper.sln

# Run the app
dotnet run --project src\PinterestWallpaper.App

# Publish a self-contained portable build
dotnet publish src\PinterestWallpaper.App -c Release -r win-x64 --self-contained true -o publish\win-x64
```

To cut a new GitHub Release automatically via GitHub Actions:

```powershell
git tag v0.1.0
git push origin v0.1.0
```

## Docs

- [PLAN.md](PLAN.md) — Roadmap and config reference
- [ARCHITECTURE.md](ARCHITECTURE.md) — How the components fit together
- [AGENTS.md](AGENTS.md) — Rules for AI agents contributing to this repo
- [CONTRIBUTING.md](CONTRIBUTING.md) — Contributing guidelines
- [SECURITY.md](SECURITY.md) — Privacy & local session handling

## License

[MIT](LICENSE)
