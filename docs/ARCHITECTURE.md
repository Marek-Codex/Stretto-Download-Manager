# Stretto architecture

How each part of Stretto works, and the reasons behind the choices that are easy to
undo by accident. Written as the code was, so sections read as notes rather than a manual.

## Status

| Piece | State |
|---|---|
| `Stretto.Core` — segmented engine | Working, verified |
| `Stretto.Check` — headless CLI harness | Working |
| `tools/testserver.js` — deterministic test server | Working |
| `Stretto.App` — WinUI 3 shell | Working, wired to the engine |
| `Stretto.Core/HandoffListener` — browser endpoint | Working, verified |
| `extensions/chromium/` — MV3 extension | Loaded and tested in Brave; unpacked install |
| `extensions/firefox/` — Firefox extension | Built and packaged; signing/distribution not complete |
| `Stretto.App/Services/QueueService` — queues + scheduler | Working, with UI |

## Implementation notes

Queues and the scheduler work end to end: `Dialogs/QueueDialog` (menu → Queues and
scheduler) creates queues, orders files in them and sets the timetable, and the engine
releases work on schedule. Fixed along the way: `IsEnabled` on a `StackPanel` kills
`XamlCompiler.exe` with exit code 1 and no diagnostic — it is a `Control` property,
so the dialog toggles each child control instead (`SetScheduleEnabled`).

Also done since: a full Settings screen (General, Network, Browser monitoring,
Advanced), global speed limit, keep-awake and finish actions, server timestamps,
app-owned capture rules, the per-download progress window, taskbar progress, and the
browser context-menu entries plus Copy as cURL (the exact request, for debugging elsewhere).

Distribution still needs work: the browser add-on is not store-installed, browser coverage
is limited, and Firefox's regular-release install needs a signed add-on. See
[`extensions/README.md`](../extensions/README.md) for the current install and support limits.

## Design decisions worth keeping

**Positional writes, no chunk files.** Stretto
preallocates the output file once and each worker does `RandomAccess.WriteAsync` at its own
offset. No merge pass, and resume state is just a small sidecar JSON of per-segment byte
counts.

**Segment requests must get a 206.** If a server answers a ranged segment request with 200
it is sending the whole file down that connection; writing that at the segment offset would
corrupt the output. The engine treats a 200 there as a hard error.

**No automatic decompression.** Content-Encoding and Range do not mix — a compressed body
makes byte offsets meaningless.

**The sidecar is deliberately stale.** Resume state saves once a second, so a hard kill
leaves it slightly *behind* real progress. Re-downloading a few hundred KB is free;
overstating progress would corrupt the file.

**Resume requires an exact match** on both length and ETag/Last-Modified. Anything else
restarts, because a changed remote file would otherwise be silently stitched together.

## Views and commands

Three workspaces in a `NavigationView`: **Transfers** (everything not finished, failures
included), **Library** (finished files) and **Queues** (the queue editor, hosted as a page).
File types are **filter chips inside Library** rather than a tree in the pane, because a type
only ever described finished files; a download still in flight belongs in Transfers.
Why the layout moved away from the Active/Complete/categories sidebar is in
[UI-IDENTITY-AUDIT.md](UI-IDENTITY-AUDIT.md).

The list is grouped. Transfers groups by stage (Downloading, Needs attention, Up next,
Paused) so a failure sits under a heading that says why; Library groups by when things
finished, or by type, or not at all when sorted by name or size. `Refresh` syncs the groups
in place on every tick, removing moved rows from every group before adding any, then
re-selecting what was selected, so the selection survives a row changing group.

The command bar follows the selection. With nothing selected it holds commands for the whole
view (Pause all and Resume all in Transfers, Open downloads folder and Clear list in
Library). With a selection it shows "N selected" and the commands that apply; ones that
cannot apply are hidden rather than disabled, except Pause and Resume, which stay side by
side so neither jumps under the pointer. Each row also carries one state-appropriate button:
pause, resume, retry, or show in folder. Rarely used commands (import, export, the log,
About, Exit) are in the bar's own overflow.

Details (Properties in older builds) is an inline pane beside the list, reusing
`PropertiesDialog` in its embedded mode. Browser state is a footer item in the pane, with a
status dot, and the monitoring switch sits in its flyout.

## Browser integration

`HandoffListener` is a raw `TcpListener` speaking just enough HTTP for three routes, rather
than `HttpListener` — the latter needs a URL ACL registration (an admin `netsh` step) for
most prefixes, and that is not an acceptable install step for a download manager.

