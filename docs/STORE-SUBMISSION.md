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
saved as a draft. IARC completion is pending permission: its form states that
Microsoft shares the publisher display name and account email with IARC.
