# Stretto pre-push review — October 28, 2026

Scope: Stretto's source repository, README presentation, release configuration,
build/regression checks, documentation links and publication hygiene. The two
Twitch repositories were writing references, not targets of this review.

## Changes from this pass

- Added accurate platform/framework/license/pre-release badges and an anime-style
  Moe Counter to the main README. The shared Plexus writing guide now records
  this header convention. The counter is labeled as image loads, not unique users.
- Disabled the standalone GitHub update channel until a real public release is
  configured. The previous enabled destination produced 404 update checks.
  MSIX updates continue to belong to the installation source.
- Resolved each CI action tag against its official Git repository and pinned the
  workflow to those immutable revisions.
- Fixed three broken relative links in the comparison audit.
- Corrected helper-tool comments: the Stretto-managed executable takes precedence
  over PATH, and an upstream Windows binary cannot be described as having no
  license obligations merely because of the main project's source license.
- Added ignore rules for local environment files, key files and logs.

## Verified locally

- Release x64 solution build: zero warnings and errors.
- All 51 engine tests passed, including byte correctness, recovery, host connection
  budgets, media fixtures, final output identity and proxy-password protection.
- Browser capture, media detection and naming/handoff acceptance scripts passed.
- Tracked JSON/XML files parse; packaging PowerShell syntax parses.
- All tracked Markdown relative file links resolve.
- No unwanted model attribution/co-author trailers in reachable commit messages
  or tracked source; no personal absolute build paths in tracked text.
- Basic private-key/token-pattern and generated-output checks found no matches
  in the intended source publication set. This is not an exhaustive security audit.

## Limits and release work

- GitHub Actions has not run on a hosted runner. A local pass does not establish
  that the hosted environment will pass. Media checks may skip if its optional
  tools are absent.
- Direct requests to the README image hosts returned HTTP 403 in this environment.
  The URLs use their documented formats, but image rendering remains unverified.
- This pass does not supersede the final visual/browser acceptance or clean-machine
  signed-installation checks in [RELEASING.md](RELEASING.md).
- The Store product remains a draft; exact Partner Center package identity and
  publisher values must still be applied. Existing installer artifacts predate
  the final source changes and need rebuilding for distribution.
- No Git remote, GitHub repository, public release or Store submission was created
  or pushed in this pass. The current all-rights-reserved license remains unchanged.

The repository can be published as pre-release source with these limits stated.
That is a different milestone from shipping a finished, signed installer.
