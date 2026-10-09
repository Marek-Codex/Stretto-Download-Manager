# Browser extensions

One folder per browser **engine**, not per browser.

| Folder | Covers | State |
|---|---|---|
| `chromium/` | Chromium browsers | Tested in Brave; Chrome, Edge, Opera, and Vivaldi have not had a full compatibility pass |
| `firefox/` | Firefox and compatible forks | Code and packaged XPI are built; permanent installation in regular Firefox needs Mozilla signing |

Chromium browsers all take the same MV3 build unchanged, so splitting Brave from Chrome
would be duplication with nothing to justify it. Firefox is a separate folder because it
genuinely differs:

- MV3 in Firefox wants `background.scripts`, not `background.service_worker`
- it needs `browser_specific_settings.gecko.id` to be loadable
- it still supports blocking `webRequest`, so interception there can be cleaner than the
  `downloads.onDeterminingFilename` handoff Chromium uses

Shared logic is factored into Chromium's `common.js`; the app assembles it with Firefox's
manifest and capture script rather than maintaining a second copy.

Install instructions are in each folder's README.

## Layout

One codebase, two engines:

- `chromium/` holds everything shared (`common.js`, popup, options, icons) plus Chromium's
  own manifest and `capture-chromium.js`. `background.js` only `importScripts` the two.
- `firefox/` holds just Firefox's `manifest.json` (MV2) and `capture-firefox.js`.

The app build assembles `extensions/firefox` in its output from both folders and packs
`extensions/stretto-firefox.xpi`. Shared code uses `api` (`browser` in Firefox, `chrome`
otherwise); both return promises.

## Firefox

Firefox has no `downloads.onDeterminingFilename`, but it still has blocking `webRequest`
with Promise-returning listeners. `capture-firefox.js` holds a navigation's response
headers while Stretto decides: taken cancels it; declined lets the same response continue.
Form POSTs stay with the browser, since the app cannot repeat them from an address.

Because a Firefox download is a navigation, its request headers come from the shared
`onSendHeaders` tracker in `common.js` rather than from anything download-specific. That
is why the tracker also watches `main_frame` and `sub_frame` — one event per page load,
cheap next to the per-resource traffic the media watcher deliberately skips.

The popup's **Start it** link goes through the `stretto:` scheme, which the app registers
under `HKCU\Software\Classes` on every launch. Per-user, so nothing needs elevation and
removing it is one key.

Installing: Developer Edition, Nightly, ESR, LibreWolf, Waterfox, Floorp and Zen can set
`xpinstall.signatures.required` to false and install the `.xpi` permanently. Regular Firefox
accepts it only as a temporary add-on (about:debugging), which it forgets on restart. A
permanent install there needs Mozilla's free unlisted signing. The setup guide walks through
each case.

Firefox may send no Origin on the pairing request, so the add-on names itself in an
`X-Stretto-Extension` header, accepted only on an originless request and only for
`moz-extension://`. It still needs the one-time Allow prompt.
