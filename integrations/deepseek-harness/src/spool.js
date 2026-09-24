/**
 * Bounded, atomic spool writer for the OVERSEER DeepSeek Harness observer.
 *
 * Layout: `<spoolRoot>/dsh/<sourceInstanceId>/` containing
 * - `events.jsonl` — append-only, one complete JSON line per write, each record
 *   written with a single `writeSync` on a file descriptor opened `'a'`
 *   (the `appendFileSync` contract without the per-record open/close);
 * - `current.json` — atomically replaced: written to a temp file in the SAME
 *   directory, then `renameSync`d over the target, so a concurrent reader only
 *   ever sees a complete JSON document;
 * - `health.json` — writer health (counters, lastWrite, queueDepth, dropped, version);
 * - `events-<utcstamp>.jsonl` — rotated event files owned by this adapter.
 *
 * Bounds: an in-process FIFO queue (default 512 records) drops the OLDEST record
 * and counts the drop; a disk failure degrades to counters instead of throwing
 * into the host or blocking a callback; 64 KiB per record; a 16 MiB soft cap, a
 * 4,096 file cap and a 1 hour TTL for THIS adapter's own rotated files. The
 * cleanup pass never leaves this adapter's own directory and never touches a
 * file this adapter did not write.
 *
 * Every public method is total: it records an error and returns rather than
 * throwing, because it is called from host callbacks where a synchronous throw
 * on `session/created` can roll back session publication
 * (packages/core/session/src/index.ts).
 *
 * @module overseer-dsh-observer/spool
 */

import * as defaultFs from 'node:fs'
import { join } from 'node:path'
import { HARNESS_ID, LIMITS, PRODUCER_VERSION } from './normalize.js'

/** Spool document version stamped into `health.json` and `current.json`. */
export const SPOOL_SCHEMA_VERSION = 1

/** Default bounded queue length. */
export const DEFAULT_QUEUE_LIMIT = 512
/** Hard clamp for the queue length. */
export const QUEUE_LIMIT_MIN = 16
/** Hard clamp for the queue length. */
export const QUEUE_LIMIT_MAX = 8192
/** Hard clamp for one serialized record. */
export const MAX_RECORD_BYTES = LIMITS.recordBytes
/** Rotate `events.jsonl` once it grows past this size. */
export const EVENT_FILE_BYTES = 4 * 1024 * 1024
/** Soft cap for the whole adapter directory. */
export const SPOOL_SOFT_CAP_BYTES = 16 * 1024 * 1024
/** Hard file-count cap for the adapter directory. */
export const SPOOL_MAX_FILES = 4096
/** TTL of an OVERSEER-owned rotated event file. */
export const ROTATED_TTL_MS = 60 * 60 * 1000
/** Default heartbeat period. */
export const DEFAULT_HEARTBEAT_MS = 30_000
/** Hard clamp for the heartbeat period. */
export const HEARTBEAT_MS_MIN = 5_000
/** Hard clamp for the heartbeat period. */
export const HEARTBEAT_MS_MAX = 300_000
/** How often the bounded cleanup pass may run. */
export const CLEANUP_INTERVAL_MS = 60_000
/** Bounded scheduled retries for a transient Windows rename failure. */
const CURRENT_RETRY_LIMIT = 8
/** Base backoff for the scheduled retry, in milliseconds. */
const CURRENT_RETRY_BASE_MS = 4
/** Cap on the scheduled retry backoff, in milliseconds. */
const CURRENT_RETRY_MAX_MS = 64

/** Minimum spacing between atomic `current.json` replacements. */
export const CURRENT_WRITE_INTERVAL_MS = 250
/** Maximum records written per drain tick, so one tick cannot monopolize the loop. */
export const MAX_DRAIN_PER_TICK = 128
/** Retry delay after the adapter directory cannot be created or opened. */
export const UNAVAILABLE_RETRY_MS = 1_000

const ROTATED_RE = /^events-\d{8}T\d{6}Z(-\d+)?\.jsonl$/

