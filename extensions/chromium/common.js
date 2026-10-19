// Stretto browser integration: everything that is the same in every browser.
//
// Discovery and pairing, the capture rules, right-click and grab-all, media detection for
// the popup, and the popup's messages. How a download is actually intercepted differs
// per engine and lives beside this file: capture-chromium.js or capture-firefox.js.
//
// `api` is Firefox's promise-based `browser` where it exists and Chrome's `chrome`
// otherwise; in Manifest V3 both return promises, so one code path serves both.

const api = globalThis.browser ?? globalThis.chrome;

// Chromium MV3 calls it action; Firefox MV2 still calls it browserAction.
const action = api.action ?? api.browserAction;

const DEFAULTS = {
  enabled: true,
  // Discovered, not configured. The extension finds the app's port itself and asks it for
  // a token; nothing here is meant to be typed by hand.
  port: 0,
  token: '',
  // Last rules fetched from the app. The app owns these; this is only a cache so capture
  // still behaves sensibly while the app is closed. Empty captureTypes means "not fetched
  // yet", which is treated as capture-everything rather than capture-nothing -- failing
  // closed here would silently stop taking over downloads.
  // mediaTypes and alwaysHosts are additive on top of the built-in tables, so an older
  // Stretto that sends neither still works.
  rules: {
    captureTypes: [], videoTypes: [], mediaTypes: [], minVideoKb: 0, skipHosts: [], alwaysHosts: [], nameFrom: 'Smart',
  },
};

const config = () => api.storage.local.get(DEFAULTS);

const appUrl = (cfg, path) => `http://127.0.0.1:${cfg.port}${path}`;

// Stretto's default plus a few neighbours, so a port clash does not mean a settings trip.
const PORT_RANGE = [9615, 9616, 9617, 9618, 9619];

// Every call to the app carries a deadline. Loopback should answer in milliseconds, so
// these are far longer than a healthy request needs and far shorter than "stuck".
// A missing timeout is not a slow request, it is a permanently pending promise: nothing
// upstream can make progress, and in the capture path a download is being held open
// against that promise.
const PING_TIMEOUT_MS = 1500;
const RULES_TIMEOUT_MS = 5000;

/** Finds the port Stretto is listening on, or null. */
async function discoverPort(cfg) {
  const ports = [cfg.port, ...PORT_RANGE].filter((p, i, all) => p && all.indexOf(p) === i);

  for (const port of ports) {
    try {
      // Short timeout: a closed loopback port usually refuses instantly, but a port held
      // by something unrelated could otherwise stall the whole scan.
      const response = await fetch(`http://127.0.0.1:${port}/hello`, {
        signal: AbortSignal.timeout(1500),
      });
      if (!response.ok) continue;

      const body = await response.json();
      if (body.app === 'stretto') return port;
    } catch {
      // Nothing listening there.
    }
  }

  return null;
}

/** Asks the app for a token. It answers once the user has allowed this extension. */
async function requestPairing(port) {
  try {
    // The app may be asking the user to allow this extension, so allow time to answer --
    // but not forever, or a prompt nobody sees would wedge the worker. A refusal here just
    // means the next alarm tries again.
    // Firefox may send no Origin on this request, so its extension names itself. Chromium
    // always sends one, and a custom header would only cost it a preflight.
    const ownUrl = api.runtime.getURL('').replace(/\/$/, '');
    const headers = ownUrl.startsWith('moz-extension://') ? { 'X-Stretto-Extension': ownUrl } : {};

    const response = await whileAwake(fetch(`http://127.0.0.1:${port}/pair`, {
      method: 'POST',
      headers,
      signal: AbortSignal.timeout(90000),
    }));
    if (!response.ok) return null;

    const body = await response.json();
    return body.token || null;
  } catch {
    return null;
  }
}

/**
 * Returns a config that can actually talk to the app, pairing first if needed.
 *
 * Everything that touches the app goes through here, so installing the extension is the
 * whole setup: no port to copy, no token to paste.
 */
let lastConnectAttempt = 0;

/**
 * A "!" on the toolbar icon while Stretto cannot be reached, so a download going to the
 * browser is not the first sign. Set as the default for every tab; a tab's own media count
 * overrides it, and is the more useful thing to see there anyway.
 */
