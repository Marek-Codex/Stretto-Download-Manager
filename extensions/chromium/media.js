// Stretto browser integration: media detection.
//
// Watches the page's own network traffic for video and audio, keeps a per-tab list for the
// popup, and reports new sightings to the app's grabber. Loaded after common.js, whose
// helpers (config, callApp, extensionOf, action) it uses.

// ═══ Media detection for the popup ═══
//
// Watches real network traffic, which is the only thing that finds media on pages
// that build their video URLs at runtime. Observation only: MV3 kept non-blocking
// webRequest, so this never touches the requests themselves.

const VIDEO_EXTS = new Set(['mp4', 'webm', 'mkv', 'mov', 'm4v', 'avi', 'ogv']);
const AUDIO_EXTS = new Set(['mp3', 'm4a', 'ogg', 'oga', 'wav', 'flac', 'opus', 'weba']);
const PLAYLIST_EXTS = new Set(['m3u8', 'm3u', 'mpd']);
// Chunks of a stream, not something to download on its own. Recorded so
// "More formats" can show them, hidden by default.
const SEGMENT_EXTS = new Set(['ts', 'm4s', 'mp4v', 'cmfv', 'cmfa']);

/**
 * What a response is, or null if it is not media.
 *
 * The app's mediaTypes are consulted first and matched as a substring of the Content-Type,
 * so a site serving video from an address with no extension ("/play?id=9182") is still
 * found -- which is the whole reason the list is configurable rather than a constant.
 * Everything the list misses falls through to the extension sets below.
 */
function mediaKind(url, mime, mediaTypes) {
  const type = (mime || '').toLowerCase().split(';')[0].trim();
  const ext = extensionOf(url, '');

  if (PLAYLIST_EXTS.has(ext) || type.includes('mpegurl') || type.includes('dash+xml'))
    return 'stream';
  // Before the video/ and audio/ checks: servers label chunks video/mp4, video/mp2t or
  // video/iso.segment, and taking that at its word reported a few seconds of a stream
  // as if it were the whole video.
  if (SEGMENT_EXTS.has(ext) || type === 'video/mp2t' || type.includes('iso.segment')) return 'segment';
  if (type.startsWith('video/')) return 'video';
  if (type.startsWith('audio/')) return 'audio';
  if (VIDEO_EXTS.has(ext)) return 'video';
  if (AUDIO_EXTS.has(ext)) return 'audio';

  // Last, because a name the user has put in the list should not outrank what the response
  // plainly says it is. "video/" would make a playlist above look like a video.
  if (type && (mediaTypes || []).some((m) => m && type.includes(m.toLowerCase()))) return 'video';

  return null;
}

function guessName(url) {
  try {
    const parsed = new URL(url);
    const last = decodeURIComponent(parsed.pathname.split('/').filter(Boolean).pop() || '');
    if (last && /\.\w{2,5}$/.test(last)) return last;
    return parsed.hostname + (last ? `/${last}` : '');
  } catch {
    return url;
  }
}

// Session storage survives the service worker dozing off and clears itself when
// the browser closes, which is exactly the lifetime a detected-media list wants.
const sessionStore = () => (api.storage.session ? api.storage.session : api.storage.local);

/**
 * The app's Content-Type fragments, mirrored from the last rules fetch.
 *
 * Kept here rather than read per event because the webRequest listener is synchronous
 * and storage is not. Empty until the first fetch, which means the built-in extension
 * sets decide alone -- the same behaviour an older Stretto still gets.
 */
let currentMediaTypes = [];

async function getMedia() {
  const stored = await sessionStore().get({ media: [] });
  return Array.isArray(stored.media) ? stored.media : [];
}

async function setMedia(list) {
  await sessionStore().set({ media: list });
}

// Media responses can arrive together. Serialize the small read/modify/write so one
// response or clear operation cannot overwrite a newer list from another event.
let mediaUpdateQueue = Promise.resolve();

function updateMedia(update) {
  const result = mediaUpdateQueue.then(async () => {
    const current = await getMedia();
    const next = update(current);
    await setMedia(next);
    return next;
  });

  mediaUpdateQueue = result.then(() => undefined, () => undefined);
  return result;
}

