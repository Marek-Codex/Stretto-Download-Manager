# Stretto Download Manager

A Windows download manager with resumable transfers, adaptive connections, queues,
and browser integration. Built by Marek Codex as part of Plexus Apps.

**Release status:** 0.1.0 is in preparation. The Microsoft Store app name is reserved,
but the app is not published yet. Signed installation, clean-machine acceptance and
browser compatibility checks remain release requirements. See the
[release checklist](docs/RELEASING.md).

*Stretto* is the fugue term for subject entries piling up overlapping and compressed —
which is what a multi-connection download is.

## What it does

- **Downloads:** segmented HTTP/HTTPS acceleration, pause/resume that survives restarts and
  crashes, per-segment and automatic retries, single-stream fallback, FTP.
- **Browser:** Chromium and Firefox extensions that hand downloads over (and give them back
  if Stretto is closed or declines), right-click menus, links/images-on-page batch capture,
  cookie forwarding for signed-in downloads, pairing so web pages cannot queue downloads.
- **Video:** media detected while browsing, page scraping, HLS/DASH via ffmpeg, site
  extraction via optional yt-dlp with format choice, MP3 / MP4 / remux conversion.
- **Organizing:** Transfers grouped by stage, a Library grouped by date with file-type
  filters and sorting, an inline details pane with a checksum check (MD5, SHA-1, SHA-256,
  SHA-512), auto-sort into per-category folders, search, import/export of the list.
- **Scheduling:** named queues with ordering and timetables, add paused, a concurrency
  cap, a global speed limit (also from the tray).
- **Intake:** new-download dialog, clipboard monitoring, pattern batches and pasted link
  lists, Copy as cURL.
- **Network:** system, manual HTTP or SOCKS5 proxy with per-download overrides, saved site
  logins (Basic/Digest), Windows account auth (NTLM/Kerberos), and connections that ramp up
  on their own while the speed keeps improving, backing off if a site refuses extras.
- **System:** keep-awake, finish actions including a cancellable shutdown, start with
  Windows / minimized, tray, taskbar progress, antivirus scan after download.

Windows only, by decision. Torrents are out of scope.

## Getting started

For an end-user installation, see [installing and using Stretto](docs/USER-GUIDE.md). See the [privacy notice](PRIVACY.md) for local storage, browser permissions and optional network requests.

Running Stretto requires Windows 10 1809 or later (x64). Building from source requires
Windows, the .NET 10 SDK and the Windows build tools described in
[CONTRIBUTING.md](CONTRIBUTING.md). Node.js is needed for the local test server.

```bash
dotnet build Stretto.slnx -p:Platform=x64
dotnet run --project Stretto.App
```

The browser extension ships beside the app, in `extensions/` of the build output. Open
**Browser** at the bottom of Stretto's navigation pane, then **Set up the extension**, and it
walks you through loading it; pairing
with the app is automatic. See [extensions/README.md](extensions/README.md) for browser
support and Firefox signing.

Optional: ffmpeg (streams and conversion) and yt-dlp (video sites) can be installed from
Settings → Components & about. Plain downloads need neither.

## Project layout

Windows distribution builds: see [installer/README.md](installer/README.md) for the self-contained NSIS EXE and MSIX packaging script, signing, and release acceptance checks.

| Folder | What it is |
|---|---|
| `Stretto.Core` | The download engine: segmented HTTP, FTP, streams, the browser listener. No UI dependencies. |
| `Stretto.App` | The WinUI 3 desktop app. |
| `Stretto.Tests` | Engine tests, run against a real local server. |
| `Stretto.Check` | A command-line harness for trying the engine by hand. |
| `extensions/` | The Chromium and Firefox extensions; see [extensions/README.md](extensions/README.md). |
| `tools/` | The deterministic test server and helper scripts; see [tools/README.md](tools/README.md). |
| `docs/` | Architecture notes and research. |

## Testing

```bash
dotnet run --project Stretto.Tests
node tools/capture-test.js
node tools/media-test.js
node extensions/naming-check.js
```

The suite downloads from `tools/testserver.js` and checks every byte of the result, since
the failures worth catching are a segment written at the wrong offset or a resume that
appended to a file it should have restarted.

### Checking by hand

`tools/testserver.js` generates its body rather than storing it — byte at offset `i` is
`i % 251` — so `tools/verify.js` can detect a misplaced segment write at any offset, which a
hash comparison would only tell you happened somewhere.

```bash
node tools/testserver.js --port 8099 --size 33554432 [--flaky 0.5] [--no-range] [--throttle 500000] [--max-concurrent 4]
dotnet run --project Stretto.Check -- http://localhost:8099/f.bin -o ./out -n 8
node tools/verify.js ./out/stretto-test.bin 33554432
```

Cases covered so far: clean multi-connection, 50% of responses dying mid-stream, server
refusing Range (single-stream fallback), and SIGKILL mid-download followed by resume. All
produce byte-perfect output.


## Why it is fast

Not threads. The engine opens N concurrent HTTP connections to one file, each requesting a
different byte range. These are async socket reads, so a 16-connection download occupies
approximately no CPU. The speedup comes from two network realities:

1. **Per-connection throttling.** Many hosts and CDNs cap throughput per socket, not per
   client. Eight sockets gets eight times the cap.
2. **TCP congestion control.** A single stream ramps slowly and halves its window on every
   packet loss. Several independent streams recover separately and collectively keep the
   pipe full. On a high-latency link this dominates.

How many connections is decided by measuring, not guessing. A download opens with four,
then every two seconds adds a batch and keeps going only while the total speed rises. When
a step no longer helps, the line or the server's limit is full and it stops. A connection a
site turns away hands its range back and ends the growth, so single-use links still finish.
Where a host throttles each connection, that can be the difference between a few MB/s and
the full speed of the line.

An unhelpful step now retires its extra connections and queues their remaining bytes
for the workers that stay. HTTP file downloads to the same hostname share one connection
budget, including probes and downloads using different login/proxy clients. Rate-limit
responses pause new host requests for `Retry-After` (or a short cooldown if absent) and
temporarily reduce the host's concurrency. Resuming a file also respects the current
connection setting, regardless of how many ranges its saved state contains.

## Further reading

- [Building and contributing](CONTRIBUTING.md)
- [Release checklist](docs/RELEASING.md)
- [Architecture and design notes](docs/ARCHITECTURE.md): how each part works and why
- [XDM gap audit](docs/XDM-GAP-AUDIT.md) and [XDM research](docs/XDM-RESEARCH.md)
- [UI identity audit](docs/UI-IDENTITY-AUDIT.md): which parts of the interface are just a download manager, which are just WinUI, and which read as XDM in WinUI 3

## License

All rights reserved for now; see [LICENSE](LICENSE). The app depends on the Windows App SDK and one Microsoft cryptography package, which keep their own licenses.
