# GitHub publishing setup

Prepared for the first source publication. This file is a plan, not a claim that
the repository or release is already live.

## Repository details

- Owner: `Marek-Codex`
- Name: `Stretto-Download-Manager`
- Visibility: public, retaining the current all-rights-reserved license
- About: `A Windows download manager with resumable transfers, adaptive connections, queues, and Chromium/Firefox integration. Built with WinUI 3 and .NET 10. Part of Plexus Apps.`
- Topics: `download-manager`, `download-accelerator`, `windows`, `winui3`, `dotnet`,
  `csharp`, `browser-extension`, `chromium`, `firefox`, `hls`, `plexus-apps`
- Website: leave empty until an actual public project/support page is available

Create an empty repository, without an automatically generated README, license or
gitignore. The local repository already has these and the real development history.
Push `main` normally; no force push is needed into an empty remote.

```powershell
git remote add origin https://github.com/Marek-Codex/Stretto-Download-Manager.git
git push -u origin main
```

Those commands require GitHub authentication with write access. Do not replace
the history with an uploaded source folder: the existing commits should travel
with the project. Confirm the destination is empty before pushing.

## First download release

- Git tag: `v0.1.0-alpha.1`
- Release title: `Stretto 0.1.0 alpha 1 — first public preview`
- Mark **pre-release**; do not mark this as a stable/latest production release.
- Use [the prepared release notes](releases/v0.1.0-alpha.1.md).
- Attach the five files from `artifacts/0.1.0.0/test-signed/`: the test-signed EXE,
  test-signed MSIX, public `Stretto-Test-Signing.cer`, `TESTING.md` and `SHA256SUMS.txt`.
- The EXE is the simpler test download. The MSIX is optional and requires the
  tester to explicitly trust the exact self-signed certificate; describe that
  requirement and publish its SHA-256 fingerprint in the release notes.
- Do not attach the unsigned MSIX, private keys, or a PFX. These test packages use
  a development identity, not the final Microsoft Store identity.
- GitHub generates source archives from the tag; do not upload `.git`, private
  backups, staging folders or app-data files as release assets.

Before creating the tag, ensure the release notes and source changes are committed,
the working tree is clean, and the asset checksums still match. The app's compiled
version is 0.1.0; the alpha tag identifies this preview release without claiming
a completed signed-installation acceptance pass.

To regenerate the test-signed set after rebuilding unsigned packages, run
`./installer/sign-test-packages.ps1`. The first run needs `-CreateCertificate`;
later runs reuse the local testing certificate. It does not change trust stores.

Keep the standalone GitHub update channel disabled during this alpha. Set it up
when the intended public release channel is ready, then rebuild and verify its
behavior. The Microsoft Store product is a separate draft, not a published app.

## After publication

Verify the public About/topics, commit history, README images and release downloads.
Watch the first hosted CI run and record any skipped optional-tool checks. Then
complete the remaining browser, visual and signed-package checks in
[RELEASING.md](RELEASING.md) before a stable release.
