/**
 * OVERSEER Claude Code hook bridge - bounded local spool writer.
 *
 * Layout (spoolRoot defaults to %LOCALAPPDATA%\\OVERSEER\\spool on Windows and
 * $XDG_STATE_HOME/OVERSEER/spool elsewhere; override with OVERSEER_SPOOL_ROOT):
 *
 *   <spoolRoot>/claude/instance.json                     install identity
 *   <spoolRoot>/claude/<sourceInstanceId>/source-epoch.json   install epoch
 *   <spoolRoot>/claude/<sourceInstanceId>/events.jsonl        append-only records
 *   <spoolRoot>/claude/<sourceInstanceId>/events.lock         writer lock
 *   <spoolRoot>/claude/<sourceInstanceId>/health.json         bounded counters
 *   <spoolRoot>/claude/<sourceInstanceId>/sessions/<key>.seq  per-session order
 *
 * Every operation is best-effort and bounded by a Deadline. Nothing here ever
 * throws towards the hook caller: a failure is reported as a reason code.
 */

import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { createHash, randomBytes, randomUUID } from 'node:crypto';

import { MAX_RECORD_BYTES } from './mapping.mjs';

/** Fixed, bounded counter set. health.json never grows with event volume. */
export const HEALTH_COUNTERS = Object.freeze([
  'recordsWritten',
  'droppedOversizeInput',
  'droppedEmptyInput',
  'droppedMalformedInput',
  'droppedStdinDeadline',
  'droppedStdinError',
  'droppedLockTimeout',
  'droppedOversizeRecord',
  'droppedCorruptSourceEpoch',
  'droppedUnsupportedPayload',
  'droppedInternalError',
  'recoveredCorruptFile',
  'unknownEvents',
]);

const STALE_LOCK_MS = 5000;
const HEALTH_MAX_MS = 60;

export class Deadline {
  constructor(ms) {
    this.end = Date.now() + Math.max(0, ms);
  }
  remaining() {
    return this.end - Date.now();
  }
  expired() {
    return Date.now() >= this.end;
  }
  /** Sub-deadline clamped to what is left of this one. */
  child(ms) {
    return new Deadline(Math.max(0, Math.min(ms, this.remaining())));
  }
}

function sleepSync(ms) {
  if (!(ms > 0)) return;
  try {
    Atomics.wait(new Int32Array(new SharedArrayBuffer(4)), 0, 0, ms);
  } catch {
    /* ignore */
  }
}

export function utcStamp(date = new Date()) {
  return date.toISOString().replace(/[-:]/g, '').replace(/\.\d{3}Z$/, 'Z');
}

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
  if (trimmed === '.' || trimmed === '..') return null;
  return trimmed;
}

function sanitizeEpoch(value) {
  if (typeof value !== 'string') return null;
  const trimmed = value.trim();
  return /^epoch-[A-Za-z0-9-]{8,64}$/.test(trimmed) ? trimmed : null;
}

function readJson(file) {
  try {
    return JSON.parse(fs.readFileSync(file, 'utf8'));
  } catch {
    return null;
  }
}

function writeJsonAtomic(file, value) {
  const tmp = `${file}.tmp-${process.pid}-${randomBytes(4).toString('hex')}`;
  fs.writeFileSync(tmp, JSON.stringify(value, null, 2) + '\n', { encoding: 'utf8', flag: 'wx' });
  try {
    fs.renameSync(tmp, file);
  } catch (err) {
    try { fs.rmSync(tmp, { force: true }); } catch { /* ignore */ }
    throw err;
  }
}

/**
 * Read an existing identity file or create it exactly once. A file that exists
 * but cannot be parsed is moved aside (bounded recovery) rather than silently
 * regenerating the identity on every invocation.
 */
