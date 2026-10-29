// Popup: whether Stretto is reachable, the capture switch, and media seen on this page.

const $ = (id) => document.getElementById(id);

// Firefox's promise-based `browser` where it exists, Chrome's `chrome` otherwise.
const api = globalThis.browser ?? globalThis.chrome;

const sessionStore = () => (api.storage.session ? api.storage.session : api.storage.local);

let tabId;
let tabUrl;
let items = [];
let showAll = false;

function extOf(url) {
  const match = /\/([^\/?#]+?)(?:\?.*)?(?:#.*)?$/.exec(url);
  const name = match ? match[1] : '';
  const dot = name.lastIndexOf('.');
  return dot < 0 ? '' : name.slice(dot + 1).toLowerCase();
}

/** Same rule as siteOf in common.js, which the popup page does not load. */
function siteOf(host) {
  const labels = String(host || '').toLowerCase().replace(/^\.+|\.+$/g, '').split('.');
  if (labels.length <= 2 || /^[\d.]+$/.test(host) || host.includes(':')) return String(host || '').toLowerCase();
  const countryCode = labels[labels.length - 1].length === 2;
  const secondLevel = ['co', 'com', 'org', 'net', 'gov', 'ac', 'edu', 'ne', 'or'].includes(labels[labels.length - 2]);
  return labels.slice(countryCode && secondLevel ? -3 : -2).join('.');
}

function fmtSize(bytes) {
  if (!bytes || bytes <= 0) return '';
  if (bytes < 1024) return `${bytes} B`;
  if (bytes < 1048576) return `${(bytes / 1024).toFixed(0)} KB`;
  if (bytes < 1073741824) return `${(bytes / 1048576).toFixed(1)} MB`;
  return `${(bytes / 1073741824).toFixed(2)} GB`;
}

/** The short label in the row's tile: the container where known, else the kind. */
function badgeFor(item) {
  const ext = extOf(item.url);
  if (item.kind === 'stream') return ext === 'mpd' ? 'DASH' : 'HLS';
  if (item.kind === 'segment') return 'SEG';
  return ext ? ext.slice(0, 4).toUpperCase() : (item.kind === 'audio' ? 'AUD' : 'VID');
}

function detailFor(item) {
  const kind = { stream: 'Stream, joined by ffmpeg', segment: 'Stream chunk', audio: 'Audio', video: 'Video' }[item.kind] || 'Media';
  const size = fmtSize(item.size);
  return size ? `${kind} · ${size}` : kind;
}

function setStatus(message, kind = '') {
  const status = $('status');
  status.textContent = message;
  status.className = kind;
}

// Duplicate display names (master plus variants) get a numbered prefix.
function displayNames(shown) {
  const counts = new Map();
  for (const item of shown) counts.set(item.name, (counts.get(item.name) || 0) + 1);

  const seen = new Map();
  return shown.map((item) => {
    if ((counts.get(item.name) || 0) < 2) return item.name;
    const n = (seen.get(item.name) || 0) + 1;
    seen.set(item.name, n);
    return `(${n}) ${item.name}`;
  });
}

function visibleItems() {
  const scoped = tabId == null ? items : items.filter((m) => m.tabId === tabId);
  const shown = showAll ? scoped : scoped.filter((m) => m.kind !== 'segment');
  return [...shown].sort((a, b) => (b.seenAt || 0) - (a.seenAt || 0));
}

function render() {
  const list = $('list');
  list.textContent = '';

  const shown = visibleItems();
  $('empty').hidden = shown.length > 0;
  list.hidden = shown.length === 0;
  $('clear').hidden = shown.length === 0;

  const names = displayNames(shown);
  shown.forEach((item, index) => {
    const row = document.createElement('button');
    row.type = 'button';
    row.className = `row ${item.kind}`;

    const tile = document.createElement('span');
    tile.className = 'kind';
    tile.textContent = badgeFor(item);
    tile.setAttribute('aria-hidden', 'true');

    const meta = document.createElement('span');
    meta.className = 'meta';

    const name = document.createElement('div');
    name.className = 'name';
    name.textContent = names[index];
    name.title = item.url;

    const detail = document.createElement('div');
    detail.className = 'detail';
    detail.textContent = detailFor(item);

    const send = document.createElement('span');
    send.className = 'send';
    send.textContent = 'Download';
    send.setAttribute('aria-hidden', 'true');

    meta.append(name, detail);
    row.setAttribute('aria-label', `Download ${names[index]} with Stretto, ${detail.textContent}`);
    row.append(tile, meta, send);
    row.addEventListener('click', () => sendItem(row, item));
    list.append(row);
  });
}

async function sendItem(row, item) {
  row.disabled = true;
  setStatus(`Sending ${item.name}…`);

  try {
    const result = await api.runtime.sendMessage({ type: 'stretto-send', url: item.url, tabUrl, tabId: item.tabId, kind: item.kind });

    if (result?.ok) setStatus('Sent to Stretto.', 'ok');
    else if (result?.error === 'not-paired') setStatus('Stretto has not allowed this extension yet.', 'bad');
    else if (result?.error === 'declined') setStatus('Stretto did not take it.', 'bad');
    else setStatus(`Stretto is not reachable (${result?.error || 'unknown error'}).`, 'bad');
  } catch {
    setStatus('Stretto is not reachable. Is the app running?', 'bad');
  }

  row.disabled = false;
}

/** Fills the connection line under the name. */
async function showConnection() {
  const conn = $('conn');
  const text = $('conn-text');

  let status;
  try {
    status = await api.runtime.sendMessage({ type: 'stretto-status' });
  } catch {
    status = { state: 'offline' };
  }

  conn.className = `conn ${status?.state || 'offline'}`;
  text.textContent = '';

  if (status?.state === 'connected') {
    text.textContent = 'Connected';
  } else if (status?.state === 'unpaired') {
    // The app is running but has not handed this extension a token yet; asking again
    // brings up its Allow prompt.
    text.append('Not paired · ');
    const link = document.createElement('a');
    link.textContent = 'Connect';
    link.addEventListener('click', async () => {
      text.textContent = 'Waiting for Stretto…';
      try { await api.runtime.sendMessage({ type: 'stretto-connect' }); } catch { /* shown below */ }
      showConnection();
    });
    text.append(link);
  } else {
    // Offer to start the app when it is absent. Its registered URI scheme means
    // the user does not need to go hunting for the shortcut.
    text.textContent = 'Stretto is not running · ';
    const launch = document.createElement('a');
    launch.textContent = 'Start it';
    launch.addEventListener('click', () => {
      // An extension page navigating to a custom scheme; the browser hands it to whatever
      // registered it, which is the app. Nothing to wait for, so the popup just closes.
      window.location.href = 'stretto://launch';
      window.close();
    });
    text.append(launch);
  }

  const offline = status?.state !== 'connected';
  $('all-links').disabled = offline;
  $('all-images').disabled = offline;

  await showSite(!offline);
}

/** The site switch: whether this host is on the app's skip list. */
async function showSite(connected) {
  let host = '';
  try { host = /^https?:/i.test(tabUrl || '') ? new URL(tabUrl).hostname : ''; } catch { /* not a web page */ }

  $('site').hidden = !connected || !host;
  if ($('site').hidden) return;

  const { rules } = await api.storage.local.get({ rules: { skipHosts: [] } });
  const site = siteOf(host);
  $('site-host').textContent = site;
  // Same matching as capture itself, so the switch shows what will actually happen.
  $('site-capture').checked = !(rules.skipHosts || []).some((h) => h && (host === h || host.endsWith(`.${h}`) || site === h));
}

$('site-capture').addEventListener('change', async () => {
  const box = $('site-capture');
  const host = $('site-host').textContent;
  box.disabled = true;

  try {
    const result = await api.runtime.sendMessage({ type: 'stretto-skip-host', host, skip: !box.checked });
    if (!result?.ok) throw new Error(result?.error || 'failed');
    setStatus(box.checked
      ? `Downloads from ${host} go to Stretto again.`
      : `Downloads from ${host} stay in the browser.`, 'ok');
  } catch {
    box.checked = !box.checked;
    setStatus('Could not change that. Is Stretto running?', 'bad');
  }

  box.disabled = false;
});

async function grabAll(images) {
  const button = images ? $('all-images') : $('all-links');
  button.disabled = true;
  setStatus(images ? 'Collecting images…' : 'Collecting links…');

  try {
    const result = await api.runtime.sendMessage({ type: 'stretto-grab-all', tabId, images });
    setStatus(result?.message || (result?.ok ? 'Sent to Stretto.' : 'Could not send.'), result?.ok ? '' : 'bad');
  } catch {
    setStatus('Stretto is not reachable.', 'bad');
  }

  button.disabled = false;
}

async function init() {
  try {
    const [tab] = await api.tabs.query({ active: true, currentWindow: true });
    tabId = tab?.id;
    tabUrl = tab?.url;
  } catch {
    tabId = undefined;
    tabUrl = undefined;
  }

  try {
    const host = new URL(tabUrl).hostname.replace(/^www\./i, '');
    $('page-title').textContent = host ? `Media on ${host}` : 'Media on this page';
    $('page-title').title = tabUrl;
  } catch {
    $('page-title').textContent = 'Media on this page';
  }

  // Browser pages cannot be scripted, so there is nothing to collect from them.
  if (!/^https?:/i.test(tabUrl || '')) {
    $('all-links').disabled = true;
    $('all-images').disabled = true;
  }

  const cfg = await api.storage.local.get({ enabled: true, popupShowAll: false });
  $('monitoring').checked = cfg.enabled !== false;
  showAll = cfg.popupShowAll === true;
  $('formats').setAttribute('aria-pressed', String(showAll));

  try {
    const stored = await sessionStore().get({ media: [] });
    items = Array.isArray(stored.media) ? stored.media : [];
  } catch {
    items = [];
  }

  render();
  await showConnection();

  if (!/^https?:/i.test(tabUrl || '')) {
    $('all-links').disabled = true;
    $('all-images').disabled = true;
  }
}

$('monitoring').addEventListener('change', async () => {
  await api.storage.local.set({ enabled: $('monitoring').checked });
  setStatus($('monitoring').checked
    ? 'Downloads from this browser go to Stretto.'
    : 'Downloads stay in the browser until you switch this back on.');
});

$('formats').addEventListener('click', async () => {
  showAll = !showAll;
  $('formats').setAttribute('aria-pressed', String(showAll));
  await api.storage.local.set({ popupShowAll: showAll });
  render();
});

$('clear').addEventListener('click', async () => {
  try {
    await api.runtime.sendMessage({ type: 'stretto-clear', tabId });
  } catch {
    // The worker may be asleep; fall through to the local refresh either way.
  }

  items = tabId == null ? [] : items.filter((m) => m.tabId !== tabId);
  setStatus('');
  render();
});

$('all-links').addEventListener('click', () => grabAll(false));
$('all-images').addEventListener('click', () => grabAll(true));
$('opts').addEventListener('click', () => api.runtime.openOptionsPage());

init();
