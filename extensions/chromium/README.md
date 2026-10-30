# Stretto browser extension

Chromium MV3. Tested target is Brave; Chrome and Edge use the same build.

## Install

1. Start Stretto.
2. Open `brave://extensions` (or `chrome://extensions`), turn on **Developer mode**, click
   **Load unpacked**, and choose this folder. Stretto's setup guide can drag it in for you.

That is the whole setup. There is no port to enter and no token to copy: the extension finds
Stretto on loopback by itself and asks it for a token. Choose Allow in the app once;
already-approved extensions reconnect silently. Revoking an extension requires a fresh
Allow before it can connect again, including the bundled extension.

No store listing, so no developer fee. The extension id changes if you move the folder — if
that happens you just get the Allow prompt once more.

## How it captures

Manifest V3 removed blocking `webRequest`, so the extension cannot redirect a download
before it begins. It uses `downloads.onDeterminingFilename` to hold the browser's original
download while Stretto decides whether to take it. If Stretto accepts, the extension cancels
and erases that browser entry; if Stretto is unavailable or the user declines, it releases
the original request back to the browser. This preserves downloads created by form POSTs,
which cannot safely be recreated from a URL alone.

If the app does not answer, the download is **handed back to the browser** rather than lost.

`Download with Stretto` is also on the right-click menu for links, video, audio and images,
for things the browser would open rather than download.

## Request headers

A URL is not always enough to fetch it. Sites gate video behind an `Authorization` header, a
per-session CSRF token or a signature, and a handoff carrying only Cookie and Referer gets a
401 for a file the browser itself could read. So the extension watches `onSendHeaders` as
well as `onHeadersReceived`, pairs the two by `requestId`, and replays what the browser
actually sent.

Headers that must not be replayed are dropped on the way out: Cookie (it has its own field),
Referer and User-Agent (set from their own fields), the HTTP/2 pseudo-headers, and the
`sec-*` integrity claims, which describe a connection that no longer exists. What is left
is held in memory, and only a short list of harmless ones (`Origin`, `Accept`,
`Accept-Language`, `X-Requested-With`, cache and DNT headers) is saved with the list, so a
restored download can still ask the same way. Cookies, `Authorization` and anything
token-shaped never reach the disk.

This only applies to media the extension watched go out. A link taken over from the
downloads list has no request behind it, so it gets the three it always had. That is not
an oversight on the extension's part: Chrome's downloads API does not say which request
produced a download, so correlation needs the separate request history.

Known states, rather than surprises waiting to be found:

- A row in the app's media grabber carries the headers from the *first* sighting, because
  `/media` is only posted when a sighting is new. The extension's own popup always uses
  the newest. The two disagree only for a site that rotates a header token between
  requests, which is rare — short-lived credentials usually live in the URL, which the
  URL already carries.
- The app replays these headers only within their exact origin (scheme, host and port).
  When a redirect leaves it, the session goes: cookies, `Authorization` and custom headers
  are dropped, and only ordinary request headers such as `Range` and `User-Agent` follow.
- The service worker console logs which headers a sighting carried and what a send
  included, so the capture can be checked without a debugger.

Every call to the app carries a deadline. A missing timeout is not a slow request, it is
a permanently pending promise, and in the capture path a browser download is being held
open against it.

## Settings

Browser capture, notifications and the advanced stream-fragment display preference
live in extension Settings. File-type and site rules belong to the desktop app under
Settings → Capture rules; the extension fetches them from `GET /rules`.

## Browser companion

The popup uses Stretto’s navy surfaces, blue accents and compact cards:

- **Desktop connection** shows connected, approval needed or app offline, with
  Connect, Start Stretto and a connection refresh action.
- **Capture controls** below the media list opens a second view inside the popup,
  with Back to media navigation. Its summary shows on, paused or off for this site.
  The media filter is preserved when switching views.
- **Catch browser downloads**, in Capture controls, pauses automatic capture. Manual media sends and page
  collection remain available while connected. The site switch follows the app’s
  skip list and is disabled while browser capture is paused.
- **Collect from this page** offers Links and Images. Both open a batch in Stretto
  for review before downloading.
- **Media on this page** updates while open. All, Video, Audio and Streams filters
  show counts for this tab only, newest first. Rows identify direct files and
  playlists; sending preserves the existing worker handoff with captured headers.
  Playlist downloads need ffmpeg in the desktop app.
- **Clear list** forgets this tab’s detected media after the worker confirms success.
  Stream fragments are hidden by default; enable them in Settings when needed.

Presentation decisions live in `popup.model.js`; `popup.js` binds the browser APIs
and DOM. Capture, pairing and acceptance remain owned by the existing worker.
Titles are rendered as text, and an unknown or internal tab never exposes other
tabs’ media. A live update cannot re-enable an in-flight send.

Players that fetch in byte ranges put the range in the query string, so one video used to
be a new sighting every couple of seconds. Sightings are keyed with those parameters
stripped (`VOLATILE_PARAMS` in `background.js`), and a ranged response's total size is read
from `Content-Range`.

New non-segment sightings are also POSTed to the app's `/media` route, where they land in
the media grabber. Segments stay extension-local: dozens per page would drown the app's feed.

## The heartbeat

The toolbar has to be right about two things — is the app running, and are the rules
current — and neither can be right from a single check.

This one backs off instead: every 30 seconds while the app is unreachable, so starting it
is noticed promptly, and every 5 minutes once it is up, where the download path is already
refreshing the rules on every capture anyway. Half a minute is Chrome's floor for a
repeating alarm.