// Query parameters that change on every request for the same media. Players that fetch
// in byte ranges (YouTube's googlevideo, most DASH players) put the range in the query
// string, so without this one video became a new "sighting" every couple of seconds --
// flooding the popup and posting to the app on each one.
const VOLATILE_PARAMS = ['range', 'rn', 'rbuf', 'ump', 'srfvp', 'cpn', 'sq', 'lmt_ts', 'playback_ts', '_'];

// ═══ The headers the browser actually sent ═══
//
// A URL is not always enough to fetch it. Sites gate video behind an Authorization header,
// a per-session CSRF token, or a signature, and a handoff carrying only Cookie and Referer
// gets a 401 for a file the browser itself could read. The request headers are the last
// piece of the browser's context still visible after the fact, so they are captured as the
// request goes out and replayed verbatim by the app.
//
// Keyed by requestId and paired with onHeadersReceived, then dropped, so this holds one
// page's worth of in-flight requests at most.

/**
 * Headers that must not be replayed.
 *
 * Cookie has its own field in the handoff. Referer and User-Agent are set from their own
 * fields and the app puts them in one dictionary, so sending them here as well would
 * collide. Host, Connection and Content-Length describe a connection that no longer exists.
 * sec-* are the browser's own integrity claims, and the ":name" HTTP/2 pseudo-headers are
 * not valid in an HTTP/1.1 request.
 */
// Range and the conditional headers describe the one request the player made: replayed,
// they asked the server for that same slice again. A site that serves a whole video as one
// file in byte ranges then handed Stretto the first few seconds and called it done.
const UNREPLAYABLE = /^(cookie|host|connection|content-length|accept-encoding|transfer-encoding|referer|user-agent|upgrade|te|range|if-range|if-match|if-none-match|if-modified-since|if-unmodified-since)$/i;

/** In-flight requests kept at once. A busy page has a few hundred, not thousands. */
const MAX_TRACKED = 1000;

const trackedRequests = new Map();

/** The replayable request headers, lowercased, one value per name. */
function replayableHeaders(details) {
  const seen = new Map();

  for (const header of details.requestHeaders || []) {
    const name = header.name.toLowerCase();
    if (name.startsWith(':') || name.startsWith('sec-') || UNREPLAYABLE.test(name)) continue;

    // Repeated headers fold into one comma-joined value, which is what an HTTP/1.1
    // request means by them anyway. The app's dictionary holds a single value per name.
    seen.set(name, seen.has(name) ? `${seen.get(name)}, ${header.value}` : header.value);
  }

  return seen.size ? Object.fromEntries(seen) : null;
}

function trackRequest(details) {
  // GET only, as the comment on the listener promises. Every API call a page makes is a
  // POST "xmlhttprequest", and none of them is media; skipping them here is most of what
  // keeps this listener cheap, and a POST's headers would describe a body nobody replays.
  if (details.method !== 'GET') return;

  const headers = replayableHeaders(details);
  if (!headers) return;

  // Oldest first, so the cap drops the requests that have been in flight longest rather
  // than the newest -- a page that fires hundreds of media requests at once should not
  // evict the one being looked at.
  if (trackedRequests.size >= MAX_TRACKED) trackedRequests.delete(trackedRequests.keys().next().value);
  trackedRequests.set(details.requestId, headers);
}

/**
 * The headers for a request, once. Deleting on the way out is deliberate: a request that
 * has answered cannot answer again, and the two consumers (the media watcher here, the
 * Firefox capture) never overlap, so whichever gets here first is the right one.
 */
function takeRequest(requestId) {
  if (!trackedRequests.size) return null;
  const headers = trackedRequests.get(requestId) || null;
  trackedRequests.delete(requestId);
  return headers;
}

function mediaKey(url) {
  try {
    const parsed = new URL(url);
    for (const name of VOLATILE_PARAMS) parsed.searchParams.delete(name);
    parsed.hash = '';
    return parsed.href;
  } catch {
    return url;
  }
}

/**
 * A name worth showing. Stream playlists are almost always called master.m3u8, index.m3u8
 * or manifest.mpd, and players' media URLs are often bare ids; the page title says what
 * the video actually is. XDM names streams this way. A real file name wins when there is one.
 */
/**
 * Whether a file name is one of the wildly random ones some servers hand out: long, with no
 * readable word in it. Also a stream playlist's stock name, or no extension at all. Strict on
 * purpose, because the page title replaces the name: "movie.mp4" and "clip01.mp4" are
 * somebody's names. The app applies the same rule (FileNaming.IsGeneric) when it downloads.
 */
