/**
 * Test-only support helpers for the OVERSEER Claude Code hook bridge.
 * Not imported by bridge.mjs, install.mjs or uninstall.mjs.
 *
 * Tests use real child processes and real temporary directories; nothing here
 * touches a real ~/.claude or the production spool.
 */

import { spawn } from 'node:child_process';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

export const INTEGRATION_ROOT = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
export const BRIDGE_PATH = path.join(INTEGRATION_ROOT, 'bridge.mjs');
export const INSTALL_PATH = path.join(INTEGRATION_ROOT, 'install.mjs');
export const UNINSTALL_PATH = path.join(INTEGRATION_ROOT, 'uninstall.mjs');
export const FIXTURES_DIR = path.join(INTEGRATION_ROOT, 'fixtures');

export function tempDir(prefix = 'overseer-cc-') {
  return fs.mkdtempSync(path.join(os.tmpdir(), prefix));
}

export function removeDir(dir) {
  fs.rmSync(dir, { recursive: true, force: true });
}

export function fixture(name) {
  return JSON.parse(fs.readFileSync(path.join(FIXTURES_DIR, name), 'utf8'));
}

export function fixtureText(name) {
  return fs.readFileSync(path.join(FIXTURES_DIR, name), 'utf8');
}

export function fixtureNames() {
  return fs.readdirSync(FIXTURES_DIR).filter((name) => name.endsWith('.json')).sort();
}

/**
 * Run a Node script with controlled stdin.
 *   input        string or Buffer written immediately
 *   endStdin     true (default) closes stdin after the write
 *   keepStdinOpen keeps the pipe open until the process ends (deadline test)
 */
export function runNode(scriptPath, args = [], options = {}) {
  const {
    env = {},
    input = '',
    endStdin = true,
    timeoutMs = 20000,
    cwd = INTEGRATION_ROOT,
  } = options;

  return new Promise((resolve) => {
    const started = Date.now();
    const child = spawn(process.execPath, [scriptPath, ...args], {
      cwd,
      env: { ...process.env, ...env },
      stdio: ['pipe', 'pipe', 'pipe'],
      windowsHide: true,
    });

    let stdout = '';
    let stderr = '';
    let settled = false;
    let timedOut = false;

    child.stdout.setEncoding('utf8');
    child.stdout.on('data', (chunk) => { stdout += chunk; });
    child.stderr.setEncoding('utf8');
    child.stderr.on('data', (chunk) => { stderr += chunk; });
    child.stdin.on('error', () => { /* EPIPE when the child stops reading */ });

    const timer = setTimeout(() => {
      timedOut = true;
      try { child.kill(); } catch { /* ignore */ }
    }, timeoutMs);

    child.on('close', (status, signal) => {
      if (settled) return;
      settled = true;
      clearTimeout(timer);
      resolve({ status, signal, stdout, stderr, durationMs: Date.now() - started, timedOut });
    });

    if (input && input.length > 0) {
      try { child.stdin.write(input); } catch { /* ignore */ }
    }
    if (endStdin) {
      try { child.stdin.end(); } catch { /* ignore */ }
    }
  });
}

export function runBridge(payload, options = {}) {
  const input = typeof payload === 'string' ? payload : JSON.stringify(payload);
  return runNode(BRIDGE_PATH, [], { ...options, input });
}

export function instanceDirectory(spoolRoot, env = {}) {
  const override = env.OVERSEER_CLAUDE_SOURCE_INSTANCE_ID;
  if (override) return path.join(spoolRoot, 'claude', override);
  const file = path.join(spoolRoot, 'claude', 'instance.json');
  const parsed = JSON.parse(fs.readFileSync(file, 'utf8'));
  return path.join(spoolRoot, 'claude', parsed.sourceInstanceId);
}

export function readLines(dir) {
  let raw;
  try {
    raw = fs.readFileSync(path.join(dir, 'events.jsonl'), 'utf8');
  } catch {
    return [];
  }
  return raw.split('\n').filter((line) => line.length > 0);
}

export function readRecords(dir) {
  return readLines(dir).map((line) => JSON.parse(line));
}

export function readHealthJson(dir) {
  try {
    return JSON.parse(fs.readFileSync(path.join(dir, 'health.json'), 'utf8'));
  } catch {
    return null;
  }
}

export function readSourceEpoch(dir) {
  return JSON.parse(fs.readFileSync(path.join(dir, 'source-epoch.json'), 'utf8'));
}

export function listBackups(settingsPath) {
  const dir = path.dirname(settingsPath);
  const base = path.basename(settingsPath) + '.overseer-backup-';
  return fs.readdirSync(dir).filter((name) => name.startsWith(base)).sort();
}

/** Every string value in a JSON tree, used for privacy assertions. */
export function collectStrings(value, out = []) {
  if (typeof value === 'string') out.push(value);
  else if (Array.isArray(value)) for (const item of value) collectStrings(item, out);
  else if (value && typeof value === 'object') for (const item of Object.values(value)) collectStrings(item, out);
  return out;
}
