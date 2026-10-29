# Browser companion pass — October 28, 2026

Extension version 1.5.0 gives the popup three visible jobs: desktop connection,
page collection and detected media. It follows the app's navy palette and compact
cards, with restrained copy and Plexus Apps branding.

The controller and presentation rules are separate. Worker discovery, pairing,
request interception, headers and download acceptance retain their existing
implementation. The advanced fragment preference keeps its existing storage key
and moves into Settings. No new browser permissions were added.

Correctness fixes in this pass:

- Unknown and internal tabs cannot display another tab's media or clear its list.
- A failed worker clear request retains the visible list and reports failure.
- Media updates arrive while the popup stays open. An update cannot re-enable a
  send or collection action that is still in flight.
- Pausing automatic capture leaves manual sends available. The site switch
  explains the paused state rather than implying capture is active.
- Unknown stream containers use a generic stream badge instead of claiming HLS.

Validation:

- `node tools/popup-test.js`: controller fixtures cover tab isolation, filters,
  literal title rendering, exact send payload, declines, in-flight updates, failed
  clear, local-storage fallback and paused/offline/unpaired/internal-page states.
- Existing capture, media and naming/handoff Node regressions pass.
- Browser click-through uses the actual HTML/CSS/scripts with simulated extension
  APIs from `tools/popup-preview.js`. Checked connected, offline, approval needed,
  paused and internal-page states; audio filtering, send feedback, Settings and
  its fragment switch. Connected fixture fits 390 × 589 CSS pixels with three
  media rows. Longer lists scroll.

This is not an installed Chrome/Edge/Firefox integration test. Real pairing,
download interception and the app's batch dialog still need a loaded-extension
smoke test before calling the release complete. Source-provenance uncertainty and
distribution-license work remain tracked separately in the provenance review.
