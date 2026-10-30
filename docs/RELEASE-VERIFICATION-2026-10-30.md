# Preview verification — October 30, 2026

## Passed

- Release solution build: zero warnings and zero errors.
- All 70 local engine, release-regression and connection-policy checks passed.
- Chromium capture, detected-media, popup and naming/handoff scripts passed.
- Current tracked/untracked publication candidates: 189 text/binary candidates
  inventoried during the signature scan; no private-key, GitHub-token, AWS-key or
  hard-coded personal-path signatures were found in readable text. The secret
  signatures also had no matches in inspected local history. This was not an
  exhaustive secret-detection guarantee. AWS was a credential pattern, not an
  application dependency.
- Local history contained zero automated co-author trailers at the publication check.
- Fresh EXE/MSIX packages built and were signed with the existing self-signed
  testing certificate. Windows signature verification returned the expected
  untrusted-root result, not a hash mismatch. No host trust store was changed.
- Package payload included LICENSE, NOTICE, a dependency inventory and 23
  upstream license/notice/metadata files, including the Windows SDK .NET runtime
  package's linked SDK license. Optional ffmpeg/yt-dlp were not bundled.
- Each of the five uploaded draft assets had a GitHub SHA-256 digest matching
  the corresponding local file.

## Disposable clean Windows package checks

The final successful guest run started at 20:16:06 UTC and finished at 20:20:48 UTC.
The host runner stopped its own Sandbox afterward. Networking and clipboard
redirection were disabled; input was a read-only package/test folder, and a separate
temporary folder received results. Signing-certificate trust was added only inside
the disposable guest for MSIX installation.

Both EXE and MSIX passed installation, window presentation, a 1 MiB HTTP fixture
download through their installed Stretto.Core.dll with four requested connections,
verification of every downloaded byte, and uninstall. The test used a self-contained
headless harness, so no developer SDK or Node installation was required in the guest.
This exercises the shipped engine separately from UI intake; it is not a browser
pairing or click-through download acceptance test.

EXE startup remained disabled. A second installation of the same EXE version
succeeded. This is a reinstall check, not evidence of a cross-version upgrade.

## Issues discovered and corrected

The first MSIX activated but failed loading WinUI theme resources. Manual packaging
had omitted the package-level resources.pri. Packaging now re-indexes the already
merged Stretto.App.pri under the package identity and verifies theme/main-window
entries before packing. Re-indexing every separate PRI produced conflicting entries
and was not retained. The corrected package presented a window in the guest.

The smoke harness also initially misread a successful child process as failure
because its detached Process wrapper returned a null exit code in Windows
PowerShell 5.1. Using Start-Process -Wait preserved the exit code. The host connection
was made asynchronous so the runner could poll results and enforce its timeout.
Both corrections are in the checked-in reusable harness.

The package engine manifest records source commit dfea5575f99c79cfca38f9d54868c638397317a8,
an unmodified engine source tree, and the shipped assembly/source SHA-256 values.
Subsequent harness and documentation changes do not change the shipped runtime code.

## Still open for stable/Store acceptance

- Real Chrome/Edge/Firefox capture, decline/offline, POST, worker restart and media checks.
- Browser pairing, revoked pairing and end-to-end UI intake on clean Windows.
- Cross-version upgrades, minimum supported OS versions and broader account/device testing.
- Accessibility, keyboard, high-DPI and small-screen acceptance.
- Final Store identity, listing, screenshots, certification checks and submission.
- Mozilla signing for permanent regular-Firefox installation.

These results support a labelled alpha preview, not a stable-release or Store
certification claim. The standalone GitHub updater remains disabled.
