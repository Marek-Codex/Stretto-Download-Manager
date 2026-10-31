# Store submission preparation

The GitHub alpha is public. The Store app remains reserved/unpublished; this is
a prepared submission worksheet, not a certification or account-verification result.

## Public project links

- Homepage: https://marek-codex.github.io/Stretto-Download-Manager/
- Privacy: https://marek-codex.github.io/Stretto-Download-Manager/privacy.html
- Support: https://marek-codex.github.io/Stretto-Download-Manager/support.html
- Source: https://github.com/Marek-Codex/Stretto-Download-Manager
- Preview: https://github.com/Marek-Codex/Stretto-Download-Manager/releases/tag/v0.1.0-alpha.1

## Listing copy to review

**Name:** Stretto Download Manager

**Short description:** Resumable downloads, adaptive connections, queues and a browser companion for Windows.

**Description:** Stretto helps manage file downloads with pause/resume, adaptive
HTTP connections, queues, schedules, speed controls and checksum checks. Its
Chromium and Firefox companion extensions can hand accepted downloads and
detected media to the app. Optional ffmpeg and yt-dlp components add supported
media extraction and conversion; ordinary downloads need neither.

The current free version has no paid feature activation. Stretto's original
code uses Apache-2.0, including personal and commercial use. Third-party tools
and runtime components retain their own licenses.

**Limitations to retain:** Requires Windows x64. Some form submissions, one-use
links and browser-bound sessions must remain in the browser. Regular Firefox's
unsigned temporary extension disappears on restart. Supported media depends on
the source and optional tools; Stretto does not bypass DRM or access permissions.

## Still required before submission

1. Confirm any remaining account-verification requirements before certification.
2. Validate the Store package using the observed identity and
   test installation/activation with the relevant packaging workflow.
3. Run the Windows App Certification Kit, then complete the broader acceptance
   checks in RELEASING.md. The clean Sandbox smoke result does not replace them.
4. Capture real app screenshots, complete accessibility/age-rating/pricing and
   listing fields, and provide certification notes for optional tools and browser setup.
5. Review the complete submission before publishing. Do not claim Store availability
   on the homepage until it is confirmed.
## Live preparation — October 30, 2026

Partner Center opened the reserved product successfully. Submission 1 was created
as an editable draft; it has not been submitted for certification.

Observed Product identity values:

- Name: `MarekCodex.StrettoDownloadManager`
- Publisher: `CN=BF937EA2-16E2-41D1-BEC3-5DF343330C30`
- PublisherDisplayName: `Marek Codex`
- Package family: `MarekCodex.StrettoDownloadManager_bt7pnfxx6vfq8`

A fresh unsigned Store MSIX was built with these values. The manifest identity,
resource index and bundled license were inspected inside the ZIP package.
SHA-256: `35a949152e934942635c0e866dcfffe637ebecec54f1e56aefa1dbb32eeee88e`.
The published GitHub test-signed assets were not replaced.

The draft Properties section was saved with Utilities + tools / File managers,
the public homepage, support URL and privacy policy. Personal-information use
is declared because downloads and browser handoff may process user-provided URLs,
authentication information and local file paths; no analytics claim is implied.
Accessibility certification was not claimed.

The Windows App Certification Kit was not found on PATH or in the standard
Windows Kits directory. Its tests remain outstanding; package creation and
Partner Center upload validation do not replace them.

The initial upload rejected the manifest display name `Stretto` because the
reserved name is `Stretto Download Manager`. Both package and visual display
names were corrected to the reserved name, and the Store package was rebuilt.

The corrected package passed Partner Center's initial package validation and was
saved in Submission 1 for Windows Desktop x64. The remaining upload warning
is the restricted `runFullTrust` capability, which needs certification approval.
This is upload acceptance, not certification or proof of runtime compatibility.

English listing description, short description, initial feature, developer and
copyright fields were saved. Screenshots are still required. Submission options
were saved with manual publishing hold and a sub-500-character runFullTrust
justification; the overview marked this section Complete.

Pricing was configured at USD 0 for the default worldwide market group and
saved as a draft. The IARC form states that
Microsoft shares the publisher display name and account email with IARC.

After explicit approval of the IARC terms and age-of-majority declaration, the
ratings were saved on October 30, 2026: ESRB Everyone, PEGI/Microsoft/IARC 3+,
and all-ages equivalents for the other displayed systems. The saved summary
showed Current Rating ID Pending. This does not submit the app for certification.
## Reviewed build and screenshots

The category and live-details changes were reviewed and committed as
`3bef961331bd3bb4659da1f11d42bbbe72192967`. Release regressions passed all 21
checks, including removed/reassigned category types and custom-category persistence.
The user also reported that the installed build looked good; this is a visual
acceptance report, not a substitute for browser or accessibility tests.

Five app screenshots (1426 by 893 PNG) were inspected and saved to the English
Store listing: Library, live transfer details, Add download, checksum completion,
and download settings. Captions describe example files and avoid promising the
sample transfer speed. The Store listing section then showed Complete.

A new MSIX was built from the reviewed source with the reserved Store identity.
SHA-256: `5df4abe67e01d74f738cabe2173695da8a1820e503e25ddd0f383e1800518203`.
Its engine manifest records the reviewed commit, sourceModified false and source
SHA-256 `59bfc6ddc92a6a43b859044900ed199c6a5cd7aec96cd318a520a0daeb71e8e3`.

Partner Center rejected replacing the saved 0.1.0.0 package with different
contents under the same full package name. The app/Store candidate was therefore
advanced to 0.1.1 (package 0.1.1.0) in commit 3448651. The public GitHub alpha
remains unchanged. The preceding 0.1.0.0 hash identifies the rejected candidate,
not a submitted or certified Store package.

The versioned Store candidate SHA-256 is
`1b52365c66764a745a675b00271fb37224c5c67451eda72ff43b004987f29ff9`.
It was built from 3448651e23d248a5df6cf6007e15188f095f1d85 with the same engine
source fingerprint as the reviewed 3bef961 build. The intervening source change
only advances the app version. Its own deployment checks remain distinct from
the test-signed development-identity package checks.

The reviewed development-identity 0.1.0.0 EXE/MSIX set passed all 11 disposable
Windows checks from 22:24:48 to 22:29:31 Eastern on October 30, 2026. Both
installed engines verified every byte of a 1 MiB fixture; window activation,
startup opt-in, same-version EXE reinstall and both removals passed. The owned
Sandbox was stopped afterward. This was not a cross-version upgrade test or
Windows App Certification Kit result.

The 0.1.1.0 upload passed initial package validation, with the expected
runFullTrust approval warning. The package page was refreshed to clear stale
upload progress controls before saving replacement of the older draft package.
