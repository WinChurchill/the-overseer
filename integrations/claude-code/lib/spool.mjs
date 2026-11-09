/**
 * OVERSEER Claude Code hook bridge - bounded local spool writer.
 *
 * Layout (spoolRoot defaults to %LOCALAPPDATA%\\OVERSEER\\spool on Windows and
 * $XDG_STATE_HOME/OVERSEER/spool elsewhere; override with OVERSEER_SPOOL_ROOT):
 *
 *   <spoolRoot>/claude/instance.json                     install identity
 *   <spoolRoot>/claude/<sourceInstanceId>/source-epoch.json   install epoch
 *   <spoolRoot>/claude/<sourceInstanceId>/events.jsonl        append-only records
 *   <spoolRoot>/claude/<sourceInstanceId>/events-previous.jsonl   previous segment
 *   <spoolRoot>/claude/<sourceInstanceId>/events.lock         writer lock
 *   <spoolRoot>/claude/<sourceInstanceId>/health.json         bounded counters
 *   <spoolRoot>/claude/<sourceInstanceId>/health-pending/     bounded accounting receipts
 *   <spoolRoot>/claude/<sourceInstanceId>/sessions/<key>.seq  per-session order
 *   <spoolRoot>/claude/<sourceInstanceId>/sessions/<key>.seq.recovery  recovery checkpoint
 *
 * Every operation is best-effort and bounded by a Deadline. Nothing here ever
 * throws towards the hook caller: a failure is reported as a reason code.
 */

import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { randomBytes, randomUUID } from 'node:crypto';
import { Deadline, ensureRegularDirectory, readJson, regularFile, sleepSync, utcStamp, withLock } from './spool-io.mjs';

export { Deadline, utcStamp, withLock } from './spool-io.mjs';
export { EVENT_FILE_BYTES, publishRecord } from './event-log.mjs';
export {
  HEALTH_COUNTERS, HEALTH_RECEIPT_SLOTS, MAX_HEALTH_RECEIPT_BYTES,
  beginHealthAccounting, bumpHealth, prepareHealthDirectory, readHealth,
} from './health-accounting.mjs';

export function resolveSpoolRoot(env = process.env) {
  const explicit = typeof env.OVERSEER_SPOOL_ROOT === 'string' ? env.OVERSEER_SPOOL_ROOT.trim() : '';
  if (explicit) return path.resolve(explicit);
  if (process.platform === 'win32') {
    const base = typeof env.LOCALAPPDATA === 'string' && env.LOCALAPPDATA.trim()
      ? env.LOCALAPPDATA.trim()
      : path.join(os.homedir(), 'AppData', 'Local');
    return path.join(base, 'OVERSEER', 'spool');
  }
  const xdg = typeof env.XDG_STATE_HOME === 'string' ? env.XDG_STATE_HOME.trim() : '';
  const base = xdg || path.join(os.homedir(), '.local', 'state');
  return path.join(base, 'OVERSEER', 'spool');
}

export function instanceDir(spoolRoot, sourceInstanceId) {
  return path.join(spoolRoot, 'claude', sourceInstanceId);
}

export function sanitizeInstanceId(value) {
  if (typeof value !== 'string') return null;
  const trimmed = value.trim();
  if (!/^[A-Za-z0-9._-]{1,64}$/.test(trimmed)) return null;
  if (trimmed.endsWith('.')) return null;
  return trimmed;
}

function sanitizeEpoch(value) {
  if (typeof value !== 'string') return null;
  const trimmed = value.trim();
  return /^epoch-[A-Za-z0-9-]{8,64}$/.test(trimmed) ? trimmed : null;
}

/**
 * Read an existing identity file or create it exactly once. A file that exists
 * but cannot be parsed is moved aside (bounded recovery) rather than silently
 * regenerating the identity on every invocation.
 */
