// Stretto browser integration, the Firefox half: how a download is intercepted.
//
// Firefox has no downloads.onDeterminingFilename, which is what the Chromium build holds a
// download with. It does still have what Chromium took away: blocking webRequest, and a
// listener that may return a Promise. So the response headers of a navigation are held
// while Stretto decides, exactly as long as the Chromium build holds its download:
//
//   taken    -> the request is cancelled and Stretto fetches the file itself;
//   declined -> the listener returns nothing and the browser carries on with the very same
//               response, so nothing is re-requested and nothing is lost.
//
// As in the Chromium build, if the app cannot be reached the download stays with the
// browser. A download manager that eats downloads while it is closed is worse than none.
//
// Everything shared with Chromium is in common.js, loaded first.

/** Types a browser shows in a tab rather than saving, unless told to save them. */
const INLINE_TYPES = /^(text\/|image\/|video\/|audio\/|application\/(xhtml\+xml|xml|json|javascript|pdf|x-javascript)|multipart\/)/i;

function header(details, name) {
  const found = (details.responseHeaders || []).find((h) => h.name.toLowerCase() === name);
  return found ? found.value || '' : '';
}

/** The filename a Content-Disposition header asks for, RFC 5987 form preferred. */
function dispositionName(disposition) {
  const extended = /filename\*\s*=\s*[^']*'[^']*'([^;]+)/i.exec(disposition);
  if (extended) {
    try { return decodeURIComponent(extended[1].trim().replace(/^"|"$/g, '')); } catch { /* fall through */ }
  }

  const plain = /filename\s*=\s*("([^"]*)"|[^;]+)/i.exec(disposition);
  return plain ? (plain[2] ?? plain[1]).trim() : '';
}

/**
 * The whole file's size, not this response's.
 *
 * A 206 carries only the slice it is serving in Content-Length; the file's total is after
 * the slash in Content-Range. Read the length alone and a server that answers from a cache
 * looks like a few hundred bytes, which quietly defeats a minimum-size capture rule. The
 * Chromium build's media watcher reads it the same way.
 */
function totalSize(details) {
  const total = Number(header(details, 'content-range').split('/')[1]);
  if (total > 0) return total;
  return Number(header(details, 'content-length')) || 0;
}

/**
 * Whether this response is a download at all, before any of the user's rules apply: the
 * server said attachment, or it is a navigation to something a browser cannot display.
 */
function looksLikeDownload(details) {
  const disposition = header(details, 'content-disposition');
  if (/^\s*attachment/i.test(disposition)) return true;

  const type = header(details, 'content-type').split(';')[0].trim();
  return type !== '' && !INLINE_TYPES.test(type);
}

api.webRequest.onHeadersReceived.addListener(
  (details) => decide(details),
  { urls: ['<all_urls>'], types: ['main_frame', 'sub_frame'] },
  ['blocking', 'responseHeaders'],
);

async function decide(details) {
  // Claimed before the first await, and claimed once: the pairing onSendHeaders listener
  // lives in common.js, and this is the only chance to take what it kept.
  const headers = takeRequest(details.requestId);

  if (!/^https?:/i.test(details.url)) return {};
  if (details.statusCode < 200 || details.statusCode >= 300) return {};

  // A form POST cannot be repeated by the app from its address alone, so it stays with
  // the browser. (The Chromium build leaves unwanted POSTs with the browser too, but one
  // it hands to Stretto and gets back is re-issued by address, which loses the body.)
  if (details.method !== 'GET') return {};
  if (!looksLikeDownload(details)) return {};

  let cfg = await config();
  if (!cfg.enabled) return {};

  const ready = await connectWithRules(cfg);
  if (!ready) {
    notifyOffline();
    return {};
  }

  // Fresh rules for every decision, so a saved change applies to the very next download.
  cfg = ready;

  const filename = dispositionName(header(details, 'content-disposition'));
  const size = totalSize(details);
  if (shouldSkip(cfg, details.url, filename, size, details.documentUrl || details.originUrl)) return {};

  try {
    const taken = await sendToApp(cfg, {
      url: details.url,
      fileName: filename || null,
      cookie: (await cookieHeaderFor(details.url)) || null,
      referrer: details.originUrl || details.documentUrl || null,
      userAgent: navigator.userAgent,
      size: size > 0 ? size : null,
      mime: header(details, 'content-type') || null,
      headers,
    });

    if (!taken) return {};
  } catch (error) {
    console.info(`Stretto: sending failed: ${error.message}`);
    notify('Stretto stopped answering, so the browser is handling this download.');
    return {};
  }

  // A link with target=_blank opens a tab just to carry the download. Cancelling leaves
  // that tab blank, so close it; a tab that already showed a page is left alone.
  if (details.type === 'main_frame' && details.tabId >= 0) {
    api.tabs.get(details.tabId)
      .then((tab) => { if (!tab.url || tab.url === 'about:blank') api.tabs.remove(details.tabId); })
      .catch(() => {});
  }

  return { cancel: true };
}