function isGenericName(name) {
  if (!/\.\w{2,5}$/.test(name)) return true;

  const stem = name.replace(/\.\w{2,5}$/, '');
  if (/\.(m3u8?|mpd)$/i.test(name) && /^(master|index|playlist|manifest|chunklist|video|audio|media|stream)[\w-]*$/i.test(stem)) return true;

  const readsLikeWords = /\s/.test(stem) || /[A-Za-z]{3,}[_.-][A-Za-z]{3,}/.test(stem);
  return stem.length >= 12 && !readsLikeWords;
}

/**
 * The tab showing the page a download came from. The exact address wins, but a download
 * served from another host (a CDN, nearly always) carries only the page's origin as its
 * referrer, so an origin match is the fallback. Among matches the active tab wins: it is
 * the one the person just clicked in.
 */
async function tabForPage(pageUrl) {
  if (!pageUrl) return null;

  const parse = (address) => { try { return new URL(address); } catch { return null; } };
  const wanted = parse(pageUrl);
  if (!wanted) return null;

  const bare = (address) => { const u = parse(address); if (u) u.hash = ''; return u ? u.href : ''; };
  const pick = (tabs) => tabs.find((t) => t.active) || tabs[0] || null;

  try {
    const tabs = await api.tabs.query({});
    return pick(tabs.filter((t) => bare(t.url || '') === bare(pageUrl)))
      || pick(tabs.filter((t) => parse(t.url || '')?.origin === wanted.origin));
  } catch {
    return null;
  }
}

/** The tab in front, in the window the person was last using. */
async function activeTab() {
  try {
    return (await api.tabs.query({ active: true, lastFocusedWindow: true }))[0] || null;
  } catch {
    return null;
  }
}

/**
 * The title of the page a download or video belongs to, and a note on how it was found.
 *
 * This is all the extension says about names: it reports the title, and the app decides
 * what the file is called. The tab is the one that owns the video when the browser says
 * which, otherwise the one the referrer points at, otherwise the one in front -- a button
 * that sends no referrer was, nearly always, clicked on the page being looked at.
 */
async function pageTitleFor({ tabId, referrer }) {
  const version = api.runtime.getManifest?.().version || '?';
  let tab = null;
  let how = '';

  if (tabId >= 0) {
    try { tab = await api.tabs.get(tabId); how = 'its own tab'; } catch { /* tab gone */ }
  }
  if (!tab) { tab = await tabForPage(referrer); if (tab) how = 'the referrer'; }
  if (!tab) { tab = await activeTab(); if (tab) how = 'being in front'; }

  if (!tab) return { title: null, note: `v${version}: no tab found (referrer ${referrer ? 'given' : 'missing'})` };

  const title = cleanTitle(tab.title, tab.url);
  return {
    title: title || null,
    note: title
      ? `v${version}: title of a tab found by ${how}, ${title.length} characters`
      : `v${version}: tab found by ${how} but its title was empty (raw length ${String(tab.title || '').length})`,
  };
}

async function nameFor(item) {
  const fromUrl = guessName(item.url);
  const generic = isGenericName(fromUrl);

  // The app's choice: the file's own name always, the page title always, or (Smart, the
  // default) the title only when the address has nothing better to say.
  const nameFrom = (await config()).rules?.nameFrom || 'Smart';
  if (nameFrom === 'File') return fromUrl;
  if ((nameFrom !== 'Title' && !generic) || !(item.tabId >= 0)) return fromUrl;

  try {
    const tab = await api.tabs.get(item.tabId);
    const title = cleanTitle(tab?.title, tab?.url);
    if (!title) return fromUrl;
    const ext = extensionOf(item.url, '');
    return ext && ext !== fromUrl ? `${title}.${ext}` : title;
  } catch {
    return fromUrl;
  }
}

/**
 * A tab title as a file name: no unread-count prefix like "(3) ", no " | Site" tail, and a
 * " - Site" tail only when it names the page's own host, so "Spider-Man - Part 1" is safe.
 */
