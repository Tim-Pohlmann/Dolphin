'use strict';

const { test } = require('node:test');
const assert = require('node:assert/strict');
const path = require('path');
const { PassThrough, Writable } = require('stream');
const https = require('https');
const fs = require('fs');
const childProcess = require('child_process');
const { getVersion, getRid, download, ensureBinary } = require('./launcher');

const PLUGIN_ROOT = path.join(__dirname, '..');

// ---------------------------------------------------------------------------
// getVersion / getRid
// ---------------------------------------------------------------------------

test('getVersion returns semver string from plugin.json', () => {
  assert.match(getVersion(PLUGIN_ROOT), /^\d+\.\d+\.\d+$/);
});

test('getRid linux/x64', () => {
  assert.deepEqual(getRid('linux', 'x64'), { rid: 'linux-x64', ext: 'tar.gz' });
});

test('getRid linux/arm64', () => {
  assert.deepEqual(getRid('linux', 'arm64'), { rid: 'linux-arm64', ext: 'tar.gz' });
});

test('getRid darwin/arm64', () => {
  assert.deepEqual(getRid('darwin', 'arm64'), { rid: 'osx-arm64', ext: 'tar.gz' });
});

test('getRid win32/x64', () => {
  assert.deepEqual(getRid('win32', 'x64'), { rid: 'win-x64', ext: 'zip' });
});

test('getRid unsupported platform throws', () => {
  assert.throws(() => getRid('darwin', 'x64'), /Unsupported platform/);
});

// ---------------------------------------------------------------------------
// Helpers for download / ensureBinary tests
// ---------------------------------------------------------------------------

// A Writable that discards all data (no readable side to block on).
function makeSinkStream() {
  const ws = new Writable({ write(chunk, enc, cb) { cb(); } });
  ws.close = (cb) => { if (cb) cb(); };
  return ws;
}

// Fake https response. Data is emitted in a nextTick so pipe is set up first.
function makeResponse(statusCode, body, headers = {}) {
  const res = new PassThrough();
  res.statusCode = statusCode;
  res.headers = headers;
  process.nextTick(() => { if (body) res.write(body); res.end(); });
  return res;
}

// ---------------------------------------------------------------------------
// download
// ---------------------------------------------------------------------------

test('download resolves and writes data on 200', async (t) => {
  const origGet = https.get;
  const origCWS = fs.createWriteStream;
  t.after(() => { https.get = origGet; fs.createWriteStream = origCWS; });

  fs.createWriteStream = () => makeSinkStream();
  https.get = (url, opts, cb) => {
    cb(makeResponse(200, 'bytes'));
    return { on: () => {} };
  };

  await download('https://example.com/f', '/tmp/out');
});

test('download follows 301 redirect', async (t) => {
  const origGet = https.get;
  const origCWS = fs.createWriteStream;
  t.after(() => { https.get = origGet; fs.createWriteStream = origCWS; });

  fs.createWriteStream = () => makeSinkStream();
  let call = 0;
  https.get = (url, opts, cb) => {
    call++;
    if (call === 1) {
      cb(makeResponse(301, null, { location: 'https://example.com/redirected' }));
    } else {
      cb(makeResponse(200, 'ok'));
    }
    return { on: () => {} };
  };

  await download('https://example.com/f', '/tmp/out');
  assert.equal(call, 2);
});

test('download follows 302 redirect', async (t) => {
  const origGet = https.get;
  const origCWS = fs.createWriteStream;
  t.after(() => { https.get = origGet; fs.createWriteStream = origCWS; });

  fs.createWriteStream = () => makeSinkStream();
  let call = 0;
  https.get = (url, opts, cb) => {
    call++;
    if (call === 1) {
      cb(makeResponse(302, null, { location: 'https://example.com/r2' }));
    } else {
      cb(makeResponse(200, 'ok'));
    }
    return { on: () => {} };
  };

  await download('https://example.com/f', '/tmp/out');
  assert.equal(call, 2);
});

test('download rejects on non-200 status', async (t) => {
  const origGet = https.get;
  t.after(() => { https.get = origGet; });

  https.get = (url, opts, cb) => {
    cb(makeResponse(404, null));
    return { on: () => {} };
  };

  await assert.rejects(
    download('https://example.com/f', '/tmp/out'),
    /HTTP 404/
  );
});

