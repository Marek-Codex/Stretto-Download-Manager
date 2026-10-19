// Checks how the extension names what it sends, with no browser and no website: the real
// scripts run against a fake browser and a fake app, with a made-up tab title and a hash-named
// file. Run with `node extensions/naming-check.js`; it exits non-zero if a name is wrong.
const fs = require('fs');
const vm = require('vm');
const dir = require('path').join(__dirname, 'chromium') + '/';

function run({ nameFrom, tabs, scenario }) {
  const sent = [];
  const logs = [];
  const listeners = {};
  const store = {
    port: 9615, token: 't', enabled: true,
    rules: {
      captureTypes: ['zip', 'mp4', 'exe'], videoTypes: ['mp4', 'm3u8', 'webm'], mediaTypes: [],
      minVideoKb: 0, skipHosts: [], alwaysHosts: [], ...(nameFrom ? { nameFrom } : {}),
    },
  };

  const listenerSink = (name) => ({ addListener: (fn) => { listeners[name] = fn; } });
  const deep = () => new Proxy(function () {}, {
    get: (_t, p) => (p === 'then' ? undefined : p === 'addListener' ? () => {} : deep()),
    apply: () => Promise.resolve(undefined),
  });

  const fb = (o) => new Proxy(o, { get: (t, p) => (p in t ? t[p] : deep()) });
  const chrome = new Proxy({
    storage: fb({
      local: {
        get: async (d) => ({ ...d, ...store }),
        set: async (o) => { Object.assign(store, o); },
      },
      session: undefined,
      onChanged: { addListener() {} },
    }),
    tabs: fb({
      get: async (id) => { const t = tabs.find((x) => x.id === id); if (!t) throw new Error('no tab'); return t; },
      query: async (q = {}) => tabs.filter((x) => q.active === undefined || x.active === q.active),
      onUpdated: listenerSink('tabs.onUpdated'),
    }),
    cookies: { getAll: async () => [] },
    downloads: fb({
      onDeterminingFilename: listenerSink('onDeterminingFilename'),
      cancel: async () => {}, erase: async () => {}, download: async () => 1,
    }),
    runtime: fb({
      onMessage: listenerSink('onMessage'), onInstalled: listenerSink('onInstalled'),
      onStartup: listenerSink('onStartup'), getPlatformInfo: async () => ({}),
    }),
    alarms: fb({ create: () => {}, onAlarm: listenerSink('onAlarm') }),
  }, { get: (t, p) => (p in t ? t[p] : deep()) });

  const ctx = {
    chrome, console: { info: (...a) => logs.push(a.join(' ')), log() {}, warn() {}, error() {} },
    navigator: { userAgent: 'UA' }, setInterval, clearInterval, setTimeout, clearTimeout, URL, AbortSignal,
    Date, Promise, JSON, Math, Map, Set, Array, Object, String, Number, RegExp, Error,
    fetch: async (url, opts = {}) => {
      const path = new URL(url).pathname;
      const reply = (o) => ({ ok: true, json: async () => o });
      if (path === '/add') { sent.push(JSON.parse(opts.body)); return reply({ action: 'taken' }); }
      if (path === '/rules') return reply(store.rules);
      return reply({ ok: true });
    },
    importScripts() {},
  };
  ctx.globalThis = ctx;
  vm.createContext(ctx);
  for (const f of ['common.js', 'media.js', 'capture-chromium.js'])
    vm.runInContext(fs.readFileSync(dir + f, 'utf8'), ctx, { filename: f });

  return { ctx, sent, logs, listeners };
}

const tabsFor = (url, title) => [
  { id: 1, active: false, url: 'https://other.example/', title: 'Something else' },
  { id: 5, active: true, url, title },
];

let failed = 0;
const expect = (label, got, want) => {
  const ok = got === want;
  if (!ok) failed++;
  console.log(`${ok ? 'PASS' : 'FAIL'} ${label}: ${JSON.stringify(got)}${ok ? '' : ` (wanted ${JSON.stringify(want)})`}`);
};

(async () => {
  const pageTitle = 'Funny Cat Video - Site';
  const page = 'https://www.site.com/video/9981/funny-cat';
  const cdn = 'https://b-cdn.site-cdn.com/files/26e2fb0b265b99d.mp4';

  // 1. The popup's send (sendMedia).
  {
    const h = run({ tabs: tabsFor(page, pageTitle) });
    const r = await h.ctx.sendMedia({ url: cdn, tabUrl: page, tabId: 5, kind: 'video' });
    expect('popup send names it after the tab', h.sent[0]?.fileName, 'Funny Cat Video.mp4');
  }

  // 2. A download the browser started, with only the origin as referrer (cross-host).
  {
    const h = run({ tabs: tabsFor(page, pageTitle) });
    const item = { id: 7, url: cdn, finalUrl: cdn, filename: 'C:/Users/x/Downloads/26e2fb0b265b99d.mp4', referrer: 'https://www.site.com/', totalBytes: 5e6 };
    await new Promise((resolve) => {
      const ret = h.listeners.onDeterminingFilename(item, () => {});
      setTimeout(resolve, 200);
    });
    expect('download with only the origin as referrer', h.sent[0]?.fileName, 'Funny Cat Video.mp4');
  }

  // 3. Same, with a full-page referrer.
  {
    const h = run({ tabs: tabsFor(page, pageTitle) });
    const item = { id: 7, url: cdn, finalUrl: cdn, filename: '26e2fb0b265b99d.mp4', referrer: page, totalBytes: 5e6 };
    await new Promise((resolve) => { h.listeners.onDeterminingFilename(item, () => {}); setTimeout(resolve, 200); });
    expect('download with the page as referrer', h.sent[0]?.fileName, 'Funny Cat Video.mp4');
  }

  // 4. No referrer at all, which some sites and policies produce.
  {
    const h = run({ tabs: tabsFor(page, pageTitle) });
    const item = { id: 7, url: cdn, finalUrl: cdn, filename: '26e2fb0b265b99d.mp4', referrer: '', totalBytes: 5e6 };
    await new Promise((resolve) => { h.listeners.onDeterminingFilename(item, () => {}); setTimeout(resolve, 200); });
    expect('download with no referrer uses the tab in front', h.sent[0]?.fileName, 'Funny Cat Video.mp4');
  }

  // 5. The file's own name is requested.
  {
    const h = run({ nameFrom: 'File', tabs: tabsFor(page, pageTitle) });
    await h.ctx.sendMedia({ url: cdn, tabUrl: page, tabId: 5, kind: 'video' });
    expect('the File setting keeps the address name', h.sent[0]?.fileName, '26e2fb0b265b99d.mp4');
  }
  process.exit(failed ? 1 : 0);
})();
