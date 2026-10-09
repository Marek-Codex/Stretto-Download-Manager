#!/usr/bin/env node
// Deterministic HTTP server for exercising the download engine.
//
// The body is generated, never stored: byte at offset i is (i % 251). That makes
// correctness checkable at any offset without keeping a reference file around, and
// it means a segment written to the wrong offset is detectable rather than silent.
//
//   node testserver.js [--port 8099] [--size 33554432]
//                      [--no-range]            refuse Range, force single-stream fallback
//                      [--flaky 0.4]           chance each response dies partway through
//                      [--throttle 2000000]    bytes/sec per connection (models CDN caps)
//                      [--slow-first 100000]   bytes/sec for connections starting at byte 0 only
//                      [--stall-first]         a connection starting at byte 0 sends a little, then hangs
//                      [--max-concurrent 4]    answer 503 to ranged requests beyond this many at once

const http = require('http');

const arg = (name, fallback) => {
  const i = process.argv.indexOf(`--${name}`);
  return i >= 0 && process.argv[i + 1] && !process.argv[i + 1].startsWith('--')
    ? Number(process.argv[i + 1]) : fallback;
};
const flag = (name) => process.argv.includes(`--${name}`);

const PORT = arg('port', 8099);
const SIZE = arg('size', 32 * 1024 * 1024);
const FLAKY = arg('flaky', 0);
const THROTTLE = arg('throttle', 0);
const NO_RANGE = flag('no-range');
const MAX_CONCURRENT = arg('max-concurrent', 0);
let active = 0;
const SLOW_FIRST = arg('slow-first', 0);
const STALL_FIRST = flag('stall-first');
const ETAG = '"stretto-test-v1"';
// A fixed, obviously-not-now date so a timestamp test can assert on it.
const LAST_MODIFIED = new Date(Date.UTC(2019, 4, 17, 12, 34, 56)).toUTCString();

const CHUNK = 64 * 1024;

function fill(start, length) {
  const buf = Buffer.allocUnsafe(length);
  for (let i = 0; i < length; i++) buf[i] = (start + i) % 251;
  return buf;
}

const server = http.createServer((req, res) => {
  const range = NO_RANGE ? null : req.headers.range;
  let start = 0;
  let end = SIZE - 1;

  if (range) {
    const m = /^bytes=(\d*)-(\d*)$/.exec(range.trim());
    if (!m) { res.writeHead(416).end(); return; }
    start = m[1] ? parseInt(m[1], 10) : 0;
    end = m[2] ? parseInt(m[2], 10) : SIZE - 1;
    if (start >= SIZE || end >= SIZE || start > end) {
      res.writeHead(416, { 'Content-Range': `bytes */${SIZE}` }).end();
      return;
    }
  }

  // Echo the requested path back as the filename so the client's category detection and
  // name handling can be exercised by just asking for /album.mp3.
  const requested = decodeURIComponent(req.url.split('?')[0].replace(/^\//, '')) || 'stretto-test.bin';

  const length = end - start + 1;

  // A site that allows only so many parallel range requests and turns the rest away.
  // The one-byte probe is not counted: it is over before it could matter.
  if (MAX_CONCURRENT > 0 && range && length > 1) {
    if (active >= MAX_CONCURRENT) { res.writeHead(503).end(); return; }
    active++;
    res.on('close', () => { active--; });
  }

  const headers = {
    'Content-Type': 'application/octet-stream',
    'Content-Length': String(length),
    'ETag': ETAG,
    'Last-Modified': LAST_MODIFIED,
    'Content-Disposition': `attachment; filename="${requested.replace(/"/g, '')}"`,
  };

  if (range) {
    headers['Content-Range'] = `bytes ${start}-${end}/${SIZE}`;
    res.writeHead(206, headers);
  } else {
    if (!NO_RANGE) headers['Accept-Ranges'] = 'bytes';
    res.writeHead(200, headers);
  }

  if (req.method === 'HEAD') { res.end(); return; }

  // A response that dies partway is the single most important case to get right:
  // the client must resume from exactly where it stopped, not restart the segment.
  const dieAt = FLAKY > 0 && Math.random() < FLAKY
    ? start + Math.floor(Math.random() * length)
    : -1;

  // The two faults that only hit the first range: one slow connection among fast ones
  // (what dynamic splitting fixes), and one that goes silent without closing (what the
  // stall timeout fixes). Only ranged requests, so the probe itself is never affected.
  const first = range && start === 0;
  const rate = first && SLOW_FIRST > 0 ? SLOW_FIRST : THROTTLE;
  const stallAt = first && STALL_FIRST ? start + CHUNK : -1;

  let offset = start;
  let closed = false;
  res.on('close', () => { closed = true; });

  const pump = () => {
    while (!closed && offset <= end) {
      if (dieAt >= 0 && offset >= dieAt) {
        res.destroy();
        return;
      }

      if (stallAt >= 0 && offset >= stallAt) return;   // hang: never write, never close

      const size = Math.min(CHUNK, end - offset + 1);
      const ok = res.write(fill(offset, size));
      offset += size;

      if (rate > 0) {
        setTimeout(pump, (size / rate) * 1000);
        return;
      }
      if (!ok) { res.once('drain', pump); return; }
    }
    if (!closed) res.end();
  };

  pump();
});

server.listen(PORT, () => {
  const mode = [
    NO_RANGE ? 'no-range' : 'ranges',
    FLAKY > 0 ? `flaky=${FLAKY}` : null,
    THROTTLE > 0 ? `throttle=${THROTTLE}B/s` : null,
  ].filter(Boolean).join(' ');
  console.log(`testserver :${PORT}  size=${SIZE}  ${mode}`);
});
