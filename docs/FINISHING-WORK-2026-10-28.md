# Free-version finishing work — 2026-10-28

This supersedes the code findings in the earlier release review. It does not certify an untested Store submission or a signed MSIX upgrade.

## Changes

- Chromium capture leaves POST, unknown and ambiguous requests in the browser. Eligible requests retain their original browser response until the app explicitly accepts. Decline, offline, malformed app response and errors no longer cancel and reconstruct the request by URL. Cosmetic history-erasure failure cannot restart it. Recent method metadata is bounded in extension session storage and survives worker suspension.
- The tradeoff is explicit: browsers configured to ask where to save each file may show their Save dialog during Stretto confirmation. Accepted one-use/browser-bound links can still be impossible for an external downloader to fetch; use the browser/site exclusion for these. This limitation is not claimed fixed.
- yt-dlp uses its JSON-encoded `after_move` final path. It no longer guesses from human logs or picks the newest file in a shared directory. Missing, malformed, ambiguous or out-of-folder output is rejected. User CLI config is ignored for the app-owned download invocation, and percent signs in destination folders are escaped correctly.
- Saved proxy passwords use Windows per-user DPAPI protection. Legacy plaintext settings remain readable and migrate on the next save. Malformed protected data does not stop startup.
- New download shows the host and browser-provided size/type, plus an expected folder based on filename/category. It makes no extra network probe while editing. Changing hosts clears stale login state. Escape dismisses the window.
- Progress windows show selectable failure text and Retry. Completion has the success headline and flexible filename panel from the first review.
- The browser wizard has separate Open browser and Copy address actions, readiness checks and a concrete preparation step before loading.
- MSIX installations do not use the EXE's GitHub update check. Settings points packaged users to their installation source. Diagnostics no longer promise that logs contain no filenames or private URLs.
- Added a privacy notice and user guide, included in installer payloads. Extension versions are 1.4.1 for both browser engines.

## Verification

- Release x64 build succeeded without warnings/errors.
- Complete engine suite: 51 tests passed, including new real-child-process output-identity tests and Windows proxy-password protection/migration.
- The 13 focused release checks also passed with a real downloaded yt-dlp executable against a generated local MP4. A fresh download matched the source bytes, and an already-downloaded response returned the same exact path. The helper is confined to ignored verification artifacts, not installed on PATH or bundled with the app.
- Chromium capture, shared media and naming/explicit-acceptance Node regressions all passed.
- Live local handoff checks passed for web-origin rejection, missing/wrong tokens, valid originless/extension requests, malformed input, unsupported URL schemes and unknown routes. The helper now asserts HTTP statuses and does not print part of the pairing token.
- Live UI: the published app opened, New download displayed host and expected-folder updates, a local HTTP 404 appeared in the list, and its progress window displayed the actual error and Retry.
- Rebuilt both unsigned installer formats. MakeAppx validated the MSIX. ZIP inspection confirmed runtime/layout/resource-index files, documentation and matching extension versions.
- Silent per-user EXE installation on this development machine passed. Installed files, Start menu shortcut, Installed apps entry, protocol registration, app launch and the local handoff endpoint were verified. No startup entry was created. This is not a clean-machine test.
- Automatic approval review blocked the uninstall-test/test-history-cleanup command without a supplied reason. The newly installed test copy and its single local failure-test history entry remain. The preview/app and fixture-server processes were stopped. Uninstall verification remains outstanding.

## Remaining public-release gates

1. Obtain final Partner Center identity/publisher values and build the Store-specific MSIX identity. Current identity values are development defaults; artifacts are unsigned.
2. Test signed MSIX installation, startup opt-in/Windows veto, protocol activation, optional components, update and uninstall on clean Windows accounts/VMs. Test EXE upgrade/uninstall too; the local install smoke check does not replace this.
3. Run actual browser-extension acceptance in each claimed browser, including save prompts, POST, decline/offline, worker restart, signed downloads and MSIX extension-path stability after update. API-mock regressions do not establish browser compatibility. Firefox signing remains separate from Windows signing.
4. Publish an accessible privacy/support page and configure the real public source/release destination. The checkout still has no Git remote; its configured GitHub destination was not publicly available during review. No repository, release, listing or privacy page was published by this work.
5. Complete the broader DPI, small-work-area, keyboard and accessibility acceptance pass. The live UI checks cover the current display configuration only.

There is no paid-tier implementation or payment collection in this change. This report
records validation at the time of the finishing pass; consult Git for the final commit state.

## Subsequent repository and wizard pass

- Reserved **Stretto Download Manager** as a draft MSIX Store product (`9NDPLDT131GP`).
  No package or Store listing has been published. Exact package identity values still
  need to be read from Partner Center and applied to the Store build.
- Clicked through the installed wizard's three Chromium steps, Back navigation and
  Firefox preparation/loading screens at the current display size. The final source
  adds scrolling, a bounded browser dropdown, a full-width folder path and a truthful
  close label while waiting. The unsigned XPI drag option remains, with a rejection
  warning and a temporary-install fallback. The revised build passed, but a UI-tool
  attachment failure prevented visual verification of those final source changes.
- Added repository build/contribution instructions, a release checklist and a Windows
  GitHub Actions workflow. The hosted workflow has not run; no remote is configured yet.
- Re-ran all 51 engine tests and the three extension regression scripts successfully.
  Source/history text checks found no unwanted model attribution in reachable commits
  or working text. The basic private-key/token-pattern check found no matches; it is
  repository hygiene, not a full security audit.
