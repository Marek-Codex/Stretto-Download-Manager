# Free version release review — 2026-10-28

For the completed fixes and current verification/remaining gates, see [finishing work](FINISHING-WORK-2026-10-28.md). Findings below describe the earlier review state.

Verdict: review and push the source changes, but hold a public downloadable release until packaging and browser acceptance checks are complete. No paid-tier implementation is needed for this free release.

Packaging follow-up: `installer/build-packages.ps1` now implements self-contained NSIS EXE and MSIX builds, with MSIX-aware startup, protocol registration and stable browser-extension paths. The packaging gap below records the original review state. Signing identity and clean-machine install/upgrade/uninstall acceptance remain release gates; see `installer/README.md`.

Publish smoke testing caught omitted loose WinUI XBF layouts and the app's PRI resource index. These are now included explicitly, and the packaging script requires them. The complete published payload opened the main window and browser wizard successfully. The wizard's first step now opens the selected browser with one distinct action and copies its setup address with another; it checks extension-file readiness and explains preparation before loading. Source/path scanning found no runtime dependency on the developer's drive, workspace, Windows account name or SID. Publisher/copyright/repository identifiers remain intentional product metadata; test-only sample paths are fixtures.

## Verified

- Debug and Release x64 builds succeeded with zero warnings and errors.
- All 46 engine tests passed, including HTTP/FTP byte correctness, pause/resume, collisions and file ownership, HLS audio, persistence, throttling and host connection budgets. ffmpeg/ffprobe were available; media tests ran.
- `node tools/media-test.js` and `node extensions/naming-check.js` passed.
- NuGet's direct/transitive vulnerable-package check reported no vulnerable packages from nuget.org. This is not a full security audit.
- App first launch, main window and New download were inspected live at the current desktop scale.
- A local 100 MB app download survived Pause/Resume and passed byte-for-byte verification after completion. Temporary test downloads and their list entries were removed, and the test server/app processes started for this review were stopped.
- Handoff endpoint checks returned 403 for web origins, 401 for missing/wrong tokens, 400 for malformed JSON/unsupported URLs, and 404 for an unknown route. Originless requests with a valid token are intentionally accepted; the helper's old label incorrectly calls that refusal-worthy.

## Release gates

1. **Distributable:** no installer definition, publish script or CI release workflow exists in this checkout. The Release runtimeconfig requires `Microsoft.NETCore.App` 10.0; bundling Windows App SDK does not itself bundle .NET. Publish self-contained or explicitly install/check the runtime. Build output is not yet a tested end-user package. Produce one versioned artifact; test it without development SDKs, including first launch, upgrade, startup/tray behavior, resume and uninstall. NSIS being installed only supplies the compiler.
2. **Git publishing:** this repository has no configured remote. Many existing edits and new engine/test files are uncommitted. Review and include the new source files before committing; do not publish ignored `internal/` research/build output.
3. **Chromium POST capture:** `capture-chromium.js` cancels the original download before asking the app and reissues declined downloads as a GET. It cannot reproduce form bodies or one-use responses. Leave such requests with the browser before cancellation; add browser tests for POST, signed/one-use URLs and decline/offline fallback. This is an existing documented limitation.
4. **Browser distribution:** repository docs report Brave testing and incomplete compatibility passes elsewhere. Verify Chrome/Edge/Brave handoff, pairing, disabled capture, rule changes, cookies, fallback and service-worker restart. Verify Firefox separately; complete signing/store packaging before claiming permanent ordinary-Firefox installation.
5. **End-user documentation:** provide install/uninstall instructions and explain broad extension permissions, cookie/header forwarding to the local app, local storage, optional component downloads and update requests. Source README currently emphasizes building.
6. **Desktop acceptance:** check 100/150/200% scaling, small work areas, long names/paths, keyboard-only operation, failure/retry and active-download exit. Current desktop inspection does not establish all-scale or clean-machine readiness.

7. **yt-dlp output identity:** `YtDlp.DownloadAsync` falls back to the newest file anywhere in the destination directory when stdout parsing does not identify the result. That can report an unrelated existing file as this download. Replace the guess with machine-readable final-path output (including post-processing), validate that exact path and test already-downloaded files and concurrent downloads. Source-inspection finding; not reproduced against a live video site.

## Download-window assessment

- **New:** visually consistent and compact, but wide and shallow. Keep the required Link first. The next useful addition is an asynchronous source summary (host, known size/type, final filename and resolved destination), with unknown/loading states. Do not block entry on a successful probe or add more settings to this form.
- **Active:** percentage, speed, ETA and destination cover the basics. The meaningful omission was error context. This review adds selectable failure text and a Retry label, content-based initial sizing and scrollable content when resized smaller. Inspected live during a local 100 MB transfer, including Pause and Resume states. Failure rendering still needs a live error-case acceptance check.
- **Complete:** this review adds a success headline and allows the filename panel to use available width. Open, Open folder and Close remain. Full destination is available in a tooltip. Verified live after a local 1 MB download; every byte of the downloaded file passed `tools/verify.js`. Checksum verification already exists in the main details pane.

## Build environment

Verified .NET SDK 10.0.403, Node.js, ffmpeg/ffprobe, Visual Studio Community 2026 Insiders and NSIS 3.13. Stretto builds with the installed toolchain; no extra workload was required by these builds. Visual Studio discovery must include `vswhere -prerelease`.

Git was absent and was installed with WinGet. Git's `cmd` directory and NSIS were added to the user PATH. Restart terminal/Codex to inherit PATH; no Windows reboot is required for these PATH additions. An exact safe-directory entry was added for this migrated repository because its old owner SID differs from the current account. No wildcard trust was added.

The checked startup entries did not include Git, NSIS or Visual Studio. Observed Node processes belonged to Codex/plugin tooling. SDKs and compilers occupy disk; their presence does not mean they run in the tray. Old .NET runtimes were left installed because other applications may depend on them. No startup applications or unrelated processes were disabled.

Build: `dotnet build Stretto.slnx -c Release -p:Platform=x64 --disable-build-servers`.
Tests: `dotnet run --project Stretto.Tests`.
Extension checks: `node tools/media-test.js` and `node extensions/naming-check.js`.