The listener binds to loopback, checks origins and requires a pairing token on protected
routes. Discovery, preflight and pairing have separate gates before a token exists.
Originless protected requests still require the token; loopback alone is not authentication.

The Chromium extension cannot block requests through `webRequest` in its ordinary MV3 configuration.
Download capture uses `downloads.onDeterminingFilename` to hold the browser's original
request while Stretto decides whether to take it. If Stretto is unavailable or the user
declines, the extension releases that same request back to the browser; this also preserves
downloads created by form POSTs. Non-blocking observation lists media seen on the current
page — direct files plus HLS/DASH playlists, which arrive with `isStream` so the app routes
them to ffmpeg instead of the segmented engine. A download manager that silently eats
downloads while closed is worse than none.

**Framing matters on pooled connections.** Browsers reuse connections. An HTTP reply that
mis-frames itself (for example a `204` carrying `Content-Length` and `Content-Type`, which
is malformed since a 204 must have no body) desyncs the client parser, and every later
request on that socket reads the previous response. The listener answers preflight as a
`200` with a real body and supports keep-alive properly.

**Pairing asks once, and the token is never shown to anyone.** The extension scans a
small port range for `GET /hello`, then asks for a token with `POST /pair`. Both run ahead
of the token gate, and both still demand a real extension origin, which a web page cannot
forge.

Every extension the app has not approved yet, its own bundled one included, gets a
one-click Allow that defaults to Deny. Approved origins and the shared token persist
together in `pairing-state.json` (an older `extensions.json`/`pairing.txt` is migrated once),
so the prompt happens once per extension. Revoking one rotates the token, which cuts off
every copy of the old one, prompts still pending included.

Pairing supplies authentication separately from the network address.

## Persistence

`%LOCALAPPDATA%\Stretto\downloads.json` holds the list; the bytes already survive in the
engine's per-file `.stretto` sidecars, so this only records which downloads exist. Anything
mid-flight is saved as **paused**, never as downloading, and nothing auto-starts on launch —
an app that silently resumes six downloads when it opens is a nasty surprise. Cookies are
deliberately *not* persisted: they expire, and writing them to disk in plaintext is a poor
trade for the convenience.

## Video download

`MediaExtractor` fetches a page and finds media in it: `<video>`/`<source>`/`<audio>` tags,
`og:video` meta, anchors to media files, and playlist URLs sitting in inline script. Regex
rather than an HTML parser dependency, because half the interesting URLs on a video page
live inside JavaScript where a DOM parser would not help either.

**This does not need the extension.** Pasting a page URL works standalone, which covers the
common case. The extension extends it to two places scraping cannot reach: pages behind a
login (it supplies the relevant session cookies) and
sites that construct media URLs at runtime, where only watching real network requests finds
them.

HLS and DASH do not go through the segmented engine — a playlist is not a file with byte
ranges, it is hundreds of small segments needing stitching and remuxing. `StreamDownloader`
drives ffmpeg, which already handles discontinuities, variant selection and AAC ADTS-to-ASC
conversion correctly. Progress comes from ffmpeg's `-progress pipe:1`, reported as position
through the timeline (`ProgressSnapshot.FractionOverride`) since a stream has no known size.

The grabber opens on a **From browser** tab when the extension has reported anything
(`POST /media` → `MediaFeed`, newest first, capped at 100, in-memory only); the page
scraper stays one tab over.

## Progress window

`DownloadProgressWindow` mirrors one `DownloadItem` and nothing else: the item stays the
source of truth, so closing the window has no effect on the transfer. It is **off by
default** since every row shows speed and time left; when turned on it opens for single
downloads only — a batch opening one window per file would bury the screen — and it is
always reachable from a row's context menu.

A finished download is announced with the small completion window (Open, Show in folder,
Close), unless it is turned off in Settings.

Note that `Window.Title` and the `TitleBar` control's `Title` are separate. Setting only the
former updates the taskbar entry while the visible caption never changes.

## Capture rules and unknown file types

The allowlist has one deliberate hole: **a URL with no extension is captured, not skipped.**
API-style download endpoints look exactly like that —
`civitai.com/api/download/models/3289643?fileId=3174160` has no extension anywhere — and
they are usually the ones worth taking over. The app resolves the real name from
`Content-Disposition` once it probes, so nothing is lost by letting them through.

Treating unknown as "skip" silently stopped those downloads being captured at all, which
looked like the extension being broken rather than a rule doing its job.

## Sign-in walls

A download link that answers with `text/html` has almost always bounced to a sign-in page.
Most model hosts do this when the session cookie is missing — the civitai link above
redirects to `auth.civitai.com/login` and returns 200 with no `Content-Disposition`.

