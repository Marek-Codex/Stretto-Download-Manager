#!/usr/bin/env node
// Checks a downloaded file against the testserver pattern (byte i == i % 251).
// Reports the first mismatching offset, which localises a misplaced segment write.

const fs = require('fs');

const path = process.argv[2];
if (!path) { console.error('usage: verify.js <file> [expectedSize]'); process.exit(2); }

const expected = process.argv[3] ? Number(process.argv[3]) : null;
const stat = fs.statSync(path);

if (expected !== null && stat.size !== expected) {
  console.error(`FAIL size: got ${stat.size}, expected ${expected}`);
  process.exit(1);
}

const fd = fs.openSync(path, 'r');
const buf = Buffer.allocUnsafe(1 << 20);
let offset = 0;

while (offset < stat.size) {
  const read = fs.readSync(fd, buf, 0, buf.length, offset);
  if (read === 0) break;
  for (let i = 0; i < read; i++) {
    if (buf[i] !== (offset + i) % 251) {
      console.error(`FAIL at byte ${offset + i}: got ${buf[i]}, expected ${(offset + i) % 251}`);
      process.exit(1);
    }
  }
  offset += read;
}

fs.closeSync(fd);
console.log(`OK ${stat.size} bytes verified`);
