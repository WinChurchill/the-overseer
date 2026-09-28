/**
 * OVERSEER Claude Code hook bridge - pre-write verification.
 *
 * The installer records an absolute path to `bridge.mjs` in the user's settings
 * file. A recorded path is only useful if the command it describes can actually
 * run: a settings entry pointing at a deleted extraction makes Claude Code report
 * a hook error on every event, and the user sees a broken integration rather than
 * a refused install.
 *
 * So `install.mjs --apply` refuses to write anything until the bridge it is about
 * to record has been *executed* once with a synthetic payload and has produced a
 * well-formed record. Nothing about the user's real spool is touched: the probe
 * runs against a temporary spool root that is deleted afterwards.
 *
 * This module is imported by install.mjs only. bridge.mjs never imports it.
 */

import { spawnSync } from 'node:child_process';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';

/** Bound on the verification probe. The bridge itself gives up after 2.25 s. */
export const PROBE_TIMEOUT_MS = 15000;

/** Fields the probe payload is allowed to carry: documented identity only. */
const PROBE_PAYLOAD = {
  session_id: 'overseer-install-verify',
  cwd: 'C:/overseer-install-verify',
  hook_event_name: 'SessionStart',
};

/**
 * The exact words placed in `handler.command`. Kept here so the installer, the
 * documentation and the tests all name the same executable.
 */
export const HOOK_COMMAND = 'node';

function probeFailure(code, message, extra = {}) {
  return {
    ok: false,
    code,
    message,
    ranAt: null,
    recordKind: null,
    producerVersion: null,
    exitCode: null,
    stderr: '',
    ...extra,
  };
}

/**
 * The newest record anywhere under the probe spool. The spool nests records one
 * directory deeper than the source instance (`claude/<instanceId>/events.jsonl`),
 * so this walks the tree instead of assuming a flat layout, and picks the record
 * with the highest per-session sequence so the result does not depend on
 * directory-enumeration order.
 */
function newestRecord(directory, depth = 0) {
  let entries;
  try {
    entries = fs.readdirSync(directory, { withFileTypes: true });
  } catch {
    return null;
  }

  let best = null;
  const consider = (record) => {
    if (!record || typeof record !== 'object') return;
    if (best === null || (record.sequence ?? 0) >= (best.sequence ?? 0)) best = record;
  };

  for (const entry of entries) {
    const full = path.join(directory, entry.name);
    if (entry.isDirectory()) {
      consider(newestRecord(full, depth + 1));
      continue;
    }
    // Records live below the harness directory, not beside instance.json.
    if (depth < 2 || !entry.name.endsWith('.jsonl')) continue;
    let text;
    try {
      text = fs.readFileSync(full, 'utf8');
    } catch {
      continue;
    }
    for (const line of text.split('\n')) {
      if (line.length === 0) continue;
      try {
        consider(JSON.parse(line));
      } catch {
        // A truncated tail line is not evidence of a working bridge.
      }
    }
  }
  return best;
}

/**
 * Execute a recorded hook command once against an isolated spool and report
 * whether it produced a record.
 *
 * @param {object} options
 * @param {string} options.bridgePath absolute path to bridge.mjs
 * @param {string} options.command    executable name recorded in the handler
 * @param {string} [options.spoolRoot] temporary spool root (defaults to a new temp dir)
 * @param {boolean} [options.keepSpool] keep the temporary spool (used by tests)
 */
export function verifyBridgeCommand({ bridgePath, command = HOOK_COMMAND, spoolRoot, keepSpool = false, execute = true }) {
  if (typeof bridgePath !== 'string' || bridgePath.length === 0) {
    return probeFailure('bridge-path-empty', 'no bridge path was given');
  }

  let stat = null;
  try {
    stat = fs.statSync(bridgePath);
  } catch (err) {
    const missing = err && err.code === 'ENOENT';
    return probeFailure(
      missing ? 'bridge-missing' : 'bridge-stat-failed',
      missing
        ? `the hook script does not exist: ${bridgePath}`
        : `the hook script cannot be read (${err && err.code ? err.code : 'unknown error'}): ${bridgePath}`,
    );
  }

  if (!stat.isFile()) {
    return probeFailure('bridge-not-a-file', `the hook path is not a regular file: ${bridgePath}`);
  }

  if (!execute) {
    return { ok: true, code: 'file-only', message: 'regular file exists; execution was explicitly skipped' };
  }

  const ownedTemp = spoolRoot === undefined;
  const root = ownedTemp ? fs.mkdtempSync(path.join(os.tmpdir(), 'overseer-install-verify-')) : spoolRoot;

  let run;
  try {
    run = spawnSync(command, [bridgePath, 'overseer-install-verify'], {
      input: JSON.stringify(PROBE_PAYLOAD),
      encoding: 'utf8',
      timeout: PROBE_TIMEOUT_MS,
      windowsHide: true,
      env: { ...process.env, OVERSEER_SPOOL_ROOT: root },
    });
  } catch (err) {
    if (ownedTemp && !keepSpool) fs.rmSync(root, { recursive: true, force: true });
    return probeFailure(
      'command-not-spawnable',
      `"${command}" could not be started: ${err && err.message ? err.message : String(err)}`,
    );
  }

  // The temporary spool is inspection-only; it is never left behind.
  const cleanup = () => {
    if (ownedTemp && !keepSpool) fs.rmSync(root, { recursive: true, force: true });
  };

  if (run.error) {
    const notFound = run.error.code === 'ENOENT';
    cleanup();
    return probeFailure(
      notFound ? 'command-not-found' : 'command-not-spawnable',
      notFound
        ? `"${command}" was not found on PATH; Claude Code runs the same command, so the hook could not start`
        : `"${command}" could not be started: ${run.error.message}`,
      { exitCode: run.status },
    );
  }

  if (run.signal) {
    cleanup();
    return probeFailure('command-timeout', `"${command}" did not finish within ${PROBE_TIMEOUT_MS} ms`, {
      exitCode: run.status,
      stderr: run.stderr ?? '',
    });
  }

  const record = newestRecord(path.join(root, 'claude'), 1);
  const stderr = run.stderr ?? '';

  if (run.status !== 0) {
    cleanup();
    return probeFailure('command-exit-nonzero', `the hook command exited ${run.status}`, {
      exitCode: run.status,
      stderr,
    });
  }

  if (!record) {
    cleanup();
    return probeFailure(
      'no-record-written',
      'the hook command ran but wrote no spool record; the integration would observe nothing',
      { exitCode: run.status, stderr },
    );
  }

  if (record.harnessId !== 'claude' || typeof record.kind !== 'string') {
    cleanup();
    return probeFailure('record-not-recognized', 'the hook command wrote a record this integration does not recognize', {
      exitCode: run.status,
      stderr,
    });
  }

  const result = {
    ok: true,
    code: 'verified',
    message: `the hook command ran and wrote one ${record.kind} record`,
    ranAt: record.observedAtUtc ?? null,
    recordKind: record.kind,
    producerVersion: record.producerVersion ?? null,
    exitCode: run.status,
    stderr,
    spoolRoot: ownedTemp && !keepSpool ? null : root,
  };
  cleanup();
  return result;
}