Saving that HTML under the file's name is worse than failing, because it looks like it
worked until the file is opened. `GuardAgainstSignInPage` fails the download with an
explanation and points at the extension, which forwards the cookies that make it work.

## Concurrency

`DownloadService.MaxConcurrent` caps transfers at 4. A batch of 100 files at 8 connections
each would otherwise open 800 sockets to one host, which is how you get rate limited rather
than how you go faster. Everything above the cap sits in `Queued` and starts as slots free.

## Settings

`%LOCALAPPDATA%\Stretto\settings.json`, exposed through eight sections: General,
Connections, Proxy, Browser, Capture rules, Passwords, System and Advanced. These are
grouped into pages by the job the user came to do. Components & about summarizes the
product details; Help is in the sidebar/F1 and About Stretto is in More options.

The dialog is built in code (`Dialogs/SettingsDialog.cs`) from three shapes in
`Dialogs/FormKit.cs`: a card, a row (name and hint left, control right) and a stacked row
(full-width control below). Category and login editing open small windows of their own
(`RowEditorForms.cs`) rather than unfolding a panel under the table.

Most of these were hardcoded constants, so exposing them handed over control of behaviour
that already existed rather than adding anything: maximum simultaneous downloads (was
`MaxConcurrent = 4`), segments per download, connection timeout (was 30s on the handler),
retries per segment (was 5), and the download folder.

Two that are new behaviour:

- **Overwrite existing file.** Off by default, so a clashing name becomes `name (2).ext`.
  A part-finished download is resumed rather than renamed, because its `.stretto` sidecar
  proves those bytes belong to this transfer rather than to someone else's file.
- **Gentle connections.** An optional network setting caps new, unknown hosts at four
  parallel ranges while preserving explicit per-host limits. This is useful when a host
  rejects or throttles extra range requests; known hosts can still use the configured count.
- **Keep awake / when everything finishes.** `SystemPolicy` takes the count of running
  transfers on the UI tick and derives everything from that one number. The finish actions
  fire on the falling edge with a latch, so a queue that drains and then gets one more
  download cannot shut the machine down twice. Shutdown goes through `shutdown /s /t 60`
  so Windows shows its own cancellable countdown.

Changing the network settings rebuilds the HTTP stack, because the connect timeout lives on
the handler and cannot be changed in place. In-flight downloads keep the old client until
they finish; new settings are for the next transfer, not a reason to interrupt a running one.

**No temporary folder setting for segmented downloads.** Stretto writes positionally into the final file, so there
is no temp stage to point anywhere — the setting would be a lie.

**The app owns the capture rules**: which file types are taken over, which
media types are offered, the minimum media size and the excluded hosts all live in Browser
monitoring. The extension fetches them from `GET /rules` on startup and every five minutes,
and caches the last answer so it still behaves while the app is closed.

The alternative, keeping them in the extension's own options, -- means two copies that can
disagree, and no way to see the rules without opening the browser. Fetching is one endpoint
and removes the question entirely.

The list is an **allowlist**: only listed types are taken over. An empty list
means the rules have never been fetched, which is treated as capture-everything rather than
capture-nothing, because failing closed would silently stop the extension working.

## Remaining product gaps

- **Browser distribution and validation.** Chromium's unpacked build is tested in Brave;
  broader browser verification and store distribution remain. Firefox code is built and
  packaged, but regular Firefox needs Mozilla signing for a permanent install.
- **Advanced proxy coverage.** System and manual HTTP/SOCKS5 proxies, per-download
  overrides, and Windows account authentication are present. Explicit PAC controls are
  not exposed; system mode follows the Windows proxy configuration.
- **Media-format coverage.** HLS and DASH use ffmpeg; site extraction can use yt-dlp.
  Adobe HDS is not implemented.

Site credentials are stored using Windows protection and editable under Settings →
Passwords. Site credentials answer Basic and Digest HTTP challenges and are scoped to the
site origin. Per-download proxy overrides offer global settings, no proxy, or a manual proxy;
the override persists without its password and is visible in Properties. Per-category
folders are configured in Settings → General.

FTP is a separate protocol path rather than part of the HTTP segmented accelerator.

## Setup guide

