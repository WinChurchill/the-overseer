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

/**
 * Summary-only total of accounting episodes whose outcome was lost (an abandoned
 * intent, exhausted receipt capacity or a corrupt document). Never a receipt name.
 */
export const UNACCOUNTED_COUNTER = 'droppedUnaccounted';
const SUMMARY_COUNTERS = Object.freeze([...HEALTH_COUNTERS, UNACCOUNTED_COUNTER]);
const INCOMPLETE_MARKER = 'health-incomplete';
/** The marker while its episode is being committed to the summary. */
const FOLDING_MARKER = INCOMPLETE_MARKER + '.folding';
/** An unfinished scan limits coverage without proving any outcome was lost. */
const SCAN_MARKER = 'health-scan-incomplete';

const HEALTH_MAX_MS = 60;
const HEALTH_MARKER_RESERVE_MS = 10;
const ABANDONED_INTENT_MS = 5000;
const UUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
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
    // `corrupt` means the content itself was read and is wrong, not an I/O failure.
    if (length > MAX_HEALTH_RECEIPT_BYTES) return { invalid: true, corrupt: true };
    let value;
    try { value = JSON.parse(buffer.subarray(0, length).toString('utf8')); }
    catch { return { invalid: true, corrupt: true }; }
    if (!value || !/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(value.id) || !Array.isArray(value.counters) ||
        value.counters.length > HEALTH_COUNTERS.length ||
        value.counters.some(name => !HEALTH_COUNTERS.includes(name))) return { invalid: true, corrupt: true };
    return { value };
  } catch (error) {
    return error?.code === 'ENOENT' ? {} : { invalid: true };
  } finally {
    if (fd !== undefined) try { fs.closeSync(fd); } catch { /* fail open */ }
  }
}

function readSummary(file) {
  let text;
  try { text = readSpoolFile(file, 64 * 1024); }
  catch (error) { return error?.code === 'ENOENT' ? {} : { invalid: true, corrupt: error?.code === 'SPOOL_FILE_TOO_LARGE' }; }
  let value;
  try { value = JSON.parse(text); }
  catch { return { invalid: true, corrupt: true }; }
  return value && typeof value === 'object' && value.counters && typeof value.counters === 'object' && !Array.isArray(value.counters)
    ? { value } : { invalid: true, corrupt: true };
}

function markAccountingIncomplete(dir, deadline) {
  if (deadline?.expired()) return false;
  try {
    fs.closeSync(fs.openSync(path.join(dir, INCOMPLETE_MARKER), 'wx'));
    return true;
  } catch (error) {
    return error?.code === 'EEXIST';
  }
}

function markScanIncomplete(dir, deadline) {
  if (deadline.expired()) return false;
  try {
    fs.closeSync(fs.openSync(path.join(dir, SCAN_MARKER), 'wx'));
    return true;
  } catch (error) {
    return error?.code === 'EEXIST';
  }
}

function readIntent(file) {
  const parts = readSpoolFile(file, 64).trim().split(' ');
  const pid = Number(parts[0]);
  const validOwner = /^[0-9]+$/.test(parts[0]) && Number.isInteger(pid) && pid > 0 && pid <= 2147483647;
  return { pid, id: parts.length === 2 && validOwner && UUID.test(parts[1]) ? parts[1] : null };
}

function intentSlot(file) { return Number(path.basename(file).slice(0, 2)); }

function isAbandonedIntent(file) {
  const info = regularFile(file, true);
  return info && Date.now() - info.mtimeMs > ABANDONED_INTENT_MS &&
    !processIsAlive(readIntent(file).pid);
}

/** True/false only after bounded complete evidence; null preserves uncertainty. */
function intentOutcomeCovered(dir, slot, id, deadline) {
  const result = readSummary(path.join(dir, 'health.json'));
  if (result.invalid && !result.corrupt) return null;
  const summary = result.value;
  if (summary?.accountedIntents?.[slot] === id) return true;
  for (let index = 0; index < HEALTH_RECEIPT_SLOTS; index++) {
    if (deadline.expired()) return null;
    if (summary?.accountedReceipts?.[index] === id) return true;
    const receipt = readReceipt(receiptPath(dir, index));
    if (receipt.value?.id === id) return true;
    if (receipt.invalid && !receipt.corrupt) return null;
  }
  return false;
}