/**
 * Clamp a numeric knob, rejecting unusable values.
 * @param value - candidate value (number or numeric string).
 * @param min - inclusive lower clamp.
 * @param max - inclusive upper clamp.
 * @param fallback - value used when the candidate is not a finite number.
 * @returns the clamped integer.
 */
export function clampNumber(value, min, max, fallback) {
  const parsed = typeof value === 'number' ? value : Number.parseInt(String(value ?? ''), 10)
  if (!Number.isFinite(parsed)) return fallback
  return Math.min(max, Math.max(min, Math.trunc(parsed)))
}

/**
 * `YYYYMMDDTHHMMSSZ` stamp for rotated file names (no characters Windows rejects).
 * @param ms - epoch milliseconds.
 * @returns the UTC stamp.
 */
export function utcStamp(ms) {
  try {
    return `${new Date(ms).toISOString().slice(0, 19).replace(/[-:]/g, '')}Z`
  } catch {
    return '19700101T000000Z'
  }
}

/**
 * Create one spool writer.
 *
 * @param options - `{ spoolRoot, sourceInstanceId, queueLimit, maxRecordBytes, fs, now,
 *   currentMinIntervalMs, cleanupIntervalMs, maxDrainPerTick, unavailableRetryMs }`.
 *   The bound options `eventFileBytes`, `softCapBytes`, `maxFiles`, and
 *   `rotatedTtlMs` exist so a test can exercise the cleanup path cheaply; each
 *   is clamped to at most its documented cap and can only tighten it.
 * @returns the spool API, whose every method is total (never throws).
 */
