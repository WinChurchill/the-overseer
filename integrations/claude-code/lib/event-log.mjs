import fs from 'node:fs';
import path from 'node:path';
import { createHash } from 'node:crypto';
import { MAX_RECORD_BYTES } from './mapping.mjs';
import { ensureRegularDirectory, openRegularFile, regularFile, withLock, writeAtomic } from './spool-io.mjs';

export const EVENT_FILE_BYTES = 4 * 1024 * 1024;
const RECOVERY_TAIL_BYTES = 256 * 1024;
const PREVIOUS_EVENTS = 'events-previous.jsonl';

function sessionCounterFile(dir, sessionId) {
  const digest = createHash('sha256').update(sessionId, 'utf8').digest('hex').slice(0, 16);
  const safe = sessionId.replace(/[^A-Za-z0-9._-]/g, '_').slice(0, 48) || 'session';
  return path.join(dir, 'sessions', `${safe}-${digest}.seq`);
}

/**
 * Per-session ingestion order. Read and persisted inside the writer lock, so two
 * concurrent hook invocations never receive the same number. Ingestion order is
 * the order the spool received events; it is not proof of causal hook order
 * upstream.
 *
 * The independent checkpoint is reserved before append and the primary commits
 * afterward. Losing either file, or failing the final primary write, cannot reuse
 * a number. An interrupted write can leave a gap, which the consumer recognizes.
 */
function readSequence(file) {
  let fd;
  try {
    fd = openRegularFile(file, 'r', { singleLink: true });
    const buffer = Buffer.alloc(32);
    const count = fs.readSync(fd, buffer, 0, buffer.length, 0);
    const text = buffer.toString('utf8', 0, count).trim();
    const number = Number(text);
    return count < buffer.length && /^[1-9][0-9]*$/.test(text) && Number.isSafeInteger(number) ? number : 0;
  } catch (error) {
    return error?.code === 'ENOENT' ? null : 0;
  } finally {
    if (fd !== undefined) fs.closeSync(fd);
  }
}

/** Read complete lines from a bounded suffix. Never scan an old spool from byte zero. */
function readEventTail(file) {
  let fd;
  try {
    fd = openRegularFile(file, 'r', { singleLink: true });
    const size = fs.fstatSync(fd).size;
    const start = Math.max(0, size - RECOVERY_TAIL_BYTES);
    const buffer = Buffer.alloc(size - start);
    const count = fs.readSync(fd, buffer, 0, buffer.length, start);
    const data = buffer.subarray(0, count);
    const first = start === 0 ? 0 : data.indexOf(10) + 1;
    const last = data.lastIndexOf(10) + 1;
    return (start > 0 && first === 0) || last < first ? Buffer.alloc(0) : data.subarray(first, last);
  } catch (error) {
    if (error?.code !== 'ENOENT') throw error;
    return Buffer.alloc(0);
  } finally {
    if (fd !== undefined) fs.closeSync(fd);
  }
}

function findLegacySequence(files, record, deadline) {
  let sequence = 0;
  for (const file of files) {
    if (deadline.expired()) break;
    for (const line of readEventTail(file).toString('utf8').split('\n')) {
      if (deadline.expired()) break;
      if (!line) continue;
      try {
        const event = JSON.parse(line);
        if (event?.sessionId === record.sessionId && event.sourceEpoch === record.sourceEpoch &&
            Number.isSafeInteger(event.sequence)) sequence = Math.max(sequence, event.sequence);
      } catch { /* a malformed legacy record is not ordering evidence */ }
    }
  }
  return sequence;
}

function rotateEventLog(file, previous, length) {
  if (length <= EVENT_FILE_BYTES) {
    fs.renameSync(file, previous);
    return;
  }
  // Upgrade from the old unbounded log. Retain recent complete records without
  // copying the entire file or leaving an unbounded archive behind.
  writeAtomic(previous, readEventTail(file));
  fs.unlinkSync(file);
}

