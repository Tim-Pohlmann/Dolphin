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
  const origRename = fs.renameSync;
  const origCWS = fs.createWriteStream;
  const origGet = https.get;
  const origSpawn = childProcess.spawnSync;

  t.after(() => {
    fs.existsSync = origExists;
    fs.mkdirSync = origMkdir;
    fs.unlinkSync = origUnlink;
    fs.chmodSync = origChmod;
    fs.renameSync = origRename;
    fs.createWriteStream = origCWS;
    https.get = origGet;
    childProcess.spawnSync = origSpawn;
  });

  // existsSync call order in ensureBinary (linux/x64 path):
  //   1. binaryPath     → false  (not cached, trigger download)
  //   2. /usr/bin/tar   → true   (select tar binary)
  //   3. tmpDir/dolphin  → true  (so chmodSync is called)
  //   4. tmpDir/opengrep → true  (so chmodSync is called)
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
  fs.renameSync = () => {};
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

test('ensureBinary uses PowerShell to extract zip on Windows', async (t) => {
  const origPlatform = process.platform;
  const origArch = process.arch;
  const origExists = fs.existsSync;
  const origMkdir = fs.mkdirSync;
  const origUnlink = fs.unlinkSync;
  const origRename = fs.renameSync;
  const origCWS = fs.createWriteStream;
  const origGet = https.get;
  const origSpawn = childProcess.spawnSync;

  t.after(() => {
    Object.defineProperty(process, 'platform', { value: origPlatform, configurable: true });
    Object.defineProperty(process, 'arch', { value: origArch, configurable: true });
    fs.existsSync = origExists;
    fs.mkdirSync = origMkdir;
    fs.unlinkSync = origUnlink;
    fs.renameSync = origRename;
    fs.createWriteStream = origCWS;
    https.get = origGet;
    childProcess.spawnSync = origSpawn;
  });

  Object.defineProperty(process, 'platform', { value: 'win32', configurable: true });
  Object.defineProperty(process, 'arch', { value: 'x64', configurable: true });

  let call = 0;
  fs.existsSync = () => { call++; return call !== 1; /* first=not cached */ };
  fs.mkdirSync = () => {};
  fs.unlinkSync = () => {};
  fs.renameSync = () => {};
  fs.createWriteStream = () => makeSinkStream();
  https.get = (url, opts, cb) => { cb(makeResponse(200, 'archive')); return { on: () => {} }; };

  let capturedArgs;
  childProcess.spawnSync = (...args) => { capturedArgs = args; return { status: 0 }; };

  const result = await ensureBinary();
  assert.ok(result.endsWith('dolphin.exe'), `Expected .exe, got: ${result}`);
  assert.ok(capturedArgs[0].toLowerCase().includes('powershell'), 'should invoke PowerShell');
  assert.ok(capturedArgs[1].includes('-Command'), 'should pass -Command');
  assert.ok(capturedArgs[1].some(a => a.includes('Expand-Archive')), 'should call Expand-Archive');
});

test('ensureBinary throws when PowerShell extraction fails', async (t) => {
  const origPlatform = process.platform;
  const origArch = process.arch;
  const origExists = fs.existsSync;
  const origMkdir = fs.mkdirSync;
  const origCWS = fs.createWriteStream;
  const origGet = https.get;
  const origSpawn = childProcess.spawnSync;

  t.after(() => {
    Object.defineProperty(process, 'platform', { value: origPlatform, configurable: true });
    Object.defineProperty(process, 'arch', { value: origArch, configurable: true });
    fs.existsSync = origExists;
    fs.mkdirSync = origMkdir;
    fs.createWriteStream = origCWS;
    https.get = origGet;
    childProcess.spawnSync = origSpawn;
  });

  Object.defineProperty(process, 'platform', { value: 'win32', configurable: true });
  Object.defineProperty(process, 'arch', { value: 'x64', configurable: true });

  let call = 0;
  fs.existsSync = () => { call++; return false; };
  fs.mkdirSync = () => {};
  fs.createWriteStream = () => makeSinkStream();
  https.get = (url, opts, cb) => { cb(makeResponse(200, 'archive')); return { on: () => {} }; };
  childProcess.spawnSync = () => ({ status: 1 });

  await assert.rejects(ensureBinary(), /Failed to extract archive with PowerShell/);
});