async function showHealth(reachable) {
  try {
    if (reachable) {
      await action.setBadgeText({ text: '' });
      // Back to the palette blue, because the red is a global default and a tab that once
      // reported being offline would otherwise stay red for good once it reconnected.
      await action.setBadgeBackgroundColor({ color: '#3CA0FF' });
      // Hand the tooltip back to the capture switch, which knows whether it is off.
      await showCaptureIcon((await config()).enabled !== false);
    } else {
      await action.setBadgeBackgroundColor({ color: '#FF6B6B' });
      await action.setBadgeText({ text: '!' });
      await action.setTitle({ title: 'Stretto is not running, or has not allowed this extension' });
    }
  } catch {
    // Decoration only.
  }
}

/** Whether the last connection attempt reached the app. Null until there has been one. */
let healthOnline = null;

async function ensureConnection(cfg) {
  const result = await connect(cfg);
  const online = result !== null;
  void showHealth(online);

  // Only when the answer changed. alarms.create restarts the timer, so rescheduling on
  // every call would mean the heartbeat never fires during busy browsing -- which is
  // exactly when a stale badge would be noticed. Scheduling on the transition is what
  // makes an app closing now drop to the fast poll, and starting now find the app quickly.
  if (online !== healthOnline) {
    healthOnline = online;
    scheduleHealth(online);
  }

  return result;
}

async function connect(cfg) {
  if (cfg.token && cfg.port) {
    try {
      const response = await fetch(appUrl(cfg, '/ping'), {
        headers: { 'X-Stretto-Token': cfg.token },
        signal: AbortSignal.timeout(PING_TIMEOUT_MS),
      });
      if (response.ok) return cfg;
    } catch {
      // App moved, restarted, or accepted the connection and then said nothing. The
      // timeout is what makes that last case an error rather than a hang: without it
      // this await never settles, which stalls the heartbeat and, worse, leaves a
      // download held open by onDeterminingFilename with nothing on screen to explain it.
    }
  }

  // Back off between attempts: without this, every download with the app closed would
  // rescan five ports before falling back to the browser.
  if (Date.now() - lastConnectAttempt < 10000) return null;
  lastConnectAttempt = Date.now();

  const port = await discoverPort(cfg);
  if (!port) return null;

  const token = await requestPairing(port);
  if (!token) return null;

  await api.storage.local.set({ port, token });
  return { ...cfg, port, token };
}

async function callApp(cfg, path, options = {}) {
  const response = await fetch(appUrl(cfg, path), {
    ...options,
    headers: { 'Content-Type': 'application/json', 'X-Stretto-Token': cfg.token, ...options.headers },
  });

  if (!response.ok) throw new Error(`app returned ${response.status}`);
  return response.json();
}

function extensionOf(url, filename) {
  const name = filename || (() => {
    try { return new URL(url).pathname.split('/').pop() || ''; } catch { return ''; }
  })();

  const dot = name.lastIndexOf('.');
  return dot < 0 ? '' : name.slice(dot + 1).toLowerCase();
}

/** Pulls the capture rules from the app and caches them. */
async function refreshRules(cfg) {
  try {
    const rules = await callApp(cfg, '/rules', { signal: AbortSignal.timeout(RULES_TIMEOUT_MS) });
    if (Array.isArray(rules.captureTypes)) {
      await api.storage.local.set({ rules });
      // The webRequest listener cannot await, so the one rule it needs is mirrored here.
      currentMediaTypes = Array.isArray(rules.mediaTypes) ? rules.mediaTypes : [];
      return rules;
    }
  } catch {
    // App closed or not paired. Keep using the cached copy.
  }

  return cfg.rules;
}

/**
 * The registrable part of a host, so www.civitai.com and cdn.civitai.com are both
 * civitai.com. The same rule of thumb as the app's SiteOf; keep the two in step.
 */
function siteOf(host) {
  const labels = String(host || '').toLowerCase().replace(/^\.+|\.+$/g, '').split('.');
  if (labels.length <= 2 || /^[\d.]+$/.test(host) || host.includes(':')) return String(host || '').toLowerCase();
  const countryCode = labels[labels.length - 1].length === 2;
  const secondLevel = ['co', 'com', 'org', 'net', 'gov', 'ac', 'edu', 'ne', 'or'].includes(labels[labels.length - 2]);
  return labels.slice(countryCode && secondLevel ? -3 : -2).join('.');
}

