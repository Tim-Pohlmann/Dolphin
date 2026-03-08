#!/usr/bin/env node
'use strict';

const https = require('https');
const fs = require('fs');
const path = require('path');
const { spawnSync } = require('child_process');

const PLUGIN_ROOT = process.env.CLAUDE_PLUGIN_ROOT || path.join(__dirname, '..');
const GITHUB_REPO = 'Tim-Pohlmann/Dolphin';

function getVersion() {
  const pluginJsonPath = path.join(PLUGIN_ROOT, '.claude-plugin', 'plugin.json');
  return JSON.parse(fs.readFileSync(pluginJsonPath, 'utf8')).version;
}

function getRid() {
  const p = process.platform;
  const a = process.arch;
  if (p === 'linux'  && a === 'x64')   return { rid: 'linux-x64',   ext: 'tar.gz' };
  if (p === 'linux'  && a === 'arm64') return { rid: 'linux-arm64', ext: 'tar.gz' };
  if (p === 'darwin' && a === 'arm64') return { rid: 'osx-arm64',   ext: 'tar.gz' };
  if (p === 'win32'  && a === 'x64')   return { rid: 'win-x64',     ext: 'zip'    };
  throw new Error(`Unsupported platform: ${p}/${a}. Supported: linux-x64, linux-arm64, osx-arm64, win-x64.`);
}

function download(url, dest) {
  return new Promise((resolve, reject) => {
    function get(u) {
      https.get(u, { headers: { 'User-Agent': 'dolphin-launcher' } }, res => {
        if (res.statusCode === 301 || res.statusCode === 302) {
          res.resume();
          return get(res.headers.location);
        }
        if (res.statusCode !== 200) {
          res.resume();
          return reject(new Error(`HTTP ${res.statusCode} for ${u}`));
        }
        const file = fs.createWriteStream(dest);
        res.pipe(file);
        file.on('finish', () => file.close(resolve));
        file.on('error', reject);
        res.on('error', reject);
      }).on('error', reject);
    }
    get(url);
  });
}

async function ensureBinary() {
  const version = getVersion();
  const { rid, ext } = getRid();
  const isWindows = process.platform === 'win32';
  const binaryName = isWindows ? 'dolphin.exe' : 'dolphin';
  const cacheDir = path.join(PLUGIN_ROOT, 'bin', 'cache', version, rid);
  const binaryPath = path.join(cacheDir, binaryName);

  if (fs.existsSync(binaryPath)) return binaryPath;

  fs.mkdirSync(cacheDir, { recursive: true });

  const archiveName = `dolphin-${version}-${rid}.${ext}`;
  const url = `https://github.com/${GITHUB_REPO}/releases/download/v${version}/${archiveName}`;
  const archivePath = path.join(cacheDir, archiveName);

  process.stderr.write(`[dolphin] Downloading ${archiveName} from GitHub Releases...\n`);
  await download(url, archivePath);

  if (ext === 'tar.gz') {
    spawnSync('tar', ['-xzf', archivePath, '-C', cacheDir], { stdio: 'inherit' });
  } else {
    spawnSync('powershell', ['-NoProfile', '-NonInteractive', '-Command',
      'Expand-Archive', '-Path', archivePath, '-DestinationPath', cacheDir, '-Force'],
      { stdio: 'inherit' });
  }

  fs.unlinkSync(archivePath);

  if (!isWindows) {
    for (const name of ['dolphin', 'opengrep']) {
      const p = path.join(cacheDir, name);
      if (fs.existsSync(p)) fs.chmodSync(p, 0o755);
    }
  }

  return binaryPath;
}

ensureBinary().then(binaryPath => {
  const result = spawnSync(binaryPath, process.argv.slice(2), { stdio: 'inherit' });
  process.exit(result.status ?? 1);
}).catch(err => {
  process.stderr.write(`[dolphin] Fatal: ${err.message}\n`);
  process.exit(2);
});