export function createSpool(options = {}) {
  const fs = options.fs ?? defaultFs
  const now = typeof options.now === 'function' ? options.now : Date.now
  const spoolRoot = String(options.spoolRoot ?? '.')
  const sourceInstanceId = String(options.sourceInstanceId ?? 'dsh-default')
  const queueLimit = clampNumber(options.queueLimit, QUEUE_LIMIT_MIN, QUEUE_LIMIT_MAX, DEFAULT_QUEUE_LIMIT)
  const maxRecordBytes = clampNumber(options.maxRecordBytes, 256, MAX_RECORD_BYTES, MAX_RECORD_BYTES)
  const currentMinIntervalMs = clampNumber(options.currentMinIntervalMs, 0, 60_000, CURRENT_WRITE_INTERVAL_MS)
  const cleanupIntervalMs = clampNumber(options.cleanupIntervalMs, 0, 3_600_000, CLEANUP_INTERVAL_MS)
  const maxDrainPerTick = clampNumber(options.maxDrainPerTick, 1, 4096, MAX_DRAIN_PER_TICK)
  const unavailableRetryMs = clampNumber(options.unavailableRetryMs, 0, 60_000, UNAVAILABLE_RETRY_MS)
  const eventFileBytes = clampNumber(options.eventFileBytes, 1024, EVENT_FILE_BYTES, EVENT_FILE_BYTES)
  const softCapBytes = clampNumber(options.softCapBytes, 1024, SPOOL_SOFT_CAP_BYTES, SPOOL_SOFT_CAP_BYTES)
  const maxFiles = clampNumber(options.maxFiles, 1, SPOOL_MAX_FILES, SPOOL_MAX_FILES)
  const rotatedTtlMs = clampNumber(options.rotatedTtlMs, 1000, ROTATED_TTL_MS, ROTATED_TTL_MS)

  const directory = join(spoolRoot, HARNESS_ID, sourceInstanceId)
  const eventsPath = join(directory, 'events.jsonl')
  const currentPath = join(directory, 'current.json')
  const healthPath = join(directory, 'health.json')

  /** @type {string[]} */
  const queue = []
  const counters = {
    enqueued: 0,
    written: 0,
    bytesWritten: 0,
    dropped: 0,
    droppedOldest: 0,
    droppedOversize: 0,
    droppedError: 0,
    droppedClosed: 0,
    writeErrors: 0,
    unavailable: 0,
    rotations: 0,
    pruned: 0,
    expired: 0,
    currentWrites: 0,
    healthWrites: 0,
    currentRetries: 0,
    currentRetryExhausted: 0,
  }

  let sourceEpochValue = String(options.sourceEpoch ?? 'epoch-unknown')
  let fd = null
  let closed = false
  let drainScheduled = false
  let pendingTimer = null
  let currentDocument = null
  let currentRetries = 0
  let currentDirty = false
  let currentSerialized = null
  let lastCurrentWriteMs = 0
  let lastHealthWriteMs = 0
  let lastCleanupMs = -Infinity
  let lastWriteMs = null
  let lastError = null
  let unavailable = false
  let tmpCounter = 0
  const startedAtMs = now()

  function noteError(error) {
    lastError = {
      atUtc: new Date(now()).toISOString(),
      message: error instanceof Error ? error.message : String(error),
    }
  }

  function schedule() {
    if (closed || drainScheduled) return
    drainScheduled = true
    const run = () => {
      drainScheduled = false
      pendingTimer = null
      drain()
    }
    const timer = typeof setImmediate === 'function' ? setImmediate(run) : setTimeout(run, 0)
    timer?.unref?.()
    pendingTimer = timer
  }

  function scheduleAfter(ms) {
    if (closed || drainScheduled) return
    drainScheduled = true
    const timer = setTimeout(() => {
      drainScheduled = false
      pendingTimer = null
      drain()
    }, Math.max(1, ms))
    timer?.unref?.()
    pendingTimer = timer
  }

  /** Create the adapter directory; `current.json` and `health.json` need it too. */
  function ensureDirectory() {
    try {
      fs.mkdirSync(directory, { recursive: true })
      return true
    } catch (error) {
      counters.unavailable += 1
      unavailable = true
      noteError(error)
      return false
    }
  }

  /** Open the append descriptor, creating the directory first when needed. */
  function ensureTarget() {
    if (fd !== null) return true
    if (!ensureDirectory()) return false
    try {
      fd = fs.openSync(eventsPath, 'a')
      unavailable = false
      return true
    } catch (error) {
      counters.unavailable += 1
      unavailable = true
      noteError(error)
      return false
    }
  }

  function closeFd() {
    if (fd === null) return
    try {
      fs.closeSync(fd)
    } catch (error) {
      noteError(error)
    }
    fd = null
  }

  function rotateIfNeeded() {
    if (fd === null) return
    let size = 0
    try {
      size = fs.fstatSync(fd).size
    } catch (error) {
      noteError(error)
      return
    }
    if (size <= eventFileBytes) return
    closeFd()
    const stamp = utcStamp(now())
    let target = join(directory, `events-${stamp}.jsonl`)
    let suffix = 0
    while (fileExists(target)) {
      suffix += 1
      target = join(directory, `events-${stamp}-${suffix}.jsonl`)
    }
    try {
      fs.renameSync(eventsPath, target)
      counters.rotations += 1
    } catch (error) {
      noteError(error)
    }
  }

  function fileExists(path) {
    try {
      return fs.existsSync(path)
    } catch {
      // Existence check is advisory: a failed probe is treated as "no file".
      return false
    }
  }

  /**
   * Replace a file with a same-directory temp write plus rename.
   *
   * A single attempt on purpose: this runs on the host's writer path and must
   * never block it. Transient Windows rename contention (another process holding
   * the target open) is retried on a bounded schedule by the caller, which keeps
   * the document dirty until it is actually published.
   *
   * @param target - destination path.
   * @param text - complete document text.
   * @returns true when the replacement is published.
   */
  function writeAtomic(target, text) {
    tmpCounter += 1
    // Same directory as the target, so the rename cannot cross a volume.
    const tmp = `${target}.${process.pid}.${tmpCounter}.tmp`
    let lastError = null
    try {
      fs.writeFileSync(tmp, text, 'utf8')
      fs.renameSync(tmp, target)
      return true
    } catch (error) {
      lastError = error
    }

    noteError(lastError)
    try {
      fs.rmSync(tmp, { force: true })
    } catch {
      // Nothing further can be done about a temp file that cannot be removed.
    }
    return false
  }

  function writeCurrent(force) {
    if (!currentDirty || currentDocument === null || currentSerialized === null) return false
    if (!force && now() - lastCurrentWriteMs < currentMinIntervalMs) return false
    if (!ensureDirectory()) return false
    if (!writeAtomic(currentPath, currentSerialized)) {
      // Transient contention: stay dirty and retry on a bounded schedule. The
      // writer never busy-waits, so the host is never blocked by our retry.
      currentRetries += 1
      counters.currentRetries += 1
      if (currentRetries <= CURRENT_RETRY_LIMIT) {
        scheduleAfter(Math.min(CURRENT_RETRY_MAX_MS, CURRENT_RETRY_BASE_MS * currentRetries))
      } else {
        counters.currentRetryExhausted += 1
        currentRetries = 0
      }

      return false
    }

    currentRetries = 0
    counters.currentWrites += 1
    lastCurrentWriteMs = now()
    currentDirty = false
    return true
  }

  function healthDocument() {
    return {
      version: SPOOL_SCHEMA_VERSION,
      producerVersion: PRODUCER_VERSION,
      harnessId: HARNESS_ID,
      sourceInstanceId,
      sourceEpoch: sourceEpochValue,
      startedAtUtc: new Date(startedAtMs).toISOString(),
      lastWrite: lastWriteMs === null ? null : new Date(lastWriteMs).toISOString(),
      queueDepth: queue.length,
      dropped: counters.dropped,
      counters: { ...counters },
      lastError,
      unavailable,
      directory,
    }
  }

  function writeHealth(force) {
    if (!force && now() - lastHealthWriteMs < currentMinIntervalMs) return
    if (!ensureDirectory()) return
    // Counted before serialization so the document reports the write that carries it.
    counters.healthWrites += 1
    const ok = writeAtomic(healthPath, `${JSON.stringify(healthDocument())}\n`)
    if (ok) lastHealthWriteMs = now()
    else counters.healthWrites -= 1
  }

  function runCleanup() {
    if (now() - lastCleanupMs < cleanupIntervalMs) return
    lastCleanupMs = now()
    let names = []
    try {
      names = fs.readdirSync(directory)
    } catch {
      // A directory that cannot be listed has nothing this pass can bound.
      return
    }
    const rotated = []
    let files = 0
    let bytes = 0
    for (const name of names) {
      const full = join(directory, name)
      let stat
      try {
        stat = fs.statSync(full)
      } catch {
        continue
      }
      if (!stat.isFile()) continue
      files += 1
      bytes += stat.size
      if (ROTATED_RE.test(name)) {
        rotated.push({ name, full, size: stat.size, mtimeMs: stat.mtimeMs })
      } else if (name.endsWith('.tmp') && now() - stat.mtimeMs > rotatedTtlMs) {
        removeFile(full)
      }
    }
    rotated.sort((left, right) => left.mtimeMs - right.mtimeMs)
    for (const file of rotated) {
      const expired = now() - file.mtimeMs > rotatedTtlMs
      const overFiles = files > maxFiles
      const overBytes = bytes > softCapBytes
      if (!expired && !overFiles && !overBytes) continue
      if (removeFile(file.full)) {
        files -= 1
        bytes -= file.size
        if (expired) counters.expired += 1
      }
    }
  }

  function removeFile(path) {
    try {
      fs.rmSync(path, { force: true })
      counters.pruned += 1
      return true
    } catch (error) {
      noteError(error)
      return false
    }
  }

  function drain() {
    if (closed) return
    try {
      if (queue.length > 0 && ensureTarget()) {
        let written = 0
        while (queue.length > 0 && written < maxDrainPerTick) {
          const line = queue.shift()
          try {
            rotateIfNeeded()
            // A rotation closes the descriptor and renames the file: reopen the
            // fresh events.jsonl so the record that triggered the rotation is
            // written instead of being silently swallowed.
            if (fd === null && !ensureTarget()) break
            const buffer = Buffer.from(`${line}\n`, 'utf8')
            fs.writeSync(fd, buffer)
            counters.written += 1
            counters.bytesWritten += buffer.length
            lastWriteMs = now()
            written += 1
          } catch (error) {
            counters.writeErrors += 1
            counters.dropped += 1
            noteError(error)
            closeFd()
            break
          }
        }
      }
      runCleanup()
      writeCurrent(false)
      writeHealth(false)
    } catch (error) {
      noteError(error)
    } finally {
      if (closed) return
      if (queue.length > 0) {
        if (unavailable) scheduleAfter(unavailableRetryMs)
        else schedule()
      } else if (currentDirty) {
        scheduleAfter(Math.max(1, currentMinIntervalMs - (now() - lastCurrentWriteMs)))
      }
    }
  }

  /**
   * Queue one normalized record. Never throws, never touches the disk, never
   * awaits: the caller is a host event callback.
   * @param record - the normalized record object.
   * @returns true when the record entered the queue.
   */
  function enqueue(record) {
    if (closed) {
      counters.droppedClosed += 1
      counters.dropped += 1
      return false
    }
    let line
    try {
      line = JSON.stringify(record)
    } catch (error) {
      counters.droppedError += 1
      counters.dropped += 1
      noteError(error)
      return false
    }
    if (typeof line !== 'string') {
      counters.droppedError += 1
      counters.dropped += 1
      return false
    }
    if (Buffer.byteLength(line, 'utf8') > maxRecordBytes) {
      counters.droppedOversize += 1
      counters.dropped += 1
      schedule()
      return false
    }
    if (queue.length >= queueLimit) {
      queue.shift()
      counters.droppedOldest += 1
      counters.dropped += 1
    }
    queue.push(line)
    counters.enqueued += 1
    schedule()
    return true
  }

  /**
   * Stage the `current.json` document. The replacement happens on the writer
   * tick, atomically; a new call before that tick simply supersedes the document.
   * @param document - the current-state document.
   * @returns nothing.
   */
  function setCurrent(document) {
    if (closed) return
    try {
      currentDocument = document
      currentSerialized = `${JSON.stringify(document)}\n`
      currentDirty = true
      schedule()
    } catch (error) {
      counters.droppedError += 1
      noteError(error)
    }
  }

  /**
   * Drain everything synchronously, ignoring write throttling. Used by dispose
   * and by tests; the host callbacks never call it.
   * @returns nothing.
   */
  function flushSync() {
    let guard = 0
    while (!closed && guard < 100_000) {
      guard += 1
      if (queue.length > 0) {
        if (!ensureTarget()) break
        const line = queue.shift()
        try {
          rotateIfNeeded()
          // See drain(): the rotated record still has to be published.
          if (fd === null && !ensureTarget()) break
          const buffer = Buffer.from(`${line}\n`, 'utf8')
          fs.writeSync(fd, buffer)
          counters.written += 1
          counters.bytesWritten += buffer.length
          lastWriteMs = now()
        } catch (error) {
          counters.writeErrors += 1
          counters.dropped += 1
          noteError(error)
          closeFd()
          break
        }
        continue
      }
      if (currentDirty) {
        if (!writeCurrent(true)) break
        continue
      }
      break
    }
    runCleanup()
    writeHealth(true)
  }

  /**
   * Writer health snapshot (also the `health.json` content).
   * @returns the health document.
   */
  function health() {
    return healthDocument()
  }

  /**
   * Stop the writer: drain synchronously, flush `current.json` and
   * `health.json`, close the descriptor. Idempotent.
   * @returns nothing.
   */
  function close() {
    if (closed) return
    if (pendingTimer !== null) {
      try {
        if (typeof clearImmediate === 'function') clearImmediate(pendingTimer)
        clearTimeout(pendingTimer)
      } catch {
        // A timer that already fired needs no cancellation.
      }
      pendingTimer = null
    }
    flushSync()
    closed = true
    closeFd()
  }

  return {
    directory,
    eventsPath,
    currentPath,
    healthPath,
    queueLimit,
    maxRecordBytes,
    sourceEpoch: sourceEpochValue,
    enqueue,
    setCurrent,
    flushSync,
    drain,
    health,
    close,
    /** Queue depth, for tests and diagnostics. */
    get queueDepth() {
      return queue.length
    },
    /** Whether the spool has been closed. */
    get isClosed() {
      return closed
    },
  }
}