/** A host is skipped when it, or the site it belongs to, is on the list. */
function hostSkipped(host, skipHosts) {
  if (!host) return false;
  const site = siteOf(host);
  return (skipHosts || []).some((h) => h && (host === h || host.endsWith(`.${h}`) || site === h));
}

/** The same test for the "always take this site" list. */
function hostAlways(host, alwaysHosts) {
  if (!host) return false;
  const site = siteOf(host);
  return (alwaysHosts || []).some((h) => h && (host === h || host.endsWith(`.${h}`) || site === h));
}

function shouldSkip(cfg, url, filename, size, referrer) {
  let host = '';
  try { host = new URL(url).hostname; } catch { return true; }

  let pageHost = '';
  try { pageHost = referrer ? new URL(referrer).hostname : ''; } catch { /* no page */ }

  const rules = cfg.rules || DEFAULTS.rules;

  // Either the file's own server or the page it was downloaded from: the page is what
  // the user thinks of as "the site", the server is often an anonymous CDN.
  if (hostSkipped(host, rules.skipHosts) || hostSkipped(pageHost, rules.skipHosts)) return true;

  // An allowlist, the way XDM does it: only listed types are taken over. An empty list
  // means the rules have never been fetched, so capture everything rather than nothing.
  const type = extensionOf(url, filename);
  const allowed = rules.captureTypes || [];
  const media = rules.videoTypes || [];

  // A site the user has said to always trust skips the allowlist, which is XDM's
  // "matching hosts". The skip list above still wins: never-capture beats always-capture.
  const trusted = hostAlways(host, rules.alwaysHosts) || hostAlways(pageHost, rules.alwaysHosts);

  // An empty type means the URL carries no extension, which is exactly what an API-style
  // download endpoint looks like (civitai.com/api/download/models/123). Those are usually
  // the ones worth taking over, and the app resolves the real name from Content-Disposition
  // anyway, so unknown is captured rather than dropped.
  if (!trusted && type !== '' && allowed.length > 0 && !allowed.includes(type) && !media.includes(type)) {
    return true;
  }

  // Size is frequently unknown at creation time, so this can only filter when it is known.
  const minKb = rules.minVideoKb || 0;
  if (minKb > 0 && media.includes(type) && size > 0 && size < minKb * 1024) return true;

  return false;
}

async function cookieHeaderFor(url) {
  try {
    const cookies = await api.cookies.getAll({ url });
    return cookies.map((c) => `${c.name}=${c.value}`).join('; ');
  } catch {
    return '';
  }
}

/**
 * Greys the toolbar icon while capture is switched off, the way XDM does, so "why did the
 * browser keep that download?" is answered by a glance at the toolbar.
 */
async function showCaptureIcon(enabled) {
  const suffix = enabled ? '' : '-off';
  try {
    await action.setIcon({
      path: { 16: `icon16${suffix}.png`, 32: `icon32${suffix}.png`, 48: `icon48${suffix}.png`, 128: `icon128${suffix}.png` },
    });
    await action.setTitle({ title: enabled ? 'Stretto Download Manager' : 'Stretto: capture is off' });
  } catch {
    // Decoration only.
  }
}

config().then((cfg) => showCaptureIcon(cfg.enabled !== false));
api.storage.onChanged.addListener((changes, area) => {
  if (area === 'local' && changes.enabled) void showCaptureIcon(changes.enabled.newValue !== false);
});

async function notify(message) {
  // Off in the options page for anyone who finds them noisy; the popup and the icon
  // still tell the same story.
  const { notifications } = await api.storage.local.get({ notifications: true });
  if (!notifications) return;

  api.notifications.create({
    type: 'basic',
    iconUrl: 'icon128.png',
    title: 'Stretto',
    message,
  }, () => void api.runtime.lastError);   // notifications are best effort
}

/**
 * Offers a download to Stretto and reports whether it took it. The app may put the New
 * Download window on screen first, so this can sit waiting for a person.
 */
async function sendToApp(cfg, payload) {
  const answer = await whileAwake(callApp(cfg, '/add', {
    method: 'POST',
    body: JSON.stringify(payload),
    signal: AbortSignal.timeout(600000),
  }));

  return answer.action !== 'browser';
}

/**
 * Keeps the service worker alive until the work settles.
 *
 * A Manifest V3 worker is shut down after about 30 seconds without an extension API call,
 * and a fetch waiting on a reply does not count. /add waits for a person to answer
 * Stretto's New Download window -- picking a folder easily takes longer than that -- and
 * when the worker died mid-wait the browser dropped the held download and showed its own
 * Save dialog. Any cheap API call resets the timer, so one runs every 20 seconds.
 */
