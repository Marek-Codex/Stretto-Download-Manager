// Stretto browser integration.
//
// Manifest V3 removed blocking webRequest, so XDM's original trick -- intercept the request
// before it starts and redirect it to the app -- is not available any more. What works
// instead is downloads.onDeterminingFilename, the earliest point a download can be seen.
//
// The important consequence: if the app cannot be reached, we must give the download back
// to the browser rather than swallowing it. A download manager that silently eats downloads
// when it is closed is worse than no download manager.
//
// This file is the Chromium half: the interception itself. Everything shared with Firefox
// is in common.js, loaded first.

/**
 * The interception point.
 *
 * The quick checks (switched on, app reachable, rules) run while the browser holds the
 * download, and a download Stretto does not want is released untouched with suggest(), so
 * it keeps its original request, form POSTs included. Once Stretto is going to be asked,
 * the browser's copy is cancelled at once, as XDM does: holding it through the New Download
 * window let Brave show its own Save dialog as well. If Stretto then declines, the download
 * is re-issued to the browser by address, which is the one route that loses a POST.
 */
// Addresses Stretto handed back, re-issued to the browser. The listener lets exactly one
// download of each through, or giving it back would just capture it again.
const passThrough = new Set();

api.downloads.onDeterminingFilename.addListener((item, suggest) => {
  const url = item.finalUrl || item.url;
  if (!url || !/^https?:/i.test(url)) return false;

  if (passThrough.delete(item.url) || passThrough.delete(url)) return false;

  // Returned synchronously, before any await, so the browser waits from this instant.
  claim(item, url, suggest).then((result) => {
    if (result === 'kept') suggest();
  }).catch((error) => {
    // Nothing in claim is meant to throw, and everything past the browser's cancel is
    // already guarded, so this is the last-resort path rather than a real one. But the
    // browser is holding a download open with no dialog while this promise is
    // outstanding, and a rejection would leave it stuck with nothing on screen to say why.
    // If the throw happened before the cancel, suggest() hands the download straight back.
    // If it happened after, the browser's copy is already gone and this does nothing --
    // which is why the code after the cancel keeps its own try/catch.
    console.info(`Stretto: could not decide on ${url}: ${error.message}`);
    try { suggest(); } catch { /* released either way */ }
  });

  return true;
});

/**
 * Starts a download again in the browser, after Stretto was asked and said no. The
 * original was already cancelled by then, so this is how "let the browser have it" still
 * works. Cookies come along automatically; a download that began as a form POST cannot be
 * repeated from its address, which is the one case this loses.
 */
async function giveBackToBrowser(item, url) {
  passThrough.add(url);
  try {
    await api.downloads.download({ url });
  } catch (error) {
    passThrough.delete(url);
    notify(`The browser could not restart this download (${error.message}).`);
  }
}

/**
 * Decides whether Stretto takes this download, and takes it if so.
 *
 * Returns 'kept' when the browser should carry on with its own download (nothing was
 * cancelled), or 'handled' once the browser's copy is cancelled -- whether Stretto then
 * takes it or gives it back through a fresh browser download.
 *
 * The browser's copy is cancelled *before* Stretto is asked, which is how XDM does it.
 * Holding it open while the New Download window waited on a person was the original
 * design, but Brave with "ask where to save each file" shows its own Save dialog anyway,
 * so the user got two dialogs for one download.
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

  const ready = await ensureConnection(cfg);
  if (!ready) {
    notify('Stretto is not running, or has not allowed this extension yet.');
    return giveBack('the app could not be reached');
  }

  // Fresh rules for every decision: the app is running (we just reached it) and the call
  // is local, so there is no reason to trust a copy that can be minutes old. The cache used
  // to refresh only every five minutes, so a site removed from the skip list kept being
  // skipped until then, which looked like the setting did nothing.
  cfg = { ...ready, rules: await refreshRules(ready) };

  const filename = item.filename ? item.filename.split(/[\/]/).pop() : '';

  // totalBytes is the field that replaced the deprecated fileSize, and either can be
  // absent or 0 on a given build. The rules and the handoff have to agree on this number,
  // or a minimum-size rule filters something out and the app then shows no size for it.
  const size = item.totalBytes > 0 ? item.totalBytes : (item.fileSize > 0 ? item.fileSize : 0);

  if (shouldSkip(cfg, url, filename, size, item.referrer))
    return giveBack(`a capture rule skips it (type "${extensionOf(url, filename) || 'none'}")`);

  // From here Stretto is being asked, so the browser's copy goes now, before its Save
  // dialog can appear. Everything below either takes it or re-issues it to the browser.
  try {
    await api.downloads.cancel(item.id);
    await api.downloads.erase({ id: item.id });
  } catch {
    // Already gone from the browser's list; nothing to undo.
  }
  try { suggest(); } catch { /* the listener is released either way */ }

  // Cookies are read after the cancel: they live in the browser's jar, not the download.
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
      // care about exactly that. XDM does this fallback for the same reason.
      referrer: item.referrer || (item.finalUrl && item.finalUrl !== item.url ? item.url : null),
      userAgent: navigator.userAgent,
      size: size > 0 ? size : null,
      mime: item.mime || null,
    });
  } catch (error) {
    notify(`Stretto is not reachable (${error.message}). The browser is handling this download.`);
    console.info(`Stretto: gave ${url} back to the browser: sending it failed: ${error.message}`);
    await giveBackToBrowser(item, url);
    return 'handled';
  }

  if (!taken) {
    console.info(`Stretto: gave ${url} back to the browser: Stretto answered "let the browser have it"`);
    await giveBackToBrowser(item, url);
  }

  return 'handled';
}
