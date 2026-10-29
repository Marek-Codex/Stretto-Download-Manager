# Release checklist

The current free version is 0.1.0. The Store product **Stretto Download Manager** is
reserved as an MSIX app, product ID `9NDPLDT131GP`. Reservation is not publication.

## Repository

- Build the solution and run the engine and extension regression suites from
  [CONTRIBUTING.md](../CONTRIBUTING.md). Record optional-tool skips.
- Review the staged diff for credentials, private URLs, generated output and local paths.
- Keep the intended license explicit; the current license is all rights reserved.
- Review the [provenance assessment](PROVENANCE-REVIEW-2026-10-28.md). The available
  comparison and author's screenshot-based recollection do not confirm reused
  upstream source. Keep the limits explicit; do not claim legal clearance or
  clean-room development, and preserve any attribution actually required.
- Finalize permission to use the free binaries and account for third-party runtime
  notices in the actual installer. The current placeholder license is not that work.
- Create and connect the intended GitHub repository before enabling the app's GitHub
  release channel. Verify the release URL and actual assets, not just the repository name.
- Use focused commits describing the real changes. Do not backdate development history.

## Packages and Store submission

- Read the exact package identity, Publisher and PublisherDisplayName from Partner Center.
  Replace development defaults when building the Store package.
- Build both distribution formats using [installer/README.md](../installer/README.md).
  Rebuild after the final source change; old installers do not include later UI fixes.
- Validate signed MSIX install, upgrade, removal, startup opt-in, protocol activation,
  extension folder stability and optional tools on clean Windows accounts/VMs.
- Validate EXE install, upgrade and uninstall separately.
- Complete actual-browser tests for capture, decline/offline behavior, POST preservation,
  save dialogs, extension restarts and video detection. Document the browsers tested.
- Obtain Mozilla signing for permanent regular-Firefox installation. Until then,
  describe the unsigned XPI restrictions and temporary-install fallback accurately.
- Review each extension marketplace's media-download rules before submission.
- Host accessible privacy and support pages. Ensure listing claims match the free app.
- Complete screenshots, accessibility/keyboard and small-screen/high-DPI checks,
  Store listing, age rating, pricing and certification notes.
- Upload the final package and review every submission section before publishing.

Historical investigation notes are in [the finishing report](FINISHING-WORK-2026-10-28.md).
They describe checks already performed and their limits; this checklist remains the
release acceptance gate.