function reclaimIntent(dir, file, deadline) {
  if (!isAbandonedIntent(file)) return false;
  // Serialize reclamation so two publishers cannot delete a freshly reused slot.
  const result = withLock(path.join(dir, 'health.lock'), deadline, () => {
    if (!regularFile(file, true)) return true;
    const intent = readIntent(file);
    // A receipt may still be pending. Preserve it as proof, and only declare
    // loss after checking every fixed slot inside this same acquisition slice.
    if (intent.id) {
      const covered = intentOutcomeCovered(dir, intentSlot(file), intent.id, deadline);
      if (covered === null || !isAbandonedIntent(file)) return false;
      if (!covered && !markAccountingIncomplete(dir, deadline)) return false;
      fs.unlinkSync(file);
      return true;
    }
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
      fs.writeFileSync(file, process.pid + ' ' + randomUUID(), { flag: 'wx' });
      return file;
    } catch (error) {
      if (error?.code !== 'EEXIST' && error?.code !== 'ENOENT') break;
    }
  }
  markAccountingIncomplete(dir, deadline.child(HEALTH_MARKER_RESERVE_MS));
  return null;
}

/** Publish a complete receipt without taking the shared health lock. Fixed slots bound disk use. */
function publishHealthReceipt(dir, counters, deadline, intent = null) {
  if (!prepareHealthDirectory(dir, deadline)) return false;
  const text = JSON.stringify({ id: intent?.id ?? randomUUID(), counters });
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

function addCounters(target, names, allowed = HEALTH_COUNTERS) {
  for (const name of names) {
    if (!allowed.includes(name)) continue;
    target[name] = Math.min((target[name] ?? 0) + 1, Number.MAX_SAFE_INTEGER);
  }
}

/** Bound the listing to the receipt, intent and staging names the publisher can own. */
function listPending(dir, deadline) {
  const names = new Set();
  let directory;
  try {
    directory = fs.opendirSync(path.join(dir, 'health-pending'));
    for (let count = 0; count <= HEALTH_RECEIPT_SLOTS * 3; count++) {
      // Leave time in this same slice to make a partial listing durable.
      if (deadline.remaining() <= HEALTH_MARKER_RESERVE_MS) return { names, incomplete: true };
      const entry = directory.readSync();
      if (!entry) return { names, incomplete: false };
      if (count === HEALTH_RECEIPT_SLOTS * 3) return { names, incomplete: true };
      names.add(entry.name);
    }
  } catch {
    return { names: null, incomplete: true };
  } finally {
    if (directory) try { directory.closeSync(); } catch { /* fail open */ }
  }
}

/**
 * Fold receipts, and optionally this hook's own counters, into the summary.
 * Returns true only when a new summary that includes `direct` was committed.
 *
 * A lost outcome is one bounded `droppedUnaccounted` episode, not a permanent
 * state: once no other hook's intent is unresolved and nothing unreadable remains,
 * the incomplete marker is counted in the same atomic summary and then removed.
 */
function foldHealthReceipts(dir, deadline, wait, direct = [], ownIntent = null) {
  const lockPath = path.join(dir, 'health.lock');
  try {
    if (!prepareHealthDirectory(dir, deadline)) return false;
    const result = withLock(lockPath, deadline, () => {
      const file = path.join(dir, 'health.json');
      const summary = readSummary(file);
      if (summary.invalid && !summary.corrupt) return false; // Never overwrite a temporarily unreadable checkpoint.
      const existing = summary.value;
      let unreadable = false;
      if (summary.corrupt && !markAccountingIncomplete(dir, deadline)) return false;
      // Known-corrupt contents are replaced by this atomic commit. A file that
      // appeared after an absent read needs another pass, never an invented zero.
      if (!existing && !summary.corrupt && fs.existsSync(file)) return false;
      const previous = existing && typeof existing.counters === 'object' && existing.counters !== null ? existing.counters : {};
      const next = {};
      for (const name of SUMMARY_COUNTERS) {
        const value = Number.isFinite(previous[name]) && previous[name] > 0 ? Math.floor(previous[name]) : 0;
        if (value > 0) next[name] = Math.min(value, Number.MAX_SAFE_INTEGER);
      }
      const pending = listPending(dir, deadline);
      if (pending.incomplete && !markScanIncomplete(dir, deadline)) return false;
      const accountedReceipts = {};
      const accountedIntents = {};
      const coveredReceipts = new Set();
      const consumed = [];
      for (let slot = 0; slot < HEALTH_RECEIPT_SLOTS; slot++) {
        const prior = existing?.accountedReceipts?.[slot];
        if (typeof prior === 'string') { accountedReceipts[slot] = prior; coveredReceipts.add(prior); }
        const priorIntent = existing?.accountedIntents?.[slot];
        if (UUID.test(priorIntent ?? '')) accountedIntents[slot] = priorIntent;
        if (deadline.expired()) continue;
        const file = receiptPath(dir, slot);
        if (pending.names && !pending.names.has(path.basename(file))) continue;
        const receipt = readReceipt(file);
        if (receipt.invalid) {
          // An unreadable immutable receipt can still be recovered. Only content
          // corruption loses its outcome and belongs in the loss episode.
          const marked = receipt.corrupt ? markAccountingIncomplete(dir, deadline) : markScanIncomplete(dir, deadline);
          // A published receipt is immutable, so a corrupt one never becomes
          // valid. Remove it or it would hold the source incomplete forever.
          let removed = false;
          if (receipt.corrupt && marked) try { fs.unlinkSync(file); removed = true; } catch { /* retried by the next fold */ }
          unreadable ||= !removed;
          continue;
        }
        if (!receipt.value) continue;
        if (accountedReceipts[slot] !== receipt.value.id) addCounters(next, receipt.value.counters);
        accountedReceipts[slot] = receipt.value.id;
        coveredReceipts.add(receipt.value.id);
        consumed.push(file);
      }
      if (deadline.expired()) return false;

      // Resolve other hooks' intents. One that outlived its process is a lost
      // outcome: persist the marker first, then retire the intent under this lock.
      let unresolved = pending.incomplete;
      const own = ownIntent ? path.basename(ownIntent.file) : null;
      for (let slot = 0; slot < HEALTH_RECEIPT_SLOTS; slot++) {
        if (deadline.expired()) { unresolved = true; break; }
        const name = path.basename(receiptPath(dir, slot)) + '.intent';
        if (name === own) continue;
        const intent = path.join(dir, 'health-pending', name);
        try {
          // Directory iteration can miss racing entries even without hitting its
          // cap. Check every fixed owned name before retiring covering receipts.
          if (!fs.existsSync(intent)) continue;
          const identity = readIntent(intent);
          if (identity.id && (accountedIntents[slot] === identity.id || coveredReceipts.has(identity.id))) {
            accountedIntents[slot] = identity.id;
            // Never delete another live writer's path: its final cleanup could
            // otherwise unlink a new owner that reused this fixed slot.
            if (isAbandonedIntent(intent)) fs.unlinkSync(intent);
            continue;
          }
          if (isAbandonedIntent(intent) && markAccountingIncomplete(dir, deadline)) fs.unlinkSync(intent);
          else unresolved = true;
        } catch (error) { unresolved ||= error?.code !== 'ENOENT'; }
      }
      const marker = path.join(dir, INCOMPLETE_MARKER);
      const folding = path.join(dir, FOLDING_MARKER);
      if (!unresolved && !unreadable && fs.existsSync(marker)) fs.renameSync(marker, folding);
      const episode = fs.existsSync(folding);
      if (episode) addCounters(next, [UNACCOUNTED_COUNTER], SUMMARY_COUNTERS);
      addCounters(next, direct);
      if (direct.length > 0 && ownIntent?.id) accountedIntents[intentSlot(ownIntent.file)] = ownIntent.id;
      if (deadline.expired()) return false;
      writeJsonAtomic(file, {
        schemaVersion: 1,
        harnessId: 'claude',
        sourceInstanceId: path.basename(dir),
        updatedAtUtc: new Date().toISOString(),
        healthRevision: randomUUID(),
        accountedReceipts,
        accountedIntents,
        counters: next,
      });
      // Commit the totals and receipt IDs together before cleanup. A crash or an
      // unlink failure leaves a receipt that readers and the next fold deduplicate.
      // A surviving folding marker is counted again: over-reporting a gap is safe.
      if (episode) try { fs.unlinkSync(folding); } catch { /* counted again by the next fold */ }
      if (!unresolved && !unreadable && !deadline.expired()) {
        try { fs.unlinkSync(path.join(dir, SCAN_MARKER)); } catch { /* retain limited coverage until cleanup succeeds */ }
      }
      for (const file of consumed) {
        // A later complete fold must still see the proof if intent discovery or
        // reads were incomplete. Committed receipt IDs prevent duplicate counts.
        if (unresolved || unreadable) break;
        if (deadline.expired()) break;
        try { fs.unlinkSync(file); } catch { /* the committed ID prevents replay */ }
      }
      return true;
    }, wait);
    return result.acquired && result.value === true;
  } catch {
    /* the already-published receipt remains visible; never fail the hook */
    return false;
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
    const ownIntent = intent ? { file: intent, ...readIntent(intent) } : null;
    // Uncontended fast path: commit this hook's counters straight into the
    // summary. The intent stays until that atomic write succeeds, so a crash is
    // still visible. A busy lock falls back to a lock-free receipt.
    const direct = foldHealthReceipts(dir,
      budget.child(Math.max(0, budget.remaining() - 2 * HEALTH_MARKER_RESERVE_MS)), false, list, ownIntent);
    let published = direct ||
      publishHealthReceipt(dir, list, budget.child(Math.max(0, budget.remaining() - HEALTH_MARKER_RESERVE_MS)), ownIntent);
    if (!published) {
      // A full buffer must not prevent future compaction. Reclaim space once,
      // keeping time in the same slice for a retry or the incomplete marker.
      foldHealthReceipts(dir, budget.child(Math.max(0, budget.remaining() - 2 * HEALTH_MARKER_RESERVE_MS)), true, [], ownIntent);
      if (!budget.expired()) published = publishHealthReceipt(dir, list,
        budget.child(Math.max(0, budget.remaining() - HEALTH_MARKER_RESERVE_MS)), ownIntent);
    }
    covered = published || markAccountingIncomplete(dir, budget);
    if (published && intent && !budget.expired()) {
      // Retire the intent before optional compaction uses the remaining time.
      fs.unlinkSync(intent);
      intent = null;
    }
    if (published && !direct) foldHealthReceipts(dir, budget, false);
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
    let incomplete = fs.existsSync(path.join(dir, INCOMPLETE_MARKER)) ||
      fs.existsSync(path.join(dir, FOLDING_MARKER)) ||
      fs.existsSync(path.join(dir, SCAN_MARKER)) ||
      (!summary && fs.existsSync(path.join(dir, 'health.json')));
    let found = false;
    const unresolvedIntents = [];
    const coveredReceipts = new Set();
    for (let slot = 0; slot < HEALTH_RECEIPT_SLOTS; slot++) {
      const file = receiptPath(dir, slot);
      const prior = summary?.accountedReceipts?.[slot];
      if (typeof prior === 'string') coveredReceipts.add(prior);
      if (fs.existsSync(file + '.intent')) {
        try { unresolvedIntents.push({ slot, ...readIntent(file + '.intent') }); }
        catch { incomplete = true; }
      }
      const receipt = readReceipt(file);
      incomplete ||= receipt.invalid === true;
      if (!receipt.value) continue;
      coveredReceipts.add(receipt.value.id);
      found = true;
      if (summary?.accountedReceipts?.[slot] !== receipt.value.id) addCounters(counters, receipt.value.counters);
    }
    const after = readJson(path.join(dir, 'health.json'));
    if (summary?.healthRevision !== after?.healthRevision) continue;
    incomplete ||= unresolvedIntents.some(intent => !intent.id ||
      (summary?.accountedIntents?.[intent.slot] !== intent.id && !coveredReceipts.has(intent.id)));
    if (!summary && !found && !incomplete) return null;
    return { ...summary, counters, accountingIncomplete: incomplete };
  }
  return { ...readJson(path.join(dir, 'health.json')), accountingIncomplete: true };
}
