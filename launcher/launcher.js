#!/usr/bin/env node
'use strict';

const https = require('https');
const fs = require('fs');
const path = require('path');
const childProcess = require('child_process');

const PLUGIN_ROOT = process.env.CLAUDE_PLUGIN_ROOT || path.join(__dirname, '..');
const GITHUB_REPO = 'Tim-Pohlmann/Dolphin';

function getVersion(pluginRoot = PLUGIN_ROOT) {
  const pluginJsonPath = path.join(pluginRoot, '.claude-plugin', 'plugin.json');
  return JSON.parse(fs.readFileSync(pluginJsonPath, 'utf8')).version;
}

function getRid(platform = process.platform, arch = process.arch) {
  if (platform === 'linux'  && arch === 'x64')   return { rid: 'linux-x64',   ext: 'tar.gz' };
  if (platform === 'linux'  && arch === 'arm64') return { rid: 'linux-arm64', ext: 'tar.gz' };
  if (platform === 'darwin' && arch === 'arm64') return { rid: 'osx-arm64',   ext: 'tar.gz' };
  if (platform === 'win32'  && arch === 'x64')   return { rid: 'win-x64',     ext: 'zip'    };
  throw new Error(`Unsupported platform: ${platform}/${arch}. Supported: linux-x64, linux-arm64, osx-arm64, win-x64.`);
}

function download(url, dest) {
  return new Promise((resolve, reject) => {
    function get(u, remainingRedirects = 10) {
      https.get(u, { headers: { 'User-Agent': 'dolphin-launcher' } }, res => {
        if (res.statusCode === 301 || res.statusCode === 302) {
          res.resume();
          if (remainingRedirects <= 0) {
            return reject(new Error(`Too many redirects while fetching ${url}`));
          }
          return get(res.headers.location, remainingRedirects - 1);
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

  try {
    await download(url, archivePath);

    if (ext === 'tar.gz') {
      const tar = fs.existsSync('/usr/bin/tar') ? '/usr/bin/tar' : '/bin/tar';
      const result = childProcess.spawnSync(tar, ['-xzf', archivePath, '-C', cacheDir], { stdio: 'inherit' });
      if (result.error || result.status !== 0) {
        throw new Error(`[dolphin] Failed to extract archive with tar (exit code ${result.status ?? 'unknown'}).`);
      }
    } else {
      const ps = path.join(process.env.SystemRoot || 'C:\\Windows',
        'System32\\WindowsPowerShell\\v1.0\\powershell.exe');
      const q = (p) => `'${p.replace(/'/g, "''")}'`;
      const cmd = `Expand-Archive -LiteralPath ${q(archivePath)} -DestinationPath ${q(cacheDir)} -Force`;
      const result = childProcess.spawnSync(ps, ['-NoProfile', '-NonInteractive', '-Command', cmd], { stdio: 'inherit' });
      if (result.error || result.status !== 0) {
        throw new Error(`[dolphin] Failed to extract archive with PowerShell (exit code ${result.status ?? 'unknown'}).`);
      }
    }
  } catch (err) {
    try { fs.rmSync(cacheDir, { recursive: true, force: true }); } catch {}
    throw err;
  }

  fs.unlinkSync(archivePath);

  if (!isWindows) {
    for (const name of ['dolphin', 'opengrep']) {
      const p = path.join(cacheDir, name);
      if (fs.existsSync(p)) fs.chmodSync(p, 0o750);
    }
  }

  return binaryPath;
}

if (require.main === module) {
  ensureBinary().then(binaryPath => {
    const result = childProcess.spawnSync(binaryPath, process.argv.slice(2), { stdio: 'inherit' });
    if (typeof result.status === 'number') {
      process.exit(result.status);
    } else if (result.signal) {
      process.kill(process.pid, result.signal);
    } else {
      process.exit(1);
    }
  }).catch(err => {
    process.stderr.write(`[dolphin] Fatal: ${err.message}\n`);
    process.exit(2);
  });
}

module.exports = { getVersion, getRid, download, ensureBinary };
