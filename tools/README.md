# tools

Helpers for testing Stretto. None of them ship with the app.

| File | What it does |
|---|---|
| `testserver.js` | A deterministic HTTP server. The body is generated (byte `i` is `i % 251`), so a misplaced write is detectable at any offset. Flags: `--size`, `--throttle` (bytes/s per connection), `--no-range`, `--flaky`, `--slow-first`, `--stall-first`, `--max-concurrent` (503 beyond N parallel ranges). Run with `node tools/testserver.js --port 8099`. |
| `verify.js` | Checks a downloaded file against the test server's pattern and reports the first wrong offset. `node tools/verify.js <file> [expectedSize]`. |
| `handoff-test.js` | Exercises the browser handoff endpoint the way the extension does, and the ways a hostile page would. Run with the app open. |
| `media-test.js` | Runs shared extension media-detection and header-refresh checks without a browser. Run with `node tools/media-test.js`. |
| `capture-test.js` | Runs Chromium request-method, acceptance, decline, error, cancellation and worker-restart regressions. Run with `node tools/capture-test.js`. |
| `screenshot.ps1` | Captures the app's window to a PNG from the screen. Whatever is on top of it is captured too. |
| `capture.ps1` | Captures one window by title with `PrintWindow`, so only that window comes back, even if something is covering it. `.\tools\capture.ps1 -TitleLike "New download" -Out shot.png`. |

The automated suite (`dotnet run --project Stretto.Tests`) starts and stops `testserver.js`
itself, so it needs Node.js on the PATH but nothing else.

HLS regressions generate local fixtures with ffmpeg and inspect the results with ffprobe.
They cover MPEG-TS, fragmented MP4, byte ranges, separate audio, MP3 audio, AES-128,
and unusual segment extensions. Install ffmpeg/ffprobe on PATH to run those checks;
they are skipped when ffmpeg is unavailable. To run only the release regressions:
`dotnet run --project Stretto.Tests -- --release-regressions`.

Connection-policy regressions can be run with
`dotnet run --project Stretto.Tests -- --connection-policy`. They check shared host
budgets across independent clients, bounded resume, server retry delays, permanent
failures, scaling down after a throughput plateau, cancellation and settings changes.