/**
 * Atomically append one bounded JSON line. The write happens under the spool
 * lock with a single buffered write, so concurrent hook invocations can never
 * interleave a line.
 */
export function publishRecord({ dir, record, deadline }) {
  const lockPath = path.join(dir, 'events.lock');
  const eventsPath = path.join(dir, 'events.jsonl');
  const previous = path.join(dir, PREVIOUS_EVENTS);
  let outcome;
  try {
    ensureRegularDirectory(dir);
    ensureRegularDirectory(path.join(dir, 'sessions'));
    outcome = withLock(lockPath, deadline, () => {
      const info = regularFile(eventsPath, true);
      const counter = sessionCounterFile(dir, record.sessionId);
      const checkpoint = counter + '.recovery';
      const primary = readSequence(counter);
      const reserved = readSequence(checkpoint);
      const saved = Math.max(primary ?? 0, reserved ?? 0);
      const last = saved || findLegacySequence([eventsPath, previous], record, deadline);
      if (last === 0 && (primary === 0 || reserved === 0)) {
        return { ok: false, reason: 'write-failed', detail: 'sequence-unrecoverable' };
      }
      const sequence = last + 1;
      if (!Number.isSafeInteger(sequence)) return { ok: false, reason: 'write-failed', detail: 'sequence-exhausted' };
      const line = JSON.stringify({ ...record, sequence }) + '\n';
      const bytes = Buffer.byteLength(line, 'utf8');
      if (bytes > MAX_RECORD_BYTES) return { ok: false, reason: 'record-too-large' };
      if (deadline.expired()) return { ok: false, reason: 'lock-timeout' };
      let length = info?.size ?? 0;
      if (length > 0 && length + bytes + 1 > EVENT_FILE_BYTES) {
        rotateEventLog(eventsPath, previous, length);
        length = 0;
      }
      if (deadline.expired()) return { ok: false, reason: 'lock-timeout' };
      const fd = openRegularFile(eventsPath, fs.constants.O_RDWR | fs.constants.O_APPEND, { singleLink: true, create: 'ax+' });
      try {
        const end = Buffer.alloc(1);
        if (length > 0 && fs.readSync(fd, end, 0, 1, length - 1) !== 1)
          return { ok: false, reason: 'write-failed', detail: 'unreadable-event-tail' };
        // An interrupted process can leave an unterminated record. Delimit
        // that fragment so it cannot swallow the next complete event.
        const buffer = Buffer.from(length > 0 && end[0] !== 10 ? '\n' + line : line, 'utf8');
        writeAtomic(checkpoint, String(sequence));
        if (deadline.expired()) return { ok: false, reason: 'lock-timeout' };
        let offset = 0;
        try {
          while (offset < buffer.length) {
            const written = fs.writeSync(fd, buffer, offset, buffer.length - offset, null);
            if (!Number.isFinite(written) || written <= 0) throw new Error('short append');
            offset += written;
          }
        } catch (error) {
          // Roll back a known failed append. If rollback fails, retain the
          // reservation so a possibly visible record cannot reuse its number.
          try {
            fs.ftruncateSync(fd, length);
            if (last > 0) writeAtomic(checkpoint, String(last));
            else fs.unlinkSync(checkpoint);
          } catch { /* the checkpoint still protects the number */ }
          throw error;
        }
        try { fs.fsyncSync(fd); } catch { /* durability is best effort */ }
      } finally {
        try { fs.closeSync(fd); } catch { /* ignore */ }
      }

      let recovered = last > (primary ?? 0) || reserved === 0;
      try { writeAtomic(counter, String(sequence)); } catch { recovered = true; }
      return { ok: true, sequence, bytes, recovered };
    });
  } catch (err) {
    return { ok: false, reason: 'write-failed', detail: err && err.code ? err.code : 'unknown' };
  }
  if (!outcome.acquired) return { ok: false, reason: 'lock-timeout' };
  return outcome.value;
}
