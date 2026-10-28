#!/usr/bin/env node
// Chromium handoff regressions. Uses the actual event listeners and asynchronous API
// boundaries; never opens a real download or contacts a remote site.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const source = fs.readFileSync(path.join(__dirname, '../extensions/chromium/capture-chromium.js'), 'utf8');

function environment(options = {}, stored = {}) {
  let request, filename;
  const calls = [];
  const cfg = { enabled: !options.disabled, rules: { skipHosts: [] } };
  const session = {
    async get(defaults) {
      if (options.storageReadFails) throw new Error('storage unavailable');
      return { ...structuredClone(defaults), ...structuredClone(stored) };
    },
    async set(update) {
      if (options.storageWriteFails) throw new Error('storage unavailable');
      Object.assign(stored, structuredClone(update));
    },
  };
  const context = vm.createContext({
    URL, Date, console: { info() {} }, navigator: { userAgent: 'test' },
    api: {
      storage: { session },
      webRequest: { onBeforeRequest: { addListener(fn) { request = fn; } } },
      downloads: {
        onDeterminingFilename: { addListener(fn) { filename = fn; } },
        async cancel(id) { calls.push(['cancel', id]); if (options.cancelFails) throw new Error('cannot cancel'); },
        async erase(query) { calls.push(['erase', query.id]); if (options.eraseFails) throw new Error('cannot erase'); },
        async download() { calls.push(['restart']); throw new Error('must never replay the browser request'); },
      },
    },
    async config() { if (options.configFails) throw new Error('bad config'); return cfg; },
    async connectWithRules() { return options.offline ? null : cfg; },
    hostSkipped() { return !!options.skipHost; },
    async ensureConnection() { return cfg; }, async refreshRules() {},
    shouldSkip() { return !!options.skipRule; }, extensionOf() { return 'bin'; },
    async cookieHeaderFor() { if (options.cookieFails) throw new Error('cookie read failed'); return ''; },
    async sendToApp() {
      calls.push(['ask']);
      // Acceptance must be resolved before cancelling the original.
      assert.equal(calls.filter(call => call[0] === 'cancel').length, 0);
      if (options.answer) return options.answer();
      if (options.appFails) throw new Error('offline during handoff');
      return !!options.accept;
    },
    notify(text) { calls.push(['notice', text]); }, notifyOffline() { calls.push(['offline']); },
  });
  vm.runInContext(source, context);
  const url = 'https://download.test/one-use.bin?signature=test';
  const now = Date.now();
  return {
    calls, stored,
    record(method = 'GET', address = url) { request({ url: address, method, timeStamp: now }); },
    async flush() { await vm.runInContext('requestHistoryWrites', context); },
    async run(overrides = {}) {
      let suggestions = 0;
      let finish;
      const done = new Promise(resolve => { finish = resolve; });
      const held = filename({ id: 17, url, filename: 'one-use.bin', totalBytes: 1000,
        startTime: new Date(now).toISOString(), ...overrides }, () => { suggestions++; finish(); });
      if (held) {
        let timer;
        try { await Promise.race([done, new Promise((_, reject) => { timer = setTimeout(() => reject(new Error('download left held')), 1000); })]); }
        finally { clearTimeout(timer); }
      }
      await Promise.resolve();
      return { held, suggestions };
    },
  };
}

(async () => {
  for (const method of ['POST', 'PUT', 'HEAD', null]) {
    const env = environment({ accept: true });
    if (method) env.record(method);
    const result = await env.run();
    assert.equal(result.suggestions, 1);
    assert.equal(env.calls.length, 0, `${method || 'unknown'} remains with browser without app handoff`);
  }
  for (const options of [{}, { offline: true }, { disabled: true }, { skipHost: true },
    { skipRule: true }, { appFails: true }, { configFails: true }, { cookieFails: true },
    { storageReadFails: true }, { storageWriteFails: true }]) {
    const env = environment(options);
    env.record();
    assert.equal((await env.run()).suggestions, 1);
    assert.equal(env.calls.filter(call => ['cancel', 'erase', 'restart'].includes(call[0])).length, 0,
      `original preserved for ${JSON.stringify(options)}`);
  }
  for (const options of [{ accept: true }, { accept: true, eraseFails: true }]) {
    const env = environment(options);
    env.record();
    assert.equal((await env.run()).suggestions, 1);
    assert.equal(JSON.stringify(env.calls), JSON.stringify([['ask'], ['cancel', 17], ['erase', 17]]));
  }
  const failedCancel = environment({ accept: true, cancelFails: true });
  failedCancel.record();
  assert.equal((await failedCancel.run()).suggestions, 1);
  assert.equal(failedCancel.calls.filter(call => call[0] === 'erase' || call[0] === 'restart').length, 0);
  assert.ok(failedCancel.calls.some(call => call[0] === 'notice'));

  let answer;
  const pending = environment({ answer: () => new Promise(resolve => { answer = resolve; }) });
  pending.record();
  const completion = pending.run();
  await new Promise(resolve => setImmediate(resolve));
  assert.equal(JSON.stringify(pending.calls), JSON.stringify([['ask']]), 'confirmation keeps original alive');
  answer(false);
  assert.equal((await completion).suggestions, 1);

  const originalWorker = environment({ accept: true });
  originalWorker.record();
  await originalWorker.flush();
  const restartedWorker = environment({ accept: true }, originalWorker.stored);
  await restartedWorker.run();
  assert.ok(restartedWorker.calls.some(call => call[0] === 'ask'), 'request method survives worker restart');

  const ambiguous = environment({ accept: true });
  ambiguous.record('GET'); ambiguous.record('POST');
  await ambiguous.run();
  assert.equal(ambiguous.calls.length, 0, 'ambiguous same-URL requests remain with browser');
  const old = environment({ accept: true });
  old.record();
  await old.run({ startTime: new Date(Date.now() + 60000).toISOString() });
  assert.equal(old.calls.length, 0, 'old metadata is not assigned to a later download');
  console.log('PASS Chromium POST/unknown preservation, decline/offline/error fallback, deferred acceptance, cancellation, worker restart and ambiguous request history');
})().catch(error => { console.error(error); process.exitCode = 1; });
