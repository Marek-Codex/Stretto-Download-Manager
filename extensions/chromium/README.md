# Stretto browser extension

Chromium MV3. Tested target is Brave; Chrome and Edge use the same build.

## Install

1. Start Stretto.
2. Open `brave://extensions` (or `chrome://extensions`), turn on **Developer mode**, click
   **Load unpacked**, and choose this folder. Stretto's setup guide can drag it in for you.

That is the whole setup. There is no port to enter and no token to copy: the extension finds
Stretto on loopback by itself and asks it for a token. Stretto recognises its own bundled
extension and answers silently; anything else gets a one-click Allow prompt.

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
is held in memory only and never written to disk, like the cookie. A download restored from
the list falls back to Cookie, Referer and User-Agent.

This only applies to media the extension watched go out. A link taken over from the
downloads list has no request behind it, so it gets the three it always had. That is not
an oversight on the extension's part: Chrome's downloads API does not say which request
produced a download, so there is nothing to correlate. XDM has the same limit.

Known states, rather than surprises waiting to be found:

- A row in the app's media grabber carries the headers from the *first* sighting, because
  `/media` is only posted when a sighting is new. The extension's own popup always uses
  the newest. The two disagree only for a site that rotates a header token between
  requests, which is rare — short-lived credentials usually live in the URL, which the
  URL already carries.
- The app replays these headers across redirects. .NET drops `Authorization` when a
  redirect crosses to a different host, so the sharpest case is already covered; a
  custom token header is not, and would be replayed to the redirect target.
- The service worker console logs which headers a sighting carried and what a send
  included, so the capture can be checked without a debugger.

Every call to the app carries a deadline. A missing timeout is not a slow request, it is
a permanently pending promise, and in the capture path a browser download is being held
open against it.

## Options

Just one: whether this browser's downloads are captured at all. Everything else lives in
the Stretto app under Settings → Capture rules, and the extension fetches it from
`GET /rules`. One copy of the rules rather than two that can disagree.

The rules cover file types, media offered while playing, Content-Type fragments for media
served from an address with no extension, a minimum media size, a never-capture host list
and an always-capture host list — the last being the counterpart to `SkipHosts`, for a
site whose type is deliberately off the list.

## Popup

The toolbar icon opens a popup in the app's own palette:

- **Connection line** under the name: Connected, Not paired (with a Connect link that
  brings up the app's Allow prompt), or Stretto is not running — with a **Start it** link,
  which goes through the `stretto:` scheme the app registers on every launch. XDM's error
  popup does the same with its own.
- **Switch** in the header: the same capture on/off as the options page.
- **Media on this page**, newest first: direct video and audio plus HLS/DASH playlists,
  watched through non-blocking `webRequest`. Clicking a row sends it with cookies, referrer
  and the headers captured with it; playlists go with `isStream` so the app hands them to
  ffmpeg. **Segments** reveals stream chunks, hidden by default. **Clear** forgets this
  tab's list.
- **Links on page / Images on page** send the page's download links or images to Stretto
  as one batch (`POST /batch`), which the app shows as one list to confirm.

Players that fetch in byte ranges put the range in the query string, so one video used to
be a new sighting every couple of seconds. Sightings are keyed with those parameters
stripped (`VOLATILE_PARAMS` in `background.js`), and a ranged response's total size is read
from `Content-Range`.

New non-segment sightings are also POSTed to the app's `/media` route, where they land in
the media grabber. Segments stay extension-local: dozens per page would drown the app's feed.

## The heartbeat

The toolbar has to be right about two things — is the app running, and are the rules
current — and neither can be right from a single check. XDM answers this with twelve
alarms a minute, forever, which is why its extension is always slightly warm.

This one backs off instead: every 30 seconds while the app is unreachable, so starting it
is noticed promptly, and every 5 minutes once it is up, where the download path is already
refreshing the rules on every capture anyway. Half a minute is Chrome's floor for a
repeating alarm.

