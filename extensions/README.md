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

## Chromium handoff behavior

Only requests observed as GET are eligible for automatic capture. POST and unknown/ambiguous requests remain in the browser. The browser's original request is kept until Stretto accepts, and decline/offline/error fallback releases that same request; it is never recreated by URL. This preserves form/session semantics on fallback. A browser configured to ask where to save each file may show its own Save dialog during Stretto confirmation. Use the site skip list or disable capture for one-use links that cannot be fetched again by another app.

## Firefox

Firefox has no `downloads.onDeterminingFilename`, but it still has blocking `webRequest`
with Promise-returning listeners. `capture-firefox.js` holds a navigation's response
headers while Stretto decides: taken cancels it; declined lets the same response continue.
Form POSTs stay with the browser, since the app cannot repeat them from an address.

Because a Firefox download is a navigation, its request headers come from the shared
`onSendHeaders` tracker in `common.js` rather than from anything download-specific. That
is why the tracker also watches `main_frame` and `sub_frame` — one event per page load,
cheap next to the per-resource traffic the media watcher deliberately skips.

The popup's **Start Stretto** button goes through the `stretto:` scheme, which the app registers
under `HKCU\Software\Classes` on every launch. Per-user, so nothing needs elevation and
removing it is one key.

The build includes an unsigned `stretto-firefox.xpi`. The setup wizard offers it as a
drag tile with a signing warning. Regular Firefox rejects permanent unsigned installation;
use **about:debugging → This Firefox → Load Temporary Add-on**, then select the unpacked
`manifest.json`. Firefox forgets that temporary installation on restart. Developer Edition,
Nightly and ESR can accept unsigned installation depending on their configuration;
Firefox forks have their own policies. The wizard does not change signature settings.
Permanent regular-Firefox distribution requires Mozilla signing, including for a
self-distributed XPI. See [Mozilla's distribution guidance](https://extensionworkshop.com/documentation/publish/signing-and-distribution-overview/).

Firefox may send no Origin on the pairing request, so the add-on names itself in an
`X-Stretto-Extension` header, accepted only on an originless request and only for
`moz-extension://`. It still needs the one-time Allow prompt.
