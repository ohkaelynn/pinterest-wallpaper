<img src="assets/icon.svg" width="64" height="64" alt="Pinterest Wallpaper Swapper">

# Pinterest Wallpaper Swapper

A lightweight Windows wallpaper swapper—like Windows' built-in slideshow—powered by your **personal Pinterest boards**, **custom search terms**, and optional **home feed**.

> **Open-Source & AI-Generated:** Built transparently with AI assistance using **C# / .NET 8 + WebView2**.  
> **Unofficial Personal-Use Tool:** Not affiliated with Pinterest. Uses your own logged-in browser session with conservative, human-paced background syncing—never aggressive scraping.

## Features (Planned)

- **Personal Account, No API Needed** — Sign in once inside Windows' built-in Edge WebView2 (supports 2FA/SSO; your password is never touched or stored).
- **Gentle & Local-First** — Wallpapers rotate locally from a cached pool. Clicking "Next" never hits the network; background refreshes only run occasionally when the pool gets low.
- **Desktop Right-Click Menu** — Right-click your desktop for **Next Wallpaper**, **Previous Wallpaper**, **Open Pin in Browser**, or **Skip & Hide**.
- **Configurable Everything** — Adjust rotation intervals, boards, search queries, source weights, minimum resolution/aspect ratio, multi-monitor behavior, and fullscreen/battery pause.
- **Native Windows App** — System tray icon, clean settings window, smooth `IDesktopWallpaper` transitions, and no admin rights required.

## Docs

- [PLAN.md](PLAN.md) — Roadmap, C#/.NET 8 stack, and `config.json` layout
- [ARCHITECTURE.md](ARCHITECTURE.md) — How the pieces fit together
- [AGENTS.md](AGENTS.md) — Rules for AI agents working on this repo
- [CONTRIBUTING.md](CONTRIBUTING.md) — Contributing guidelines
- [SECURITY.md](SECURITY.md) — Privacy & local session handling

## License

MIT (to be added when code is scaffolded).