test('download rejects on network error', async (t) => {
  const origGet = https.get;
  t.after(() => { https.get = origGet; });

  https.get = (url, opts, cb) => {
    const req = new PassThrough();
    process.nextTick(() => req.emit('error', new Error('ECONNREFUSED')));
    return req;
  };

  await assert.rejects(
    download('https://example.com/f', '/tmp/out'),
    /ECONNREFUSED/
  );
});

test('download rejects after too many redirects', async (t) => {
  const origGet = https.get;
  t.after(() => { https.get = origGet; });

  https.get = (url, opts, cb) => {
    cb(makeResponse(301, null, { location: url })); // always redirect to same URL
    return { on: () => {} };
  };

  await assert.rejects(
    download('https://example.com/f', '/tmp/out'),
    /Too many redirects/
  );
});


test('ensureBinary returns cached path when binary exists', async (t) => {
  const origExists = fs.existsSync;
  t.after(() => { fs.existsSync = origExists; });

  // First call (binary path check) → true (already cached).
  let first = true;
  fs.existsSync = (p) => {
    if (first) { first = false; return true; }
    return origExists(p);
  };

  const result = await ensureBinary();
  assert.ok(typeof result === 'string');
  assert.ok(result.endsWith('dolphin') || result.endsWith('dolphin.exe'));
});

test('ensureBinary downloads and extracts tar.gz when binary is missing', async (t) => {
  const origExists = fs.existsSync;
  const origMkdir = fs.mkdirSync;
  const origUnlink = fs.unlinkSync;
  const origChmod = fs.chmodSync;
  const origCWS = fs.createWriteStream;
  const origGet = https.get;
  const origSpawn = childProcess.spawnSync;

  t.after(() => {
    fs.existsSync = origExists;
    fs.mkdirSync = origMkdir;
    fs.unlinkSync = origUnlink;
    fs.chmodSync = origChmod;
    fs.createWriteStream = origCWS;
    https.get = origGet;
    childProcess.spawnSync = origSpawn;
  });

  // existsSync call order in ensureBinary (linux/x64 path):
  //   1. binaryPath     → false  (not cached, trigger download)
  //   2. /usr/bin/tar   → true   (select tar binary)
  //   3. cacheDir/dolphin  → true  (so chmodSync is called)
  //   4. cacheDir/opengrep → true  (so chmodSync is called)
  let call = 0;
  fs.existsSync = (p) => {
    call++;
    if (call === 1) return false;
    if (p === '/usr/bin/tar') return true;
    if (p === '/bin/tar') return false;
    return true; // chmod targets
  };

  fs.mkdirSync = () => {};
  fs.unlinkSync = () => {};
  fs.chmodSync = () => {};
  fs.createWriteStream = () => makeSinkStream();

  https.get = (url, opts, cb) => {
    cb(makeResponse(200, 'archive'));
    return { on: () => {} };
  };

  let spawnCalls = 0;
  childProcess.spawnSync = (...args) => { spawnCalls++; return { status: 0 }; };

  const result = await ensureBinary();
  assert.ok(typeof result === 'string');
  assert.ok(result.endsWith('dolphin') || result.endsWith('dolphin.exe'));
  assert.equal(spawnCalls, 1, 'spawnSync should have been called for extraction');
});

test('ensureBinary throws when tar extraction fails', async (t) => {
  const origExists = fs.existsSync;
  const origMkdir = fs.mkdirSync;
  const origCWS = fs.createWriteStream;
  const origGet = https.get;
  const origSpawn = childProcess.spawnSync;

  t.after(() => {
    fs.existsSync = origExists;
    fs.mkdirSync = origMkdir;
    fs.createWriteStream = origCWS;
    https.get = origGet;
    childProcess.spawnSync = origSpawn;
  });

  let call = 0;
  fs.existsSync = (p) => {
    call++;
    if (call === 1) return false;
    if (p === '/usr/bin/tar') return true;
    return false;
  };

  fs.mkdirSync = () => {};
  fs.createWriteStream = () => makeSinkStream();

  https.get = (url, opts, cb) => {
    cb(makeResponse(200, 'archive'));
    return { on: () => {} };
  };

  childProcess.spawnSync = () => ({ status: 1 });

  await assert.rejects(ensureBinary(), /Failed to extract archive with tar/);
});