async function whileAwake(work) {
  const ping = setInterval(() => { api.runtime.getPlatformInfo().catch(() => {}); }, 20000);
  try {
    return await work;
  } finally {
    clearInterval(ping);
  }
}

// ═══ Right-click capture, for links the browser would open rather than download ═══

api.runtime.onStartup.addListener(async () => {
  const cfg = await ensureConnection(await config());
  if (cfg) await refreshRules(cfg);
});

// ═══ The heartbeat ═══
//
// Two things the toolbar has to be right about, and neither can be right from a single
// check: is Stretto running, and are the rules current. XDM solves this with twelve
// alarms a minute, forever, which is why XDM's extension is always slightly warm.
//
// Polling an app that is already there is a wasted socket and a wasted worker wake-up, so
// this backs off: often while the app is unreachable, so starting it is noticed within
// half a minute, and rarely once it is up, where the download path is already refreshing
// the rules on every capture anyway. ensureConnection re-picks the period whenever the
// answer changes, so an app that closes is noticed at once rather than in five minutes.

const HEALTH_ALARM = 'stretto-health';

/** Chrome's floor for a repeating alarm is half a minute. */
const HEALTH_OFFLINE_MINUTES = 0.5;
const HEALTH_ONLINE_MINUTES = 5;

function scheduleHealth(online) {
  try {
    api.alarms.create(HEALTH_ALARM, {
      periodInMinutes: online ? HEALTH_ONLINE_MINUTES : HEALTH_OFFLINE_MINUTES,
    });
  } catch {
    // An alarm failing to schedule is not worth interrupting anything over.
  }
}

async function heartbeat() {
  // Refreshes the rules too when the app is reachable. A separate rules alarm used to
  // sit alongside this doing the same two calls on a five-minute period, which is exactly
  // what the heartbeat already does -- and it could not notice an app that had just been
  // started, because five minutes is a long time to find out.
  const cfg = await ensureConnection(await config());
  if (cfg) await refreshRules(cfg);
}

api.alarms.onAlarm.addListener((alarm) => {
  if (alarm.name === HEALTH_ALARM) void heartbeat();
});

api.runtime.onInstalled.addListener(() => {
  // Pair the moment the extension is installed, so dropping it in is the whole setup.
  // This is also what seeds the heartbeat: ensureConnection schedules it, because the
  // first answer is the one that is guaranteed to be a change from "not known yet".
  config().then(ensureConnection).then((cfg) => cfg && refreshRules(cfg));

  api.contextMenus.create({
    id: 'stretto-download',
    title: 'Download with Stretto',
    contexts: ['link', 'video', 'audio', 'image'],
  });

  api.contextMenus.create({
    id: 'stretto-all-links',
    title: 'Download all links with Stretto',
    contexts: ['page', 'frame', 'selection'],
  });

  api.contextMenus.create({
    id: 'stretto-all-images',
    title: 'Download all images with Stretto',
    contexts: ['page', 'frame', 'selection'],
  });
});

/**
 * Scrapes the page in its own context. Runs as an injected function rather than pulling the
 * HTML back and parsing it here, because the DOM already resolved every relative URL and
 * knows which images actually loaded.
 */
function collectFromPage(wantImages) {
  const seen = new Set();

  const push = (value) => {
    if (!value) return;
    try {
      const absolute = new URL(value, document.baseURI).href;
      if (/^https?:/i.test(absolute)) seen.add(absolute);
    } catch { /* not a URL */ }
  };

  if (wantImages) {
    for (const img of document.querySelectorAll('img')) {
      push(img.currentSrc || img.src);
      for (const candidate of (img.srcset || '').split(','))
        push(candidate.trim().split(/\s+/)[0]);
    }
    for (const source of document.querySelectorAll('picture source[srcset]'))
      for (const candidate of source.srcset.split(','))
        push(candidate.trim().split(/\s+/)[0]);
  } else {
    for (const anchor of document.querySelectorAll('a[href]')) push(anchor.href);
  }

  return [...seen];
}

const IMAGE_TYPES = ['jpg', 'jpeg', 'png', 'gif', 'webp', 'bmp', 'svg', 'avif', 'ico', 'tif', 'tiff'];

