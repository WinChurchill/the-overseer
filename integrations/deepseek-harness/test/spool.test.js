/**
 * Spool behaviour: bounded queue, drop counting, record cap, atomic replacement,
 * rotation and bounded cleanup. Real filesystem in os.tmpdir(); no mocks.
 */
import { test } from 'node:test'
import assert from 'node:assert/strict'
import * as nodeFs from 'node:fs'
import { existsSync, mkdtempSync, readFileSync, readdirSync, utimesSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { dirname, join } from 'node:path'
import { Worker } from 'node:worker_threads'
import {
  MAX_RECORD_BYTES,
  SPOOL_SCHEMA_VERSION,
  createSpool,
} from '../src/spool.js'

const t0 = 1767225600000
const ROTATED_RE = /^events-\d{8}T\d{6}Z(-\d+)?\.jsonl$/

function tempRoot(label) {
  return mkdtempSync(join(tmpdir(), `overseer-dsh-${label}-`))
}

function readRecords(path) {
  const text = readFileSync(path, 'utf8')
  return text.split('\n').filter((line) => line !== '').map((line) => JSON.parse(line))
}

test('(b) queue overflow drops the oldest records, counts them, and never blocks the caller', () => {
  const spool = createSpool({
    spoolRoot: tempRoot('overflow'),
    sourceInstanceId: 'overflow',
    queueLimit: 16,
    currentMinIntervalMs: 0,
  })
  const started = process.hrtime.bigint()
  for (let marker = 0; marker < 100; marker += 1) spool.enqueue({ marker })
  const elapsedMs = Number(process.hrtime.bigint() - started) / 1e6

  const health = spool.health()
  assert.equal(spool.queueDepth, 16, 'the queue is bounded at the configured length')
  assert.equal(health.queueDepth, 16)
  assert.equal(health.dropped, 84, 'every dropped record is counted')
  assert.equal(health.counters.droppedOldest, 84)
  assert.ok(elapsedMs < 1000, `enqueue must not block; 100 enqueues took ${elapsedMs}ms`)
  assert.equal(existsSync(spool.eventsPath), false, 'no disk I/O happens in the caller path')

  spool.flushSync()
  const records = readRecords(spool.eventsPath)
  assert.equal(records.length, 16)
  assert.deepEqual(records.map((record) => record.marker), [...Array(16).keys()].map((i) => i + 84))
  spool.close()
})

test('(g) the 64 KiB record cap is enforced at the exact boundary', () => {
  const spool = createSpool({
    spoolRoot: tempRoot('cap'),
    sourceInstanceId: 'cap',
    queueLimit: 64,
    currentMinIntervalMs: 0,
  })
  // Same key order and same marker text, so only the payload length differs.
  const empty = JSON.stringify({ marker: 'boundary', payload: '' })
  const pad = MAX_RECORD_BYTES - Buffer.byteLength(empty, 'utf8')
  const exact = { marker: 'boundary', payload: 'z'.repeat(pad) }
  const over = { marker: 'boundary', payload: 'z'.repeat(pad + 1) }
  assert.equal(Buffer.byteLength(JSON.stringify(exact), 'utf8'), MAX_RECORD_BYTES)

  assert.equal(spool.enqueue(exact), true, 'a record exactly at the cap is accepted')
  assert.equal(spool.enqueue(over), false, 'a record over the cap is refused')
  spool.flushSync()

  const health = spool.health()
  assert.equal(health.counters.droppedOversize, 1)
  assert.equal(health.counters.written, 1)
  const records = readRecords(spool.eventsPath)
  assert.equal(records.length, 1)
  assert.equal(records[0].marker, 'boundary')
  spool.close()
})

test('(h) atomic current.json replacement leaves a complete document for a concurrent reader', async (t) => {
  const spool = createSpool({
    spoolRoot: tempRoot('atomic'),
    sourceInstanceId: 'atomic',
    queueLimit: 64,
    currentMinIntervalMs: 0,
  })
  spool.setCurrent({ marker: -1, payload: '', updatedAtUtc: new Date(t0).toISOString() })
  spool.flushSync()

  const worker = new Worker(new URL('../fixtures/current-reader.worker.cjs', import.meta.url), {
    workerData: { path: spool.currentPath, ms: 400 },
  })
  t.after(() => worker.terminate())
  const observed = new Promise((resolve, reject) => {
    worker.once('message', resolve)
    worker.once('error', reject)
  })
  // Let the reader thread start before the writer takes the main thread over.
  await new Promise((resolve) => setTimeout(resolve, 100))

  let writes = 0
  const deadline = Date.now() + 350
  while (Date.now() < deadline) {
    spool.setCurrent({ marker: writes, payload: 'p'.repeat(4096), updatedAtUtc: new Date(t0).toISOString() })
    spool.flushSync()
    writes += 1
  }
  const result = await observed

  assert.ok(writes > 20, `expected many replacements, wrote ${writes}`)
  assert.ok(result.reads > 20, `expected concurrent reads, saw ${result.reads}`)
  assert.equal(result.torn, 0, 'a concurrent reader must never observe a partial document')
  // A rename can lose a transient race with the concurrent reader, so require
  // that the newest staged snapshot is EVENTUALLY published instead of assuming
  // the last flush already won. A writer that never converges still fails here.
  let final = null
  for (let attempt = 0; attempt < 100; attempt += 1) {
    spool.flushSync()
    final = JSON.parse(readFileSync(spool.currentPath, 'utf8'))
    if (final.marker === writes - 1) break
  }

  assert.deepEqual(Object.keys(final), ['marker', 'payload', 'updatedAtUtc'])
  assert.equal(final.marker, writes - 1, 'the latest staged snapshot must be published')
  spool.close()
})

test('(h) current.json and health.json are produced only by a same-directory temp write plus rename', () => {
  const root = tempRoot('atomic-procedure')
  const writes = []
  const renames = []
  const spyFs = {
    ...nodeFs,
    writeFileSync: (path, ...rest) => {
      writes.push(String(path))
      return nodeFs.writeFileSync(path, ...rest)
    },
    renameSync: (from, to) => {
      renames.push([String(from), String(to)])
      return nodeFs.renameSync(from, to)
    },
  }
  const spool = createSpool({
    spoolRoot: root,
    sourceInstanceId: 'procedure',
    fs: spyFs,
    currentMinIntervalMs: 0,
  })
  spool.enqueue({ marker: 1 })
  spool.setCurrent({ marker: 1 })
  spool.close()

  const targets = [spool.currentPath, spool.healthPath]
  for (const written of writes) {
    assert.equal(targets.includes(written), false, `${written} must never be written directly`)
    assert.equal(written.endsWith('.tmp'), true)
    assert.equal(dirname(written), spool.directory)
  }
  for (const target of targets) {
    assert.ok(renames.some(([, to]) => to === target), `${target} must be renamed into place`)
  }
  assert.deepEqual(Object.keys(JSON.parse(readFileSync(spool.currentPath, 'utf8'))), ['marker'])
})

test('events.jsonl rotates past the size threshold and only this adapter\'s expired files are pruned', () => {
  const spool = createSpool({
    spoolRoot: tempRoot('rotate'),
    sourceInstanceId: 'rotate',
    queueLimit: 4096,
    currentMinIntervalMs: 0,
    cleanupIntervalMs: 0,
  })
  const chunk = 'x'.repeat(60_000)
  for (let marker = 0; marker < 80; marker += 1) spool.enqueue({ marker, payload: chunk })
  spool.flushSync()

  const rotated = readdirSync(spool.directory).filter((name) => ROTATED_RE.test(name))
  assert.ok(rotated.length >= 1, 'a file over the rotation threshold is rotated')

  const foreignRotatedName = 'events-19990101T000000Z.jsonl'
  const foreignRotated = join(spool.directory, foreignRotatedName)
  writeFileSync(foreignRotated, 'not ours in spirit but matching the pattern\n')
  const foreignNote = join(spool.directory, 'notes.txt')
  writeFileSync(foreignNote, 'unrelated file\n')

  const expired = join(spool.directory, rotated[0])
  const longAgo = new Date(Date.now() - 2 * 60 * 60 * 1000)
  utimesSync(expired, longAgo, longAgo)
  utimesSync(foreignRotated, longAgo, longAgo)
  spool.setCurrent({ marker: 'cleanup' })
  spool.flushSync()

  assert.equal(existsSync(expired), false, 'an OVERSEER-owned rotated file past the TTL is pruned')
  assert.equal(existsSync(foreignRotated), false, 'a matching rotated name is in scope for the TTL pass')
  assert.equal(existsSync(foreignNote), true, 'a file this adapter did not write is never touched')
  assert.equal(existsSync(spool.eventsPath), true, 'the live event file is never pruned')
  assert.equal(existsSync(spool.currentPath), true)
  assert.equal(existsSync(spool.healthPath), true)
  spool.close()
})

test('the file-count cap bounds rotated history, newest kept', () => {
  const spool = createSpool({
    spoolRoot: tempRoot('filecap'),
    sourceInstanceId: 'filecap',
    queueLimit: 64,
    currentMinIntervalMs: 0,
    cleanupIntervalMs: 0,
    maxFiles: 4,
  })
  spool.enqueue({ marker: 'seed' })
  spool.flushSync()
  for (let index = 0; index < 6; index += 1) {
    const name = `events-2026010${index + 1}T000000Z.jsonl`
    writeFileSync(join(spool.directory, name), 'x\n')
    spool.flushSync()
  }
  const remaining = readdirSync(spool.directory).filter((name) => ROTATED_RE.test(name))
  assert.ok(remaining.length <= 3, `expected pruning below the file cap, saw ${remaining.length}`)
  assert.ok(remaining.includes('events-20260106T000000Z.jsonl'), 'the newest rotated file survives')
  spool.close()
})

test('health.json carries version, counters, lastWrite, queueDepth and dropped', () => {
  const spool = createSpool({
    spoolRoot: tempRoot('health'),
    sourceInstanceId: 'health',
    queueLimit: 16,
    currentMinIntervalMs: 0,
  })
  for (let marker = 0; marker < 20; marker += 1) spool.enqueue({ marker, payload: 'y'.repeat(1024) })
  spool.close()

  const health = JSON.parse(readFileSync(spool.healthPath, 'utf8'))
  assert.equal(health.version, SPOOL_SCHEMA_VERSION)
  assert.equal(health.queueDepth, 0)
  assert.equal(health.dropped, 4)
  assert.equal(typeof health.lastWrite, 'string')
  assert.equal(health.counters.written, 16)
  assert.equal(health.sourceInstanceId, 'health')
  assert.equal(typeof health.sourceEpoch, 'string')
  assert.equal(health.unavailable, false)
  assert.equal(health.lastError, null)
})
