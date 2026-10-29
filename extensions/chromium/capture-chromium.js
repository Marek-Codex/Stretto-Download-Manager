// Stretto browser integration.
//
// Manifest V3 removed general blocking webRequest for this extension, so intercepting a
// request before it starts is not available here. What works
// instead is downloads.onDeterminingFilename, the earliest point a download can be seen.
//
// The important consequence: if the app cannot be reached, we must give the download back
// to the browser rather than swallowing it. We manage downloads; we do not eat them.
// Yes, even when the app has gone to bed before the rest of us.
//
// This file is the Chromium half: the interception itself. Everything shared with Firefox
// is in common.js, loaded first.

/**
 * The interception point.
 *
 * The quick checks (switched on, app reachable, rules) run while the browser holds the
 * download, and a download Stretto does not want is released untouched with suggest(), so
 * it keeps its original request, form POSTs included. Holding it through the New Download
 * window can let Brave show its own Save dialog too. Preserving the original request is
 * more important: it is cancelled only after Stretto accepts, never re-issued by URL.
 */
// DownloadItem does not expose the HTTP method. Keep a short, bounded history of the
// requests that can become downloads, without bodies or credentials. Session storage
// survives service-worker suspension. If history is unavailable, keep it in the browser.
// "Probably GET" is not enough information to replay somebody's form submission.
const requestHistoryStore = api.storage.session;
let requestHistoryWrites = Promise.resolve();
let requestHistoryHealthy = true;
const REQUEST_HISTORY_KEY = 'strettoDownloadRequests';
const REQUEST_HISTORY_MAX = 256;
const REQUEST_HISTORY_AGE = 120000;

api.webRequest.onBeforeRequest.addListener(details => {
  if (!requestHistoryStore || !/^https?:/i.test(details.url)) return;
  requestHistoryWrites = requestHistoryWrites.then(async () => {
    const now = Date.now();
    const stored = await requestHistoryStore.get({ [REQUEST_HISTORY_KEY]: [] });
    const history = (stored[REQUEST_HISTORY_KEY] || []).filter(entry => now - entry.time < REQUEST_HISTORY_AGE);
    history.push({ url: details.url, method: details.method, time: details.timeStamp });
    await requestHistoryStore.set({ [REQUEST_HISTORY_KEY]: history.slice(-REQUEST_HISTORY_MAX) });
  }).catch(() => { requestHistoryHealthy = false; });
}, { urls: ['http://*/*', 'https://*/*'], types: ['main_frame', 'sub_frame', 'other'] });

async function isKnownGet(item, url) {
  if (!requestHistoryStore) return false;
  await requestHistoryWrites;
  if (!requestHistoryHealthy) return false;
  const start = Date.parse(item.startTime);
  if (!Number.isFinite(start)) return false;
  try {
    const stored = await requestHistoryStore.get({ [REQUEST_HISTORY_KEY]: [] });
    const matches = (stored[REQUEST_HISTORY_KEY] || []).filter(entry =>
      (entry.url === item.url || entry.url === url) && Math.abs(entry.time - start) <= 5000 &&
      Date.now() - entry.time < REQUEST_HISTORY_AGE);
    return matches.length > 0 && matches.every(entry => entry.method === 'GET');
  } catch { return false; }
}

api.downloads.onDeterminingFilename.addListener((item, suggest) => {
  const url = item.finalUrl || item.url;
  if (!url || !/^https?:/i.test(url)) return false;

  // Returned synchronously, before any await, so the browser waits from this instant.
  claim(item, url, suggest).then((result) => {
    if (result === 'kept') suggest();
  }).catch((error) => {
    // Every failure before acceptance releases the original request untouched.
    console.info(`Stretto: could not decide on ${url}: ${error.message}`);
    try { suggest(); } catch { /* released either way */ }
  });

  return true;
});