function ensureExclusiveJson(file, create, extract) {
  const recovered = { hit: false };
  for (let attempt = 0; attempt < 3; attempt++) {
    const existing = readJson(file);
    if (existing) {
      const value = extract(existing);
      if (value) return { value, created: false, recovered: recovered.hit };
      try {
        fs.renameSync(file, `${file}.corrupt-${utcStamp()}-${randomBytes(2).toString('hex')}`);
        recovered.hit = true;
      } catch { /* ignore */ }
      continue;
    }
    const value = create();
    try {
      fs.writeFileSync(file, JSON.stringify(value, null, 2) + '\n', { encoding: 'utf8', flag: 'wx' });
      return { value, created: true, recovered: recovered.hit };
    } catch (err) {
      if (err && err.code === 'EEXIST') { sleepSync(3); continue; }
      return { value: null, error: err && err.code ? err.code : 'write-failed', recovered: recovered.hit };
    }
  }
  return { value: null, error: 'identity-unstable', recovered: recovered.hit };
}

/**
 * Stable source instance id for this install. Created once under the spool and
 * reused by every later invocation; OVERSEER_CLAUDE_SOURCE_INSTANCE_ID
 * overrides it for tests and advanced setups.
 */
export function ensureSourceInstance(spoolRoot, env = process.env) {
  const override = sanitizeInstanceId(env.OVERSEER_CLAUDE_SOURCE_INSTANCE_ID);
  if (override) return { sourceInstanceId: override, fromEnv: true, created: false, recovered: false };
  const dir = path.join(spoolRoot, 'claude');
  fs.mkdirSync(dir, { recursive: true });
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
  );
  if (!result.value) return { sourceInstanceId: null, error: result.error };
  return { sourceInstanceId: result.value.sourceInstanceId, fromEnv: false, created: result.created, recovered: result.recovered };
}

/** Stable source epoch for this install, stored once per source instance. */
export function ensureSourceEpoch(dir, sourceInstanceId) {
  fs.mkdirSync(dir, { recursive: true });
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
  );
  if (!result.value) return { sourceEpoch: null, error: result.error };
  return { sourceEpoch: result.value.sourceEpoch, created: result.created, recovered: result.recovered };
}

function tryAcquireLock(lockPath) {
  try {
    const fd = fs.openSync(lockPath, 'wx');
    try {
      fs.writeSync(fd, `${process.pid} ${Date.now()}\n`);
    } finally {
      fs.closeSync(fd);
    }
    return true;
  } catch (err) {
    if (err && err.code === 'EEXIST') return false;
    throw err;
  }
}

/**
 * Exclusive-create lock with a stale-lock takeover and a hard deadline.
 * Returns { acquired, value } - never blocks past the deadline.
 */
export function withLock(lockPath, deadline, fn) {
  let acquired = false;
  while (!deadline.expired()) {
    if (tryAcquireLock(lockPath)) { acquired = true; break; }
    let stale = false;
    try {
      const stat = fs.statSync(lockPath);
      stale = Date.now() - stat.mtimeMs > STALE_LOCK_MS;
    } catch {
      continue; // lock disappeared between open and stat: retry at once
    }
    if (stale) {
      try { fs.rmSync(lockPath, { force: true }); } catch { /* ignore */ }
      continue;
    }
    sleepSync(Math.min(4, Math.max(1, deadline.remaining())));
  }
  if (!acquired) return { acquired: false, value: null };
  try {
    return { acquired: true, value: fn() };
  } finally {
    try { fs.rmSync(lockPath, { force: true }); } catch { /* ignore */ }
  }
}

function sessionCounterFile(dir, sessionId) {
  const digest = createHash('sha256').update(sessionId, 'utf8').digest('hex').slice(0, 16);
  const safe = sessionId.replace(/[^A-Za-z0-9._-]/g, '_').slice(0, 48) || 'session';
  return path.join(dir, 'sessions', `${safe}-${digest}.seq`);
}

/**
 * Per-session ingestion order. Allocated inside the writer lock, so two
 * concurrent hook invocations never receive the same number. Ingestion order
 * is the order the spool received events; it is not proof of causal hook order
 * upstream.
 */
function nextSequence(dir, sessionId) {
  const file = sessionCounterFile(dir, sessionId);
  fs.mkdirSync(path.dirname(file), { recursive: true });
  let current = 0;
  try {
    const parsed = Number.parseInt(fs.readFileSync(file, 'utf8').trim(), 10);
    if (Number.isSafeInteger(parsed) && parsed > 0) current = parsed;
  } catch { /* missing or unreadable counter starts at zero */ }
  const next = current + 1;
  const tmp = `${file}.tmp-${process.pid}-${randomBytes(4).toString('hex')}`;
  fs.writeFileSync(tmp, String(next), { encoding: 'utf8', flag: 'wx' });
  try {
    fs.renameSync(tmp, file);
  } catch (err) {
    try { fs.rmSync(tmp, { force: true }); } catch { /* ignore */ }
    throw err;
  }
  return next;
}

