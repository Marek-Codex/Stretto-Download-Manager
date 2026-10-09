# tools

Helpers for testing Stretto. None of them ship with the app.

| File | What it does |
|---|---|
| `testserver.js` | A deterministic HTTP server. The body is generated (byte `i` is `i % 251`), so a misplaced write is detectable at any offset. Flags: `--size`, `--throttle` (bytes/s per connection), `--no-range`, `--flaky`, `--slow-first`, `--stall-first`, `--max-concurrent` (503 beyond N parallel ranges). Run with `node tools/testserver.js --port 8099`. |
| `verify.js` | Checks a downloaded file against the test server's pattern and reports the first wrong offset. `node tools/verify.js <file> [expectedSize]`. |
| `handoff-test.js` | Exercises the browser handoff endpoint the way the extension does, and the ways a hostile page would. Run with the app open. |
| `screenshot.ps1` | Captures the app's window to a PNG from the screen. Whatever is on top of it is captured too. |
| `capture.ps1` | Captures one window by title with `PrintWindow`, so only that window comes back, even if something is covering it. `.\tools\capture.ps1 -TitleLike "New download" -Out shot.png`. |

The automated suite (`dotnet run --project Stretto.Tests`) starts and stops `testserver.js`
itself, so it needs Node.js on the PATH but nothing else.
