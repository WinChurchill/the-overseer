import fs from 'node:fs';
import path from 'node:path';
import { randomBytes, randomUUID } from 'node:crypto';
import {
  Deadline, ensureRegularDirectory, openRegularFile, processIsAlive, readJson, readSpoolFile,
  regularFile, withLock, writeJsonAtomic,
} from './spool-io.mjs';

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
  // A genuine append/lock failure is a distinct outcome from a lock timeout; it
  // must be counted, or a lost record would be invisible in health.json.
  'droppedWriteFailed',
  'droppedCorruptSourceEpoch',
  'droppedUnsupportedPayload',
  'droppedInternalError',
  'recoveredCorruptFile',
  'unknownEvents',
]);

const HEALTH_MAX_MS = 60;
const HEALTH_MARKER_RESERVE_MS = 10;
const ABANDONED_INTENT_MS = 5000;
export const HEALTH_RECEIPT_SLOTS = 64;
export const MAX_HEALTH_RECEIPT_BYTES = 512;

function receiptPath(dir, slot) {
  return path.join(dir, 'health-pending', String(slot).padStart(2, '0') + '.json');
}

/** Cold directory acquisition belongs to the caller's parent write budget. */
export function prepareHealthDirectory(dir, deadline) {
  if (!dir || deadline.expired()) return false;
  try {
    ensureRegularDirectory(dir);
    ensureRegularDirectory(path.join(dir, 'health-pending'));
    return true;
  } catch { return false; /* fail open */ }
}

function readReceipt(file) {
  let fd;
  try {
    fd = openRegularFile(file, 'r');
    const buffer = Buffer.alloc(MAX_HEALTH_RECEIPT_BYTES + 1);
    const length = fs.readSync(fd, buffer, 0, buffer.length, 0);
    if (length > MAX_HEALTH_RECEIPT_BYTES) return { invalid: true };
    const value = JSON.parse(buffer.subarray(0, length).toString('utf8'));
    if (!value || !/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(value.id) || !Array.isArray(value.counters) ||
        value.counters.length > HEALTH_COUNTERS.length ||
        value.counters.some(name => !HEALTH_COUNTERS.includes(name))) return { invalid: true };
    return { value };
  } catch (error) {
    return error?.code === 'ENOENT' ? {} : { invalid: true };
  } finally {
    if (fd !== undefined) try { fs.closeSync(fd); } catch { /* fail open */ }
  }
}

function markAccountingIncomplete(dir, deadline) {
  if (deadline?.expired()) return false;
  try {
    fs.closeSync(fs.openSync(path.join(dir, 'health-incomplete'), 'wx'));
    return true;
  } catch (error) {
    return error?.code === 'EEXIST';
  }
}

function isAbandonedIntent(file) {
  const info = regularFile(file, true);
  return info && Date.now() - info.mtimeMs > ABANDONED_INTENT_MS &&
    !processIsAlive(Number(readSpoolFile(file, 32)));
}

function reclaimIntent(dir, file, deadline) {
  if (!isAbandonedIntent(file)) return false;
  // Serialize reclamation so two publishers cannot delete a freshly reused slot.
  const result = withLock(path.join(dir, 'health.lock'), deadline, () => {
    if (!regularFile(file, true)) return true;
    if (!isAbandonedIntent(file) || !markAccountingIncomplete(dir, deadline)) return false;
    fs.unlinkSync(file);
    return true;
  }, false);
  return result.acquired && result.value;
}

/**
 * Record the hook before waiting on the writer lock. An exhausted deadline or
 * abrupt exit then leaves bounded, visible evidence of incomplete accounting.
 */
