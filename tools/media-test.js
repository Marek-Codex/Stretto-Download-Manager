#!/usr/bin/env node
// Exercises the shared Chromium/Firefox media watcher without a browser or dependencies.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const event = () => ({ addListener() {} });
let stored = { media: [] };
const context = vm.createContext({
  URL, console, Date,
  api: {
    storage: { session: { async get() { return structuredClone(stored); }, async set(next) { stored = structuredClone(next); } } },
    tabs: { onUpdated: event(), onRemoved: event() },
    webRequest: { onSendHeaders: event(), onHeadersReceived: event() },
  },
  action: { async setBadgeText() {}, async setBadgeBackgroundColor() {} },
  extensionOf: address => path.posix.extname(new URL(address).pathname).slice(1).toLowerCase(),
});
vm.runInContext(fs.readFileSync(path.join(__dirname, '../extensions/chromium/media.js'), 'utf8'), context);
(async () => {
  await vm.runInContext(`(async () => {
    const reports = [];
    reportMedia = async entry => reports.push(entry);
    nameFor = async () => 'test video';
    if (mediaKind('https://cdn.test/master.m3u8?token=1', '', []) !== 'stream') throw new Error('ordinary HLS not detected');
    if (mediaKind('https://cdn.test/play', 'application/vnd.apple.mpegurl', []) !== 'stream') throw new Error('extensionless HLS not detected');
    if (mediaKind('https://cdn.test/segment.ts', 'video/mp2t', []) !== 'segment') throw new Error('segment shown as complete media');
    if (mediaKind('https://cdn.test/master.mpd?token=1', '', []) !== 'stream') throw new Error('signed DASH not detected');
    const base = { url: 'https://cdn.test/master.m3u8', tabId: 1, kind: 'stream' };
    await recordMedia({ ...base, requestHeaders: { authorization: 'Bearer old' } });
    await recordMedia({ ...base, requestHeaders: { authorization: 'Bearer fresh' } });
    if (reports.length !== 2 || reports[1].requestHeaders.authorization !== 'Bearer fresh') throw new Error('fresh request context not reported');
    await recordMedia({ ...base, requestHeaders: { authorization: 'Bearer fresh' } });
    if (reports.length !== 2) throw new Error('unchanged sighting reported again');
    await Promise.all([
      recordMedia({ ...base, url: 'https://cdn.test/one.m3u8' }),
      recordMedia({ ...base, url: 'https://cdn.test/two.m3u8' }),
    ]);
  })()`, context);
  assert.equal(stored.media.length, 3, 'concurrent sightings are retained');
  console.log('PASS media detection, fresh header reporting, duplicate suppression, concurrent sightings');
})().catch(error => { console.error(error); process.exitCode = 1; });