/**
 * Sends every download link (or image) on the page to Stretto as one batch, which the app
 * shows as one list to confirm. Returns the message it also shows as a notification, so
 * the popup can put it on screen.
 */
async function downloadAll(info, tab, wantImages) {
  const done = (message) => { notify(message); return message; };

  let cfg = await config();

  const ready = await ensureConnection(cfg);
  if (!ready) return done('Stretto is not running, or has not allowed this extension yet.');

  cfg = { ...ready, rules: await refreshRules(ready) };

  let urls = [];
  try {
    // A right-click in one frame reads that frame; the popup reads every frame, since
    // galleries and file lists often sit in an iframe the top page only frames.
    const results = await api.scripting.executeScript({
      target: info.frameId ? { tabId: tab.id, frameIds: [info.frameId] } : { tabId: tab.id, allFrames: true },
      func: collectFromPage,
      args: [wantImages],
    });
    urls = [...new Set(results.flatMap((r) => r?.result || []))];
  } catch (error) {
    return done(`Could not read the page (${error.message}).`);
  }

  const rules = cfg.rules || DEFAULTS.rules;

  // A page has far more links than downloads. Images are taken by type; links are filtered
  // through the same allowlist the app uses, otherwise this queues every nav link on the page.
  urls = urls.filter((url) => {
    const type = extensionOf(url, '');
    if (wantImages) return IMAGE_TYPES.includes(type);

    const allowed = rules.captureTypes || [];
    const media = rules.videoTypes || [];
    return type !== '' && (allowed.includes(type) || media.includes(type));
  });

  if (urls.length === 0) {
    return done(wantImages ? 'No images found on this page.' : 'No downloadable links found on this page.');
  }

  // One cookie header per host rather than per link: a gallery is hundreds of images on
  // two or three hosts.
  const cookies = {};
  for (const host of new Set(urls.map((u) => { try { return new URL(u).host; } catch { return ''; } }))) {
    if (!host) continue;
    const header = await cookieHeaderFor(`https://${host}/`);
    if (header) cookies[host] = header;
  }

  try {
    const answer = await whileAwake(callApp(cfg, '/batch', {
      method: 'POST',
      body: JSON.stringify({ urls, referrer: info.pageUrl || null, userAgent: navigator.userAgent, cookies }),
      signal: AbortSignal.timeout(300000),
    }));

    const accepted = answer.accepted || 0;
    return done(accepted === 0 ? 'Nothing was added.' : `Sent ${accepted} of ${urls.length} to Stretto.`);
  } catch (error) {
    // An older Stretto has no /batch route. Fall back to one request per link.
    if (!String(error.message).includes('404')) return done(`Stretto is not reachable (${error.message}).`);
  }

  let sent = 0;
  for (const url of urls) {
    try {
      const accepted = await sendToApp(cfg, {
        url,
        fileName: null,
        cookie: (await cookieHeaderFor(url)) || null,
        referrer: info.pageUrl || null,
        userAgent: navigator.userAgent,
        size: null,
      });
      if (accepted) sent++;
    } catch {
      // Keep going; one bad link should not abandon the rest.
    }
  }

  return done(`Sent ${sent} of ${urls.length} to Stretto.`);
}

api.contextMenus.onClicked.addListener(async (info, tab) => {
  if (info.menuItemId === 'stretto-all-links') return downloadAll(info, tab, false);
  if (info.menuItemId === 'stretto-all-images') return downloadAll(info, tab, true);
  if (info.menuItemId !== 'stretto-download') return;

  const url = info.linkUrl || info.srcUrl;
  if (!url || !/^https?:/i.test(url)) return;

  let cfg = await config();
  const ready = await ensureConnection(cfg);
  if (!ready) {
    notify('Stretto is not running, or has not allowed this extension yet.');
    return;
  }

  cfg = ready;

  try {
    const accepted = await sendToApp(cfg, {
      url,
      fileName: null,
      cookie: (await cookieHeaderFor(url)) || null,
      referrer: info.pageUrl || null,
      userAgent: navigator.userAgent,
      size: null,
    });
    notify(accepted ? 'Sent to Stretto.' : 'Stretto did not take this link.');
  } catch (error) {
    notify(`Stretto is not reachable (${error.message}).`);
  }
});