function ensureExclusiveJson(file, create, extract, deadline) {
  const recovered = { hit: false };
  for (let attempt = 0; attempt < 3 && !deadline.expired(); attempt++) {
    const existing = readJson(file);
    const valid = existing && extract(existing);
    if (valid) return { value: valid, created: false, recovered: recovered.hit };
    if (regularFile(file)) {
      try {
        const repair = withLock(file + '.repair.lock', deadline.child(12), () => {
          const current = readJson(file);
          const fixed = current && extract(current);
          if (fixed) return fixed;
          if (regularFile(file)) {
            fs.renameSync(file, file + '.corrupt');
            recovered.hit = true;
          }
          return null;
        });
        if (repair.value) return { value: repair.value, created: false, recovered: recovered.hit };
      } catch { /* ignore */ }
      continue;
    }
    const value = create();
    const pending = `${file}.tmp-${process.pid}-${randomBytes(4).toString('hex')}`;
    try {
      // Exclusive-create writes expose the destination before its JSON is
      // complete. A concurrent first attach can exhaust retries against that
      // partial document and silently lose its event. Publish a complete inode
      // with an exclusive hard link instead: never replace a peer's identity.
      fs.writeFileSync(pending, JSON.stringify(value, null, 2) + '\n', { encoding: 'utf8', flag: 'wx' });
      fs.linkSync(pending, file);
      return { value, created: true, recovered: recovered.hit };
    } catch (err) {
      if (err && err.code === 'EEXIST') { sleepSync(3); continue; }
      return { value: null, error: err && err.code ? err.code : 'write-failed', recovered: recovered.hit };
    } finally {
      try { fs.rmSync(pending, { force: true }); } catch { /* cleanup cannot block a hook */ }
    }
  }
  return { value: null, error: 'identity-unstable', recovered: recovered.hit };
}

/**
 * Stable source instance id for this install. Created once under the spool and
 * reused by every later invocation; OVERSEER_CLAUDE_SOURCE_INSTANCE_ID
 * overrides it for tests and advanced setups.
 */
export function ensureSourceInstance(spoolRoot, env = process.env, deadline = new Deadline(250)) {
  ensureRegularDirectory(spoolRoot);
  const dir = path.join(spoolRoot, 'claude');
  ensureRegularDirectory(dir);
  const override = sanitizeInstanceId(env.OVERSEER_CLAUDE_SOURCE_INSTANCE_ID);
  if (override) return { sourceInstanceId: override, fromEnv: true, created: false, recovered: false };
  const result = ensureExclusiveJson(
    path.join(dir, 'instance.json'),
    () => ({
      schemaVersion: 1,
      harnessId: 'claude',
      sourceInstanceId: 'cc-' + randomUUID(),
      createdAtUtc: new Date().toISOString(),
    }),
    (parsed) => {
      const sourceInstanceId = sanitizeInstanceId(parsed.sourceInstanceId);
      return sourceInstanceId ? { sourceInstanceId } : null;
    },
    deadline,
  );
  if (!result.value) return { sourceInstanceId: null, error: result.error };
  return { sourceInstanceId: result.value.sourceInstanceId, fromEnv: false, created: result.created, recovered: result.recovered };
}

/** Stable source epoch for this install, stored once per source instance. */
export function ensureSourceEpoch(dir, sourceInstanceId, deadline = new Deadline(250)) {
  ensureRegularDirectory(dir);
  const result = ensureExclusiveJson(
    path.join(dir, 'source-epoch.json'),
    () => ({
      schemaVersion: 1,
      harnessId: 'claude',
      sourceInstanceId,
      sourceEpoch: `epoch-${utcStamp()}-${randomBytes(4).toString('hex')}`,
      createdAtUtc: new Date().toISOString(),
    }),
    (parsed) => {
      const sourceEpoch = sanitizeEpoch(parsed.sourceEpoch);
      return sourceEpoch ? { sourceEpoch } : null;
    },
    deadline,
  );
  if (!result.value) return { sourceEpoch: null, error: result.error };
  return { sourceEpoch: result.value.sourceEpoch, created: result.created, recovered: result.recovered };
}
