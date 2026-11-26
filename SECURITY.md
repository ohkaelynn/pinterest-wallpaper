# Security & Privacy

- **No password storage:** You log into Pinterest directly inside Microsoft WebView2. The app never sees or stores your password.
- **Local-only:** Session data stays in your local Windows user profile (`%LOCALAPPDATA%\PinterestWallpaper\`). No telemetry or third-party servers.
- **Safe logging:** Logs never include cookies, tokens, or auth headers.
- **Strict URL allowlisting:** WebView2 navigation and browser opens are restricted to `https://` `pinterest.*` domains; image downloads are restricted to `*.pinimg.com` with a 50 MB per-file cap.
- **Local IPC:** The Named Pipe used by the right-click menu is scoped to the current Windows user (`PipeOptions.CurrentUserOnly`).
- **Verifiable builds:** Release installers and portable ZIPs are built directly by GitHub Actions (`.github/workflows/release.yml`) with `SHA256SUMS.txt` checksums.

If you find a security issue, please report it privately via GitHub Security Advisories rather than opening a public issue.