export function beginHealthAccounting(dir, deadline) {
  if (!prepareHealthDirectory(dir, deadline)) return null;
  const budget = deadline.child(HEALTH_MARKER_RESERVE_MS);
  const start = randomBytes(1)[0] % HEALTH_RECEIPT_SLOTS;
  for (let attempt = 0; attempt < HEALTH_RECEIPT_SLOTS && !budget.expired(); attempt++) {
    const file = receiptPath(dir, (start + attempt) % HEALTH_RECEIPT_SLOTS) + '.intent';
    try {
      if (fs.existsSync(file) && !reclaimIntent(dir, file, budget)) continue;
      fs.writeFileSync(file, String(process.pid), { flag: 'wx' });
      return file;
    } catch (error) {
      if (error?.code !== 'EEXIST' && error?.code !== 'ENOENT') break;
    }
  }
  markAccountingIncomplete(dir, deadline.child(HEALTH_MARKER_RESERVE_MS));
  return null;
}

/** Publish a complete receipt without taking the shared health lock. Fixed slots bound disk use. */
function publishHealthReceipt(dir, counters, deadline) {
  if (!prepareHealthDirectory(dir, deadline)) return false;
  const text = JSON.stringify({ id: randomUUID(), counters });
  if (Buffer.byteLength(text, 'utf8') > MAX_HEALTH_RECEIPT_BYTES) {
    return false;
  }
  const start = randomBytes(1)[0] % HEALTH_RECEIPT_SLOTS;
  for (let attempt = 0; attempt < HEALTH_RECEIPT_SLOTS && !deadline.expired(); attempt++) {
    const slot = (start + attempt) % HEALTH_RECEIPT_SLOTS;
    const file = receiptPath(dir, slot);
    if (fs.existsSync(file)) continue;
    const staging = file + '.tmp';
    let fd;
    let owned = false;
    try {
      fd = fs.openSync(staging, 'wx');
      owned = true;
      fs.writeFileSync(fd, text, 'utf8');
      fs.closeSync(fd);
      fd = undefined;
      // Readers see a complete immutable document. A peer cannot replace it.
      fs.linkSync(staging, file);
      return true;
    } catch (error) {
      if (error?.code !== 'EEXIST') break;
    } finally {
      if (fd !== undefined) try { fs.closeSync(fd); } catch { /* fail open */ }
      if (owned) try { fs.unlinkSync(staging); } catch { /* bounded orphan slot */ }
    }
  }
  return false;
}

function addCounters(target, names) {
  for (const name of names) {
    if (!HEALTH_COUNTERS.includes(name)) continue;
    target[name] = Math.min((target[name] ?? 0) + 1, Number.MAX_SAFE_INTEGER);
  }
}

function foldHealthReceipts(dir, deadline, wait) {
  const lockPath = path.join(dir, 'health.lock');
  try {
    if (!prepareHealthDirectory(dir, deadline)) return;
    withLock(lockPath, deadline, () => {
      const file = path.join(dir, 'health.json');
      const existing = readJson(file);
      if (!existing && fs.existsSync(file)) markAccountingIncomplete(dir, deadline);
      const previous = existing && typeof existing.counters === 'object' && existing.counters !== null ? existing.counters : {};
      const next = {};
      for (const name of HEALTH_COUNTERS) {
        const value = Number.isFinite(previous[name]) && previous[name] > 0 ? Math.floor(previous[name]) : 0;
        if (value > 0) next[name] = Math.min(value, Number.MAX_SAFE_INTEGER);
      }
      const accountedReceipts = {};
      const consumed = [];
      for (let slot = 0; slot < HEALTH_RECEIPT_SLOTS; slot++) {
        const prior = existing?.accountedReceipts?.[slot];
        if (typeof prior === 'string') accountedReceipts[slot] = prior;
        if (deadline.expired()) continue;
        const file = receiptPath(dir, slot);
        const receipt = readReceipt(file);
        if (receipt.invalid) { markAccountingIncomplete(dir, deadline); continue; }
        if (!receipt.value) continue;
        if (accountedReceipts[slot] !== receipt.value.id) addCounters(next, receipt.value.counters);
        accountedReceipts[slot] = receipt.value.id;
        consumed.push(file);
      }
      if (deadline.expired()) return;
      writeJsonAtomic(file, {
        schemaVersion: 1,
        harnessId: 'claude',
        sourceInstanceId: path.basename(dir),
        updatedAtUtc: new Date().toISOString(),
        healthRevision: randomUUID(),
        accountedReceipts,
        counters: next,
      });
      // Commit the totals and receipt IDs together before cleanup. A crash or an
      // unlink failure leaves a receipt that readers and the next fold deduplicate.
      for (const file of consumed) {
        if (deadline.expired()) break;
        try { fs.unlinkSync(file); } catch { /* the committed ID prevents replay */ }
      }
    }, wait);
  } catch {
    /* the already-published receipt remains visible; never fail the hook */
  }
}

