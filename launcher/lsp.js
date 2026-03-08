#!/usr/bin/env node
'use strict';

/**
 * Minimal Language Server for Opengrep rule files.
 *
 * On every open/change of a file inside a .dolphin/ directory, runs
 * `opengrep validate` and publishes the resulting diagnostics back to
 * the editor via the LSP textDocument/publishDiagnostics notification.
 *
 * No external npm dependencies — only Node.js built-ins plus the
 * bundled opengrep binary (resolved via ensureBinary() in launcher.js).
 */

const path = require('path');
const fs = require('fs');
const os = require('os');
const childProcess = require('child_process');
const { ensureBinary } = require('./launcher');

// ── JSON-RPC / LSP transport ─────────────────────────────────────────────────

let buf = Buffer.alloc(0);
let opengrepBin = null;

function send(obj) {
  const body = JSON.stringify(obj);
  process.stdout.write(`Content-Length: ${Buffer.byteLength(body)}\r\n\r\n${body}`);
}

process.stdin.on('data', chunk => {
  buf = Buffer.concat([buf, chunk]);
  while (true) {
    // Header ends at \r\n\r\n; read up to 512 bytes to locate it
    const header = buf.toString('ascii', 0, Math.min(buf.length, 512));
    const m = /Content-Length: (\d+)\r\n\r\n/.exec(header);
    if (!m) break;
    const headerLen = m.index + m[0].length;
    const bodyLen = parseInt(m[1], 10);
    if (buf.length < headerLen + bodyLen) break;
    let msg;
    try { msg = JSON.parse(buf.toString('utf8', headerLen, headerLen + bodyLen)); } catch {}
    buf = buf.slice(headerLen + bodyLen);
    if (msg) dispatch(msg);
  }
});

// ── LSP message dispatcher ───────────────────────────────────────────────────

function dispatch(msg) {
  switch (msg.method) {
    case 'initialize':
      send({
        jsonrpc: '2.0', id: msg.id,
        result: { capabilities: { textDocumentSync: 1 /* full */ } },
      });
      break;

    case 'textDocument/didOpen':
      validate(msg.params.textDocument.uri, msg.params.textDocument.text);
      break;

    case 'textDocument/didChange':
      if (msg.params.contentChanges.length > 0)
        validate(msg.params.textDocument.uri, msg.params.contentChanges[0].text);
      break;

    case 'textDocument/didClose':
      // Clear stale diagnostics when the file is closed
      send({
        jsonrpc: '2.0', method: 'textDocument/publishDiagnostics',
        params: { uri: msg.params.textDocument.uri, diagnostics: [] },
      });
      break;

    case 'shutdown':
      send({ jsonrpc: '2.0', id: msg.id, result: null });
      break;

    case 'exit':
      process.exit(0);
  }
}

// ── Validation ───────────────────────────────────────────────────────────────

/** Only validate YAML files that live inside a .dolphin/ directory. */
function isDolphinRulesFile(uri) {
  return uri.includes('/.dolphin/') || uri.includes('\\.dolphin\\');
}

function validate(uri, text) {
  if (!isDolphinRulesFile(uri)) {
    send({
      jsonrpc: '2.0', method: 'textDocument/publishDiagnostics',
      params: { uri, diagnostics: [] },
    });
    return;
  }

  if (!opengrepBin) {
    // Binary not ready yet (still downloading); skip silently
    return;
  }

  const tmp = path.join(os.tmpdir(), `dolphin-lsp-${process.pid}.yaml`);
  try {
    fs.writeFileSync(tmp, text, 'utf8');
    const r = childProcess.spawnSync(
      opengrepBin, ['validate', '--config', tmp],
      { encoding: 'utf8', timeout: 15000 },
    );
    const output = (r.stdout || '') + (r.stderr || '');
    const diagnostics = r.status === 0 ? [] : parseDiagnostics(output);
    send({
      jsonrpc: '2.0', method: 'textDocument/publishDiagnostics',
      params: { uri, diagnostics },
    });
  } catch {
    // Never crash the LSP process; diagnostics simply won't update
  } finally {
    try { fs.unlinkSync(tmp); } catch {}
  }
}

/**
 * Parse `opengrep validate` output into LSP Diagnostic objects.
 *
 * Opengrep (forked from Semgrep) emits errors in the form:
 *
 *   Invalid rule 'no-console-log': missing required field 'message'
 *     --> /tmp/dolphin-lsp-1234.yaml:8:5
 *
 * We try to extract a (line, col) location; if none is found we
 * point to the top of the file so the user always sees something.
 */
function parseDiagnostics(output) {
  const diagnostics = [];
  const lines = output.split('\n');

  for (let i = 0; i < lines.length; i++) {
    const raw = lines[i];
    const trimmed = raw.trim();
    if (!trimmed) continue;

    // Look for "  --> /path/to/file:LINE:COL" location pointers
    const locMatch = /-->\s+\S+:(\d+)(?::(\d+))?/.exec(raw);
    if (locMatch) {
      const lineNum = Math.max(0, parseInt(locMatch[1], 10) - 1);
      const colNum  = locMatch[2] ? Math.max(0, parseInt(locMatch[2], 10) - 1) : 0;
      // The error message is usually the previous non-location line
      const msg = (diagnostics.length > 0 && diagnostics[diagnostics.length - 1].pending)
        ? null  // already has location — skip
        : (lines[i - 1] || '').trim() || trimmed;
      if (diagnostics.length > 0 && diagnostics[diagnostics.length - 1].pending) {
        const d = diagnostics[diagnostics.length - 1];
        d.range = { start: { line: lineNum, character: colNum }, end: { line: lineNum, character: 9999 } };
        delete d.pending;
      } else if (msg) {
        diagnostics.push({
          range: { start: { line: lineNum, character: colNum }, end: { line: lineNum, character: 9999 } },
          severity: 1, source: 'opengrep', message: msg,
        });
      }
      continue;
    }

    // Emit a pending diagnostic for lines that look like error messages
    if (/error|invalid|missing|required|unexpected/i.test(trimmed)) {
      diagnostics.push({
        range: { start: { line: 0, character: 0 }, end: { line: 0, character: 9999 } },
        severity: 1, source: 'opengrep', message: trimmed, pending: true,
      });
    }
  }

  // Finalise any pending diagnostics that never got a location
  for (const d of diagnostics) delete d.pending;

  // Fallback: non-zero exit but no parseable error lines
  if (diagnostics.length === 0 && output.trim()) {
    diagnostics.push({
      range: { start: { line: 0, character: 0 }, end: { line: 0, character: 9999 } },
      severity: 1, source: 'opengrep',
      message: output.trim().split('\n')[0],
    });
  }

  return diagnostics;
}

// ── Bootstrap ────────────────────────────────────────────────────────────────

ensureBinary().then(dolphinPath => {
  const ext = process.platform === 'win32' ? '.exe' : '';
  opengrepBin = path.join(path.dirname(dolphinPath), `opengrep${ext}`);
}).catch(err => {
  process.stderr.write(`[dolphin-lsp] Could not locate opengrep binary: ${err.message}\n`);
  // Keep running — we'll skip diagnostics until opengrepBin is set
});
