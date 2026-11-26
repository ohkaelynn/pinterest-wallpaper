# AI Agent Guidelines (`AGENTS.md`)

This is an open-source, AI-generated **C# / .NET 8 + WebView2** Windows project. Follow these rules on every change:

1. **Never bypass the Rate-Limit Governor:** All Pinterest requests must obey cooldowns, daily caps, randomized delays, sequential downloads, and backoff on `429`/challenges. Never add parallel scrapers or CAPTCHA bypasses.
2. **Never hit the network on wallpaper swap:** "Next Wallpaper" (timer, tray, or right-click) reads from the local cache only.
3. **Never store or log credentials:** Login happens interactively in WebView2 only. Never log cookies, auth headers, or session files, and never commit personal boards or cached images.
4. **No admin rights required:** Registry entries (desktop context menu, startup) must use `HKEY_CURRENT_USER` only and have a clean uninstall path.
5. **Keep it simple & tested:** Avoid over-engineering. Unit tests must use mocked data—never hit live Pinterest in tests. Update [PLAN.md](PLAN.md) if the architecture or config changes.
