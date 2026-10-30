# Stretto privacy notice

Updated October 30, 2026. This notice covers the free Stretto desktop app, its bundled browser extensions and the project website.

## Data stays on your computer

Stretto has no hosted user accounts, advertising, analytics service or automatic crash-reporting service. It does not send your download history to its developer.

The desktop app stores settings, queues, download URLs, filenames, progress and diagnostic logs locally. Saved website logins and saved proxy passwords are protected using Windows encryption for your Windows user account. A pairing token and approved-extension list allow the browser extension to communicate with the app. Download URLs may contain private or temporary access tokens, so treat stored history and exported download lists as private.

The EXE version stores app data under your local application-data folder in `Stretto`. The MSIX version uses its Windows-managed local app-data folder. Downloaded files are saved to the folders you choose. Optional helper programs and the MSIX version's stable extension copy are stored in app data.

## Browser extension permissions

The extension observes downloads and selected network requests to recognize downloadable files and media. Broad website access lets it work across download hosts rather than a fixed list of sites. Cookie permission lets it pass the session cookies needed for a download you send to Stretto. Context-menu and active-tab permissions support sending links, images and media from the current page. Notifications report handoff status; scripting supports extracting links/media when requested.

When you send a download, the extension can pass its URL, suggested filename, size/type, page title, referring page, cookies, user agent and relevant request headers to the local Stretto app. The connection uses the loopback address on your computer and a pairing token. The app then contacts the download server, using the request context needed to fetch the file. Session cookies, passwords and authorization headers are excluded from saved/exported download entries.

Detected-media information and recent request metadata are stored locally by the extension. Chromium uses session storage for temporary information; shared extension code can use local storage when a browser has no session-storage API. The recent-request history is bounded and ignores expired records. The extension does not send browsing history to a Stretto-operated server.

You can switch monitoring off in the extension or app, exclude sites, revoke paired extensions in Settings, or remove the extension. POST and unrecognized browser requests remain with the browser. Some single-use or browser-protected downloads cannot be fetched again by an external app; use the browser for those downloads.

## Network requests and optional features

- Downloads contact the sites you select, which receive normal connection information such as your IP address and any request credentials needed for that download.
- In builds with a configured GitHub release channel, the EXE version's update checks contact GitHub for Stretto release information and include the app version. Automatic update checking can be turned off in Settings. This pre-release build has no GitHub update channel configured. MSIX installations use their installation source for updates instead of Stretto's GitHub check.
- Installing optional yt-dlp or ffmpeg components downloads files from their configured GitHub/Gyan sources. Video extraction and conversion use those external programs; site extraction can contact the selected video service.
- Clipboard monitoring is optional and off by default. When enabled, Stretto checks clipboard text for download links locally.
- An antivirus scanner or a program configured as a completion action runs only according to your settings. Those programs have their own behavior and privacy policies.

## Reports, retention and removal

The project website is hosted on GitHub Pages. Visiting it, downloading release
files or using GitHub Issues contacts GitHub, which processes those interactions
under its [privacy statement](https://docs.github.com/en/site-policy/privacy-policies/github-general-privacy-statement).
The project website adds no advertising, analytics or tracking scripts. GitHub
issue reports are public; review their contents before posting. Visiting the
website is separate from running the desktop app and does not cause the app to
upload its history.

Diagnostics are copied or shared only when you choose to do so. Logs can contain download URLs, filenames, paths and error details. Review diagnostic text and exported lists before sharing them publicly.

Removing a download entry does not remove its file unless you choose that option. Uninstalling the EXE preserves settings/tools for reinstallation; you can remove its app-data folder after exiting Stretto. Windows normally removes the MSIX app-data folder on uninstall. Files saved outside app data remain where you saved them. Removing a browser extension removes its extension storage according to the browser's rules.

For questions, use the support contact on the page where you obtained Stretto. Do not post passwords, cookies, pairing tokens or private download URLs in a public report.