test('ensureBinary handles concurrent install: rename fails but binary exists', async (t) => {
  const origExists = fs.existsSync;
  const origMkdir = fs.mkdirSync;
  const origUnlink = fs.unlinkSync;
  const origChmod = fs.chmodSync;
  const origRename = fs.renameSync;
  const origRmSync = fs.rmSync;
  const origCWS = fs.createWriteStream;
  const origGet = https.get;
  const origSpawn = childProcess.spawnSync;

  t.after(() => {
    fs.existsSync = origExists;
    fs.mkdirSync = origMkdir;
    fs.unlinkSync = origUnlink;
    fs.chmodSync = origChmod;
    fs.renameSync = origRename;
    fs.rmSync = origRmSync;
    fs.createWriteStream = origCWS;
    https.get = origGet;
    childProcess.spawnSync = origSpawn;
  });

  // existsSync call order:
  //   1. binaryPath       → false  (not cached at start)
  //   2. /usr/bin/tar     → true
  //   3. tmpDir/dolphin   → true   (chmod)
  //   4. tmpDir/opengrep  → true   (chmod)
  //   5. binaryPath       → true   (another process installed it while we worked)
  let existsCallCount = 0;
  fs.existsSync = (p) => {
    existsCallCount++;
    if (existsCallCount === 1) return false; // binaryPath: not cached initially
    if (p === '/usr/bin/tar') return true;
    if (p === '/bin/tar') return false;
    return true; // chmod targets (calls 3-4) and final binaryPath check (call 5)
  };

  fs.mkdirSync = () => {};
  fs.unlinkSync = () => {};
  fs.chmodSync = () => {};
  fs.createWriteStream = () => makeSinkStream();
  https.get = (url, opts, cb) => { cb(makeResponse(200, 'archive')); return { on: () => {} }; };
  childProcess.spawnSync = () => ({ status: 0 });

  // Simulate rename failing because another process already moved its copy.
  fs.renameSync = () => { throw Object.assign(new Error('EEXIST'), { code: 'EEXIST' }); };

  let rmCalled = false;
  fs.rmSync = () => { rmCalled = true; };

  const result = await ensureBinary();
  assert.ok(typeof result === 'string');
  assert.ok(result.endsWith('dolphin') || result.endsWith('dolphin.exe'));
  assert.ok(rmCalled, 'tmpDir should be cleaned up when rename fails');
});

test('ensureBinary throws when rename fails and binary is still missing', async (t) => {
  const origExists = fs.existsSync;
  const origMkdir = fs.mkdirSync;
  const origUnlink = fs.unlinkSync;
  const origChmod = fs.chmodSync;
  const origRename = fs.renameSync;
  const origRmSync = fs.rmSync;
  const origCWS = fs.createWriteStream;
  const origGet = https.get;
  const origSpawn = childProcess.spawnSync;

  t.after(() => {
    fs.existsSync = origExists;
    fs.mkdirSync = origMkdir;
    fs.unlinkSync = origUnlink;
    fs.chmodSync = origChmod;
    fs.renameSync = origRename;
    fs.rmSync = origRmSync;
    fs.createWriteStream = origCWS;
    https.get = origGet;
    childProcess.spawnSync = origSpawn;
  });

  let call = 0;
  fs.existsSync = (p) => {
    call++;
    if (call === 1) return false; // binaryPath: not cached
    if (p === '/usr/bin/tar') return true;
    if (p === '/bin/tar') return false;
    return false; // binary still missing after rename failure
  };

  fs.mkdirSync = () => {};
  fs.unlinkSync = () => {};
  fs.chmodSync = () => {};
  fs.createWriteStream = () => makeSinkStream();
  https.get = (url, opts, cb) => { cb(makeResponse(200, 'archive')); return { on: () => {} }; };
  childProcess.spawnSync = () => ({ status: 0 });
  fs.renameSync = () => { throw Object.assign(new Error('ENOTEMPTY'), { code: 'ENOTEMPTY' }); };
  fs.rmSync = () => {};

  await assert.rejects(ensureBinary(), /Binary missing after concurrent install attempt/);
});

test('ensureBinary cleans up cacheDir when download fails', async (t) => {
  const origExists = fs.existsSync;
  const origMkdir = fs.mkdirSync;
  const origRmSync = fs.rmSync;
  const origCWS = fs.createWriteStream;
  const origGet = https.get;

  t.after(() => {
    fs.existsSync = origExists;
    fs.mkdirSync = origMkdir;
    fs.rmSync = origRmSync;
    fs.createWriteStream = origCWS;
    https.get = origGet;
  });

  fs.existsSync = () => false;
  fs.mkdirSync = () => {};

  let rmCalled = false;
  fs.rmSync = (p, opts) => { rmCalled = true; };

  // Simulate a write stream that errors during the download
  fs.createWriteStream = () => {
    const ws = new Writable({ write(chunk, enc, cb) { cb(); } });
    return ws;
  };

  https.get = (url, opts, cb) => {
    const res = new PassThrough();
    res.statusCode = 500;
    cb(res);
    res.resume();
    return { on: () => {} };
  };

  await assert.rejects(ensureBinary(), /HTTP 500/);
  assert.ok(rmCalled, 'cacheDir should be cleaned up after download failure');
});