api.runtime.onMessage.addListener((message, _sender, reply) => {
  if (message?.type === 'stretto-send') {
    sendMedia(message).then(reply);
    return true;
  }

  if (message?.type === 'stretto-clear') {
    clearMedia(message.tabId).then(reply);
    return true;
  }

  // The popup asks this on open. Deliberately does not pair: pairing can wait on a
  // person answering the app's prompt, and a popup cannot sit open that long.
  if (message?.type === 'stretto-status') {
    appStatus().then(reply).catch(() => reply({ state: 'offline' }));
    return true;
  }

  if (message?.type === 'stretto-grab-all') {
    api.tabs.get(message.tabId)
      .then((tab) => downloadAll({ frameId: 0, pageUrl: tab.url }, tab, message.images === true))
      .then((text) => reply({ ok: true, message: text }))
      .catch((error) => reply({ ok: false, error: String(error?.message || error) }));
    return true;
  }

  if (message?.type === 'stretto-skip-host') {
    setSkipHost(message.host, message.skip !== false).then(reply);
    return true;
  }

  // The options page asks for this rather than duplicating discovery and pairing.
  if (message?.type === 'stretto-connect') {
    config()
      .then(ensureConnection)
      .then((cfg) => reply(cfg ? { ok: true, port: cfg.port } : { ok: false }))
      .catch(() => reply({ ok: false }));
    return true;
  }

  return false;
});

/** connected | unpaired (app found, not yet allowed) | offline (app not running). */
async function appStatus() {
  const cfg = await config();

  if (cfg.token && cfg.port) {
    try {
      const response = await fetch(appUrl(cfg, '/ping'), {
        headers: { 'X-Stretto-Token': cfg.token },
        signal: AbortSignal.timeout(1500),
      });
      if (response.ok) return { state: 'connected', port: cfg.port, enabled: cfg.enabled };
    } catch {
      // Fall through to discovery.
    }
  }

  const port = await discoverPort(cfg);
  return { state: port ? 'unpaired' : 'offline', port, enabled: cfg.enabled };
}

/** The headers and size captured for this media when the browser fetched it, if still held. */
async function capturedFor(url, tabId) {
  const entry = (await getMedia()).find((m) => m.url === url && (tabId == null || m.tabId === tabId));
  return { headers: entry?.requestHeaders || null, size: entry?.size || 0 };
}

async function sendMedia({ url, tabUrl, tabId, kind }) {
  const cfg = await ensureConnection(await config());
  if (!cfg) return { ok: false, error: 'not-paired' };

  // The sighting the user clicked is the one that saw the request, so its headers and
  // size are worth more than re-deriving either. The popup does not pass them; it only
  // knows the row it rendered.
  const captured = await capturedFor(url, tabId);
  if (captured.headers) {
    console.info(`Stretto: sending ${url} with ${Object.keys(captured.headers).join(', ')}`);
  }

  try {
    const accepted = await sendToApp(cfg, {
      url,
      fileName: await nameFor({ url, tabId }),
      cookie: (await cookieHeaderFor(url)) || null,
      referrer: tabUrl || null,
      userAgent: navigator.userAgent,
      size: captured.size > 0 ? captured.size : null,
      isStream: isStreamKind(kind, url),
      headers: captured.headers,
    });
    return accepted ? { ok: true } : { ok: false, error: 'declined' };
  } catch (error) {
    return { ok: false, error: String((error && error.message) || error) };
  }
}

/** Adds or removes a site on the app's skip list, and caches the rules it answers with. */
async function setSkipHost(host, skip) {
  const cfg = await ensureConnection(await config());
  if (!cfg) return { ok: false, error: 'not-paired' };

  try {
    // Deadlined like the rest: this one is awaited by the popup's site switch, so a stuck
    // app would otherwise leave the toggle spinning with no answer either way.
    const rules = await callApp(cfg, '/skip-host', {
      method: 'POST',
      signal: AbortSignal.timeout(RULES_TIMEOUT_MS),
      body: JSON.stringify({ host, skip }),
    });
    if (Array.isArray(rules.captureTypes)) await api.storage.local.set({ rules });
    return { ok: true };
  } catch (error) {
    return { ok: false, error: String(error?.message || error) };
  }
}

async function clearMedia(tabId) {
  await updateMedia((list) => tabId == null ? [] : list.filter((m) => m.tabId !== tabId));

  try {
    if (tabId == null) await action.setBadgeText({ text: '' });
    else await action.setBadgeText({ tabId, text: '' });
  } catch {
    // Decoration only.
  }

  return { ok: true };
}