/**
 * Atomically append one bounded JSON line. The write happens under the spool
 * lock with a single buffered write, so concurrent hook invocations can never
 * interleave a line.
 */
export function publishRecord({ dir, record, deadline }) {
  fs.mkdirSync(path.join(dir, 'sessions'), { recursive: true });
  const lockPath = path.join(dir, 'events.lock');
  const eventsPath = path.join(dir, 'events.jsonl');
  let outcome;
  try {
    outcome = withLock(lockPath, deadline, () => {
      const sequence = nextSequence(dir, record.sessionId);
      const line = JSON.stringify({ ...record, sequence }) + '\n';
      const bytes = Buffer.byteLength(line, 'utf8');
      if (bytes > MAX_RECORD_BYTES) return { ok: false, reason: 'record-too-large' };
      const buffer = Buffer.from(line, 'utf8');
      const fd = fs.openSync(eventsPath, 'a');
      try {
        let offset = 0;
        while (offset < buffer.length) {
          const written = fs.writeSync(fd, buffer, offset, buffer.length - offset, null);
          if (!Number.isFinite(written) || written <= 0) {
            // A zero-progress write must never spin forever or look successful.
            throw new Error('short append');
          }

          offset += written;
        }
        try { fs.fsyncSync(fd); } catch { /* durability is best effort */ }
      } finally {
        try { fs.closeSync(fd); } catch { /* ignore */ }
      }
      return { ok: true, sequence, bytes };
    });
  } catch (err) {
    return { ok: false, reason: 'write-failed', detail: err && err.code ? err.code : 'unknown' };
  }
  if (!outcome.acquired) return { ok: false, reason: 'lock-timeout' };
  return outcome.value;
}

function bumpHealthCounters(dir, counters, deadline) {
  const lockPath = path.join(dir, 'health.lock');
  try {
    fs.mkdirSync(dir, { recursive: true });
    withLock(lockPath, deadline, () => {
      const file = path.join(dir, 'health.json');
      const existing = readJson(file);
      const previous = existing && typeof existing.counters === 'object' && existing.counters !== null ? existing.counters : {};
      const next = {};
      for (const name of HEALTH_COUNTERS) {
        const value = Number.isFinite(previous[name]) && previous[name] > 0 ? Math.floor(previous[name]) : 0;
        if (value > 0) next[name] = Math.min(value, Number.MAX_SAFE_INTEGER);
      }
      for (const name of counters) {
        if (!HEALTH_COUNTERS.includes(name)) continue;
        const value = (next[name] ?? 0) + 1;
        next[name] = Math.min(value, Number.MAX_SAFE_INTEGER);
      }
      writeJsonAtomic(file, {
        schemaVersion: 1,
        harnessId: 'claude',
        sourceInstanceId: path.basename(dir),
        updatedAtUtc: new Date().toISOString(),
        counters: next,
      });
    });
  } catch {
    /* health is best effort and never blocks or fails the hook */
  }
}

/**
 * Bounded failure/health accounting. Ignored when no budget is left: the hook
 * must never trade Claude Code latency for diagnostics.
 */
export function bumpHealth(dir, counters, deadline) {
  if (!dir) return;
  const list = Array.isArray(counters) ? counters : [counters];
  const budget = deadline instanceof Deadline
    ? deadline.child(HEALTH_MAX_MS)
    : new Deadline(HEALTH_MAX_MS);
  if (budget.remaining() <= 0) return;
  bumpHealthCounters(dir, list, budget);
}

export function readHealth(dir) {
  return readJson(path.join(dir, 'health.json'));
}

export function readEvents(dir) {
  let raw;
  try {
    raw = fs.readFileSync(path.join(dir, 'events.jsonl'), 'utf8');
  } catch {
    return [];
  }
  const records = [];
  for (const line of raw.split('\n')) {
    if (!line) continue;
    records.push(JSON.parse(line));
  }
  return records;
}
