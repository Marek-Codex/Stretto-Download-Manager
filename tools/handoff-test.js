#!/usr/bin/env node
// Exercises the handoff endpoint the way the extension does, plus the ways a hostile page
// would. Run with the app open.
//
//   node tools/handoff-test.js [downloadUrl]

const fs = require('fs');
const path = require('path');

const PORT = 9615;
const EXT = 'chrome-extension://abcdefghijklmnopabcdefghijklmnop';
const tokenPath = path.join(process.env.LOCALAPPDATA, 'Stretto', 'pairing.txt');

if (!fs.existsSync(tokenPath)) {
  console.error(`No pairing token at ${tokenPath} — start the app first.`);
  process.exit(1);
}

const token = fs.readFileSync(tokenPath, 'utf8').trim();
const target = process.argv[2];

async function call(label, { p = '/ping', method = 'GET', origin, tok, body }) {
  const headers = {};
  if (origin) headers['Origin'] = origin;
  if (tok) headers['X-Stretto-Token'] = tok;
  if (body) headers['Content-Type'] = 'application/json';

  try {
    const response = await fetch(`http://127.0.0.1:${PORT}${p}`, { method, headers, body });
    const text = await response.text();
    console.log(`  ${String(response.status).padEnd(4)} ${label}  ${text.slice(0, 60)}`);
    return response.status;
  } catch (error) {
    console.log(`  ---  ${label}  ${error.message}`);
    return 0;
  }
}

(async () => {
  console.log(`Checking local handoff on port ${PORT}\n`);
  const check = async (label, request, expected) => {
    const actual = await call(label, request);
    if (actual !== expected) {
      console.error(`FAIL ${label}: expected HTTP ${expected}, got ${actual}`);
      process.exitCode = 1;
    }
  };

  console.log('Should be refused:');
  await check('a web page origin', { origin: 'https://evil.example.com', tok: token }, 403);
  await check('extension origin, wrong token', { origin: EXT, tok: 'x'.repeat(32) }, 401);
  await check('extension origin, no token', { origin: EXT }, 401);

  console.log('\nShould be accepted:');
  await check('preflight', { method: 'OPTIONS', origin: EXT }, 200);
  await check('ping with the real token', { origin: EXT, tok: token }, 200);
  await check('originless request with the real token', { tok: token }, 200);
  console.log('\nInput validation:');
  await check('unknown route (404, not 500)', { p: '/nope', origin: EXT, tok: token }, 404);
  await check('malformed json', {
    p: '/add', method: 'POST', origin: EXT, tok: token, body: '{not json',
  }, 400);
  await check('non-http url', {
    p: '/add', method: 'POST', origin: EXT, tok: token,
    body: JSON.stringify({ url: 'file:///unsupported-test-download.bin' }),
  }, 400);

  if (target) {
    console.log('\nHanding over a real download:');
    await call('add', {
      p: '/add', method: 'POST', origin: EXT, tok: token,
      body: JSON.stringify({
        url: target,
        fileName: null,
        cookie: 'session=abc123',
        referrer: 'https://example.com/downloads',
        userAgent: 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) Stretto-Test',
      }),
    });
  }
})();
