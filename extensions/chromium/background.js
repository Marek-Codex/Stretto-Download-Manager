// Manifest V3 wants one service-worker script, so this only loads the real ones: the code
// shared with the Firefox build, media detection, then Chromium's own way of intercepting
// downloads.
importScripts('common.js', 'media.js', 'capture-chromium.js');