/**
 * Decides whether Stretto takes this download, and takes it if so.
 *
 * Returns 'kept' when the browser should carry on with its own download (nothing was
 * cancelled), or 'handled' after acceptance and cancellation. Some browsers can show
 * their Save dialog while waiting; do not trade a recoverable request for hiding a dialog.
 */
async function claim(item, url, suggest) {
  // Every way a download is given back to the browser logs why, so "the browser asked me
  // where to save it" can be answered from the extension's service worker console.
  const giveBack = (reason) => {
    console.info(`Stretto: left ${url} to the browser: ${reason}`);
    return 'kept';
  };

  let cfg = await config();
  if (!cfg.enabled) return giveBack('monitoring is switched off in the popup');
  if (!await isKnownGet(item, url)) return giveBack('the request is not a known replayable GET');

  // A site on the skip list is released at once from the cached rules, before any call to
  // the app: it never needed the app's answer, and the browser is holding the download
  // until this returns. The refresh still runs, so a change made in the app is seen next time.
  const skipHosts = cfg.rules?.skipHosts || [];
  const hostOf = (address) => { try { return new URL(address).hostname; } catch { return ''; } };
  if (hostSkipped(hostOf(url), skipHosts) || hostSkipped(hostOf(item.referrer), skipHosts)) {
    void config().then(ensureConnection).then((c) => c && refreshRules(c));
    return giveBack('the site is on the skip list');
  }

  const ready = await connectWithRules(cfg);
  if (!ready) {
    notifyOffline();
    return giveBack('the app could not be reached');
  }

  // Fresh rules for every decision: the app is running (we just reached it) and the call
  // is local, so there is no reason to trust a copy that can be minutes old. The cache used
  // to refresh only every five minutes, so a site removed from the skip list kept being
  // skipped until then, which looked like the setting did nothing.
  cfg = ready;

  const filename = item.filename ? item.filename.split(/[\/]/).pop() : '';

  // totalBytes is the field that replaced the deprecated fileSize, and either can be
  // absent or 0 on a given build. The rules and the handoff have to agree on this number,
  // or a minimum-size rule filters something out and the app then shows no size for it.
  const size = item.totalBytes > 0 ? item.totalBytes : (item.fileSize > 0 ? item.fileSize : 0);

  if (shouldSkip(cfg, url, filename, size, item.referrer))
    return giveBack(`a capture rule skips it (type "${extensionOf(url, filename) || 'none'}")`);

  // Keep the original response alive until the user/app has actually accepted it.
  // A timeout is not a yes. Neither is our enthusiasm. Wait for the answer.
  const cookie = (await cookieHeaderFor(url)) || null;


  let taken = false;

  try {
    taken = await sendToApp(cfg, {
      url,
      fileName: filename || null,
      cookie,
      // A download that arrived by redirect usually has no referrer of its own, because
      // the referrer belongs to the request the browser was following, not the one that
      // landed. The URL it redirected from is the page it came off, and hotlink checks
      // can depend on that context.
      referrer: item.referrer || (item.finalUrl && item.finalUrl !== item.url ? item.url : null),
      userAgent: navigator.userAgent,
      size: size > 0 ? size : null,
      mime: item.mime || null,
    });
  } catch (error) {
    console.info(`Stretto: sending failed: ${error.message}`);
    notify('Stretto stopped answering, so the browser is handling this download.');
    return giveBack('the app stopped answering');
  }

  if (!taken) {
    return giveBack('Stretto declined');
  }
  try {
    await api.downloads.cancel(item.id);
  } catch {
    notify('Stretto accepted this download, but the browser copy could not be stopped. Check both download lists.');
    return 'kept';
  }
  // Erasing is cosmetic. Its failure must not release a cancelled request as a new one.
  // The browser's history row can stay untidy; the user's download cannot be the sacrifice.
  try { await api.downloads.erase({ id: item.id }); } catch { /* retain cancelled history */ }
  try { suggest(); } catch { /* cancellation already released it */ }
  return 'handled';
}