`BrowserSetupWindow` is three steps in one column. Step one launches the chosen browser
straight onto its extensions page (`brave.exe brave://extensions`, found through the
registry's App Paths), because a web link cannot open a browser's internal pages. Step two
is a real folder drag plus the Load unpacked path. Step three watches
`HandoffListener.LastContact` and says Connected the moment the extension reaches the app.

## Batch from the browser

The extension's "links on page" and "images on page" send one `POST /batch` rather than a
`/add` per link. With "Ask first" on, the app shows them in the Batch window's Links tab, so
a gallery is one list to confirm, not a New Download window per image. Cookies travel keyed
by host. An older app without the route answers 404 and the extension falls back to `/add`.

## UI notes

Every secondary window shares one shell (`UtilityWindow`, `WindowAppearance`): Mica, the
title bar with the app icon, no minimise or maximise on dialogs, and a footer bar with the
primary action first and the way out last. Tabs inside a window are a `SelectorBar`; Pivot
headers are sized for a page and shouted over small forms.

The accent needs the `SystemAccentColor*` ramp overridden as well as the `Accent*Brush`
keys: several templates read the colours directly, and without them primary buttons were the
Windows cyan rather than the family blue.


Follows the `Transient` house pattern: `MicaBackdrop BaseAlt`, the WinAppSDK `TitleBar`
control with its pane toggle, a `NavigationView` for the three workspaces, and
code-behind split into partial classes by concern.

**Family accent, tonal dark.** `#3CA0FF` and `MicaBackdrop Kind="BaseAlt"`, shared with
the other Plexus apps. WinUI accent overrides must go in `ResourceDictionary.ThemeDictionaries`,
not at the dictionary root: `AccentButtonStyle` reaches for them with `ThemeResource`, which
resolves theme dictionaries first and otherwise keeps finding the Windows system accent,
leaving every primary button the wrong blue.

**Dialog handlers need an init guard.** `ComboBox` and `Pivot` raise `SelectionChanged`
during `InitializeComponent()`, before the rest of the named fields are assigned, so a
handler touching a sibling control gets a `NullReferenceException`. Both dialogs set a
`_ready` flag at the end of the constructor and bail out until then. Related: enablement set
in a `ContentDialog` constructor does not survive to the visible dialog, because the buttons
are built when it opens; set it again from `Opened`.

**Icons live in one file.** `Assets/Icons.xaml` maps every glyph in the app. Today those are
Segoe Fluent Icons codepoints — ships with Windows 11, nothing to redistribute or license,
consistent with the other Plexus apps. Moving the whole app to Phosphor means editing that
one file plus a `FontFamily`, not hunting through markup.

**Watch out for `--` in XAML comments.** It is illegal XML inside a comment, and
`XamlCompiler.exe` fails on it with exit code 1 and *no error message at all* — the build
just says the command failed. Bisect against a known-good sibling app to confirm it is
the markup rather than the toolchain; use an em dash instead.

Debug-only launch flags for screenshotting the UI without clicking:
`--seed=<url>` queues a download at startup, `--filter=<view>` opens on a given tab, and
`--show=<newmenu|new|capture|advanced|batch|batchlinks|video|video-empty|queue|progress|properties|complete|setup|setup-load|setup-connect|settings|settings-network|settings-proxy|settings-browser|settings-rules|settings-passwords|settings-system|settings-advanced>`
opens a dialog or flyout on launch.
`tools/screenshot.ps1` captures the window by DWM frame bounds, with the pointer parked
off-window so hover states do not masquerade as selection.

Headless checks, all through `Stretto.Check`:

```bash
stretto <url> [-n 8] [--benchmark]      # segmented download, or 1-vs-N speed comparison
stretto batch <pattern> numbers 1 20    # wildcard expansion, including the rejections
stretto media <pageUrl>                 # what the page scraper finds
stretto stream <playlistUrl>            # HLS/DASH through ffmpeg
node tools/handoff-test.js [url]        # the browser endpoint, refusals included
```

## Background behaviour

- **Automatic retry.** A failed download is retried after 30 s, 2 min and 10 min
  (`DownloadService.ScheduleRetry`), but only for failures that can fix themselves: timeouts,
  dropped connections, 5xx. A 4xx or a sign-in page fails the same way every time, so those
  wait for a person. Starting a download by hand resets the count.
- **Complete ages out, categories do not.** Complete lists downloads finished in the last
  `KeepInCompleteDays` (30 by default, 0 for forever). Older ones stay under their file-type
  category. Nothing is deleted. `CompletedAt` is persisted; older lists fall back to the add time.
- **Resume on start** is opt-in (System settings), since silently resuming is a surprise.
- **Tray speed limit.** The tray menu has a Speed limit submenu with presets.
- **Hidden means idle.** In the tray, the list stops re-syncing; the status count keeps
  running because keep-awake and the finish actions depend on it.
- **Settings migrate once.** `SettingsVersion` moves an existing file onto changed defaults
  one time (v2: media notices off, gentle connections on); after that, your choice stands.