function cleanTitle(raw, pageUrl) {
  let title = String(raw || '').replace(/^\(\d+\)\s*/, '');

  let host = '';
  try { host = new URL(pageUrl).hostname.replace(/^www\./, '').split('.')[0].toLowerCase(); } catch { /* no page */ }

  const m = /^(.+)\s+(\||•|·|[-–—])\s+([^|•·\-–—]+)$/.exec(title);
  if (m) {
    const tail = m[3].toLowerCase().replace(/[^a-z0-9]/g, '');
    if (m[2] !== '-' && m[2] !== '–' && m[2] !== '—' || (host && tail.includes(host))) title = m[1];
  }

  return title.replace(/[\\/:*?"<>|]+/g, ' ').replace(/\s+/g, ' ').trim().slice(0, 120);
}

/**
 * A name is worked out when a video is first seen, and the page's title may not have
 * arrived by then. Whenever a tab's title changes, the names of its media are worked out
 * again, so the popup never keeps an address-style name once there is a title to use.
 */
async function retitleMedia(tabId) {
  const mine = (await getMedia()).filter((m) => m.tabId === tabId);
  if (!mine.length) return;

  const names = new Map();
  for (const m of mine) names.set(m.key || m.url, await nameFor(m));

  const changed = mine.filter((m) => m.kind !== 'segment' && names.get(m.key || m.url) !== m.name);

  await updateMedia((current) => current.map((m) =>
    m.tabId === tabId && names.has(m.key || m.url) ? { ...m, name: names.get(m.key || m.url) } : m));

  // The app's grabber keeps the name it was first told, so tell it the new one.
  for (const m of changed) void reportMedia({ ...m, name: names.get(m.key || m.url) });
}

api.tabs.onUpdated.addListener((tabId, change) => {
  if (change.title !== undefined) void retitleMedia(tabId);
});

async function recordMedia(item) {
  const entry = { ...item, key: mediaKey(item.url), name: await nameFor(item), seenAt: Date.now() };
  let isNew = false;
  const list = await updateMedia((current) => {
    const next = [...current];
    const existing = next.findIndex((m) => (m.key || m.url) === entry.key && m.tabId === entry.tabId);
    isNew = existing < 0;

    // Keep the first address seen for an existing key: it is the one without a range
    // tail, or at least the one the popup already shows. Only the size and time move.
    //
    // Request headers are the exception, and they take the newest value. Sites that sign
    // media rotate the token, so the header that worked on the first request is often the
    // one that has expired by the time anyone clicks -- and a sighting that arrived with
    // no headers at all should not blank out ones captured later.
    if (existing >= 0) next[existing] = {
      ...next[existing],
      size: Math.max(next[existing].size || 0, entry.size || 0),
      seenAt: entry.seenAt,
      requestHeaders: entry.requestHeaders || next[existing].requestHeaders,
    };
    else next.push(entry);

    while (next.length > 100) next.shift();
    return next;
  });

  // Segments never leave the extension: at dozens per page they would drown the
  // app's feed, and the popup already lists them under More formats.
  if (isNew && item.kind !== 'segment') void reportMedia(entry);

  const count = list.filter((m) => m.tabId === item.tabId && m.kind !== 'segment').length;
  try {
    // Tab scoped: a global colour here would turn the red "not running" mark blue.
    await action.setBadgeBackgroundColor({ tabId: item.tabId, color: '#3CA0FF' });
    await action.setBadgeText({ tabId: item.tabId, text: count > 0 ? String(count) : '' });
  } catch {
    // Badges are decoration; a dead tab must not break recording.
  }
}

// Best effort: the app being closed is the normal case, not an error.
async function reportMedia(entry) {
  try {
    const cfg = await config();

    // Deliberately does not pair: this runs for every media element seen while browsing,
    // and pairing from here would fire the app's Allow prompt at a random moment.
    if (!cfg.token || !cfg.port) return;

    const tab = entry.tabId >= 0 ? await api.tabs.get(entry.tabId).catch(() => null) : null;

    // A site on the skip list is one the user said to leave alone, so it does not feed the
    // app either. The popup still lists the media, so a video can be taken by hand.
    const skipHosts = cfg.rules?.skipHosts || [];
    const hostOf = (address) => { try { return new URL(address).hostname; } catch { return ''; } };
    if (hostSkipped(hostOf(entry.url), skipHosts) || hostSkipped(hostOf(tab?.url), skipHosts)) return;

    const cookie = await cookieHeaderFor(entry.url).catch(() => '');

    // Whether the capture actually worked is not checkable without a browser, so it says
    // so here. One line per new sighting, in the service worker console, instead of
    // needing a debugger full of breakpoints to answer "did it see any headers".
    const names = Object.keys(entry.requestHeaders || {});
    if (names.length) console.info(`Stretto: ${entry.name} carried ${names.length} header(s): ${names.join(', ')}`);

    await callApp(cfg, '/media', {
      method: 'POST',
      signal: AbortSignal.timeout(RULES_TIMEOUT_MS),
      body: JSON.stringify({
        url: entry.url,
        name: entry.name,
        pageTitle: cleanTitle(tab?.title, tab?.url) || null,
        mime: entry.mime || null,
        size: entry.size || null,
        kind: entry.kind,
        cookie: cookie || null,
        referrer: entry.documentUrl || entry.initiator || tab?.url || null,
        userAgent: navigator.userAgent,
        headers: entry.requestHeaders || null,
      }),
    });
  } catch {
    // Swallowed on purpose.
  }
}

// Only GET. A POST's headers describe a body this cannot replay, and pairing one with a
// bare GET would be worse than sending nothing.
api.webRequest.onSendHeaders.addListener(trackRequest, {
  urls: ['<all_urls>'],
  // main_frame and sub_frame are here for the Firefox build, which captures a download
  // by holding a navigation's response. It is one event per page load rather than one
  // per resource, so it costs almost nothing; the entries for ordinary navigations are
  // never claimed and age out behind the cap.
  types: ['media', 'xmlhttprequest', 'other', 'object', 'main_frame', 'sub_frame'],
  // No extraHeaders. It only reveals what Chrome otherwise withholds -- Cookie, Referer and
  // Set-Cookie -- and UNREPLAYABLE strips all three, while Chrome documents that asking for
  // it moves request handling onto a slower path. XDM needs it because it forwards the
  // cookie from here; Stretto reads cookies from the cookie store instead.
}, ['requestHeaders']);

api.webRequest.onHeadersReceived.addListener((details) => {
  const requestHeaders = takeRequest(details.requestId);

  if (!/^https?:/i.test(details.url)) return;
  if (details.tabId < 0) return;   // page media only, not background fetches

  let mime = '';
  let size = 0;
  for (const header of details.responseHeaders || []) {
    const name = header.name.toLowerCase();
    if (name === 'content-type') mime = header.value || '';
    else if (name === 'content-length' && !size) size = Number(header.value) || 0;
    // A ranged response's length is only the slice; the total is after the slash.
    else if (name === 'content-range') {
      const total = Number(String(header.value || '').split('/')[1]);
      if (total > 0) size = total;
    }
  }

  const kind = mediaKind(details.url, mime, currentMediaTypes);
  if (!kind) return;

  void recordMedia({
    url: details.url, tabId: details.tabId, mime, size, kind,
    documentUrl: details.documentUrl, initiator: details.initiator,
    requestHeaders,
  });
}, {
  urls: ['<all_urls>'],
  // Only the request types media can arrive as. Without this filter every image, script,
  // stylesheet and font on every page woke the service worker just to be rejected, which
  // is most of a browser's traffic. <video> sources are "media"; players such as hls.js
  // and dash.js fetch playlists and segments as "xmlhttprequest"; "other" catches fetch()
  // in some Chromium versions, and "object" old embeds.
  types: ['media', 'xmlhttprequest', 'other', 'object'],
}, ['responseHeaders']);

api.tabs.onRemoved.addListener(async (tabId) => {
  await updateMedia((list) => list.filter((m) => m.tabId !== tabId));
});

// A tab that goes to a new page starts a fresh list. Without this the popup kept showing
// the previous page's videos under the new one, with a badge count to match.
api.tabs.onUpdated.addListener(async (tabId, change) => {
  if (!change.url) return;

  const list = await getMedia();
  if (!list.some((m) => m.tabId === tabId)) return;   // nothing to clear; skip the write

  // Same page with a new #hash or pushState is not a new page for media purposes: many
  // video sites change the URL while the same player keeps running.
  const origin = (u) => { try { return new URL(u).origin + new URL(u).pathname; } catch { return u; } };
  const current = list.find((m) => m.tabId === tabId && m.documentUrl)?.documentUrl;
  if (current && origin(current) === origin(change.url)) return;

  await clearMedia(tabId);
});

function isStreamKind(kind, url) {
  return kind === 'stream' || PLAYLIST_EXTS.has(extensionOf(url, ''));
}
