'use strict';

const { test } = require('node:test');
const assert = require('node:assert/strict');
const path = require('path');
const { getVersion, getRid } = require('./launcher');

const PLUGIN_ROOT = path.join(__dirname, '..');

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