/**
 * Publish accounting before attempting shared aggregation. Both operations share
 * the existing health slice of the caller's write budget; no extra wait is added.
 */
export function bumpHealth(dir, counters, deadline, intent) {
  if (!dir) return;
  const list = (Array.isArray(counters) ? counters : [counters]).filter(name => HEALTH_COUNTERS.includes(name));
  const budget = deadline instanceof Deadline
    ? deadline.child(HEALTH_MAX_MS)
    : new Deadline(HEALTH_MAX_MS);
  if (budget.remaining() <= 0) return;
  let covered = false;
  try {
    let published = publishHealthReceipt(dir, list, budget.child(Math.max(0, budget.remaining() - HEALTH_MARKER_RESERVE_MS)));
    if (!published) {
      // A full buffer must not prevent future compaction. Reclaim space once,
      // keeping time in the same slice for a retry or the incomplete marker.
      foldHealthReceipts(dir, budget.child(Math.max(0, budget.remaining() - 2 * HEALTH_MARKER_RESERVE_MS)), true);
      if (!budget.expired()) published = publishHealthReceipt(dir, list,
        budget.child(Math.max(0, budget.remaining() - HEALTH_MARKER_RESERVE_MS)));
    }
    covered = published || markAccountingIncomplete(dir, budget);
    if (published && intent && !budget.expired()) {
      // Retire the intent before optional compaction uses the remaining time.
      fs.unlinkSync(intent);
      intent = null;
    }
    if (published) foldHealthReceipts(dir, budget, false);
  } catch {
    covered ||= markAccountingIncomplete(dir, budget);
  } finally {
    if (covered && intent && !budget.expired()) {
      try { fs.unlinkSync(intent); } catch { /* conservatively leave evidence */ }
    }
  }
}

export function readHealth(dir) {
  // A fold can delete or reuse a slot while a reader is inspecting it. Retry a
  // changed summary instead of combining receipts with a different checkpoint.
  for (let attempt = 0; attempt < 3; attempt++) {
    const summary = readJson(path.join(dir, 'health.json'));
    const counters = { ...summary?.counters };
    let incomplete = fs.existsSync(path.join(dir, 'health-incomplete')) ||
      (!summary && fs.existsSync(path.join(dir, 'health.json')));
    let found = false;
    for (let slot = 0; slot < HEALTH_RECEIPT_SLOTS; slot++) {
      const file = receiptPath(dir, slot);
      incomplete ||= fs.existsSync(file + '.intent');
      const receipt = readReceipt(file);
      incomplete ||= receipt.invalid === true;
      if (!receipt.value) continue;
      found = true;
      if (summary?.accountedReceipts?.[slot] !== receipt.value.id) addCounters(counters, receipt.value.counters);
    }
    const after = readJson(path.join(dir, 'health.json'));
    if (summary?.healthRevision !== after?.healthRevision) continue;
    if (!summary && !found && !incomplete) return null;
    return { ...summary, counters, accountingIncomplete: incomplete };
  }
  return { ...readJson(path.join(dir, 'health.json')), accountingIncomplete: true };
}
