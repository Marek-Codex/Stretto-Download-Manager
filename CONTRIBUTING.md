# Working on Stretto

Stretto Download Manager is a Windows desktop app by Marek Codex, part of Plexus Apps.
The repository currently uses an all-rights-reserved license, not an open-source license.
Read [LICENSE](LICENSE) before reusing or distributing its contents. A public repository
does not change those permissions. Discuss substantial contributions with the maintainer
before investing time in them.

## Build prerequisites

- Windows x64 and the .NET 10 SDK (`dotnet --list-sdks`).
- Visual Studio or Build Tools with MSBuild and Windows SDK support for WinUI 3.
  The app targets Windows SDK 10.0.26100; its minimum runtime OS is Windows 10 1809.
- Node.js on PATH for the engine's local HTTP test server and extension regressions.
- Optional ffmpeg and ffprobe on PATH for conversion and HLS fixture checks.
- NSIS only when producing the EXE installer. MSIX packaging uses MakeAppx;
  see [installer/README.md](installer/README.md) for tool discovery and signing.

No Docker, database, background service or developer certificate is needed for a normal
source build. Optional video tools are not needed for ordinary HTTP downloads.

```powershell
dotnet build Stretto.slnx -c Release -p:Platform=x64 --disable-build-servers
dotnet run --project Stretto.Tests -c Release --no-build
node tools/capture-test.js
node tools/media-test.js
node extensions/naming-check.js
```

The test project is a console runner: use `dotnet run`, not `dotnet test`.
Media checks explicitly skip when their optional tools are absent; a passing run with
skips is not full media acceptance. To include real yt-dlp fixture checks, set
`STRETTO_TEST_YTDLP` to an absolute path to that executable before running the tests.

Launch the app with `dotnet run --project Stretto.App -c Release`. Fully exit any
running Stretto instance, including its tray process, when switching builds. Local
development runs use the current Windows user's Stretto settings and history.

## Changes and reports

Keep changes focused on a concrete behavior and include the validation that matters.
Browser API mocks do not replace a test in the browser. UI changes need an actual
click-through, including keyboard navigation and the affected installation path.
Never commit pairing tokens, cookies, saved settings, downloaded files, logs, signing
keys or local build output. Logs can contain private URLs and paths.

For a bug, include the app/extension version, Windows/browser version, reproduction
steps, expected behavior and actual behavior. Use a public fixture URL where possible.
Remove credentials, private links, cookies and personal paths from anything you attach.
