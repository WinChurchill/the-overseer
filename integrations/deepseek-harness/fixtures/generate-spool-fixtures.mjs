/**
 * Regenerate the committed spool fixtures from the REAL observer modules.
 *
 * These are not hand-written documents: they are produced by
 * `src/state.js` (the shipped sequence/hydration state machine) and
 * `src/spool.js` (the shipped atomic writer, rotation and drop accounting), so a
 * consumer test exercises the producer's actual output shape, including
 * `current.json`, `health.json`, rotated event files and drop counters.
 *
 * Run: node integrations/deepseek-harness/fixtures/generate-spool-fixtures.mjs
 * Output: integrations/deepseek-harness/fixtures/spool/{live,rotated,dropped}/
 *
 * Every value is synthetic. No real session, path or credential is involved.
 */
import { mkdirSync, readdirSync, readFileSync, rmSync, writeFileSync } from 'node:fs'
import { dirname, join } from 'node:path'
import { fileURLToPath } from 'node:url'
import { createObserverState } from '../src/state.js'
import { createSpool } from '../src/spool.js'

const T0 = 1767225600000
const SOURCE = 'dsh-fixture'
const EPOCH = 'dsh-fixture-epoch-1'
const here = dirname(fileURLToPath(import.meta.url))
const outRoot = join(here, 'spool')
const catalog = JSON.parse(readFileSync(join(here, 'session-events.json'), 'utf8'))

/** Deterministic clock: every read advances by a fixed step. */
function clock(stepMs) {
  let value = T0
  return () => {
    const current = value
    value += stepMs
    return current
  }
}

function scenario(name, { nowStepMs = 10, queueLimit = 64, eventFileBytes = 4 * 1024 * 1024, directory = null } = {}) {
  rmSync(join(outRoot, name), { recursive: true, force: true })
  const now = clock(nowStepMs)
  // Keep the original non-question protocol fixtures on the old producer so the
  // consumer continues to prove backwards-compatible capability disclosure.
  const state = createObserverState({ sourceInstanceId: SOURCE, sourceEpoch: EPOCH, producerVersion: 'dsh-observer/1.0.0', now })
  const spool = createSpool({
    spoolRoot: directory ?? join(outRoot, name, 'root'),
    sourceInstanceId: SOURCE,
    sourceEpoch: EPOCH,
    queueLimit,
    currentMinIntervalMs: 0,
    eventFileBytes,
    now,
  })
  const emit = (record) => {
    if (record === null || record === undefined) return null
    spool.enqueue(record)
    spool.setCurrent(state.currentDocument())
    return record
  }
  return { now, state, spool, emit, outDir: spool.directory }
}

function closeFixture(target) {
  target.spool.close()
  // The producer writes its real spool location into this diagnostic field.
  const healthPath = join(target.outDir, 'health.json')
  const health = JSON.parse(readFileSync(healthPath, 'utf8'))
  health.directory = `C:/synthetic/bridges/dsh/${SOURCE}`
  writeFileSync(healthPath, JSON.stringify(health) + '\n')
}

/** Names of the rotated event files this adapter owns, sorted for a stable fixture. */
function listRotated(directory) {
  return readdirSync(directory).filter((name) => /^events-\d{8}T\d{6}Z(-\d+)?\.jsonl$/.test(name)).sort()
}

function sessionById(id) {
  const found = catalog.sessions.find((session) => session.sessionId === id)
  if (found === undefined) throw new Error('unknown fixture session ' + id)
  return found
}

function seedSession(target, id) {
  const session = sessionById(id)
  target.emit(target.state.sessionStart(id, {
    projectRoot: session.projectRoot,
    createdAtMs: T0,
    parentSessionId: session.parentSessionId ?? undefined,
  }))
  return session
}

function replayEvents(target, id, events) {
  target.state.reconcileQuestions(id, { timed: false, activeIds: [] })
  let records = 0
  for (const event of events) {
    const record = target.emit(target.state.observe({ sessionId: id, type: event.type, seq: event.seq, time: event.time, data: event.data }))
    if (record !== null) records += 1
  }
  return records
}

// --- live: an attached observer with an open turn, plan mode and a heartbeat ---
{
  const target = scenario('live')
  seedSession(target, 'session-fixture-parent')
  const parent = sessionById('session-fixture-parent')
  replayEvents(target, 'session-fixture-parent', parent.events)
  target.emit(target.state.sessionStart('session-fixture-child', {
    projectRoot: parent.projectRoot,
    createdAtMs: T0 + 10_000,
    parentSessionId: 'session-fixture-parent',
  }))
  const child = sessionById('session-fixture-child')
  replayEvents(target, 'session-fixture-child', child.events)
  for (const record of target.state.heartbeats()) target.emit(record)
  target.spool.flushSync()
  const health = target.spool.health()
  closeFixture(target)
  writeFileSync(join(outRoot, 'live', 'expected.json'), JSON.stringify({
    description: 'What the live fixture must contain; generated, not hand-written.',
    sourceInstanceId: SOURCE,
    sourceEpoch: EPOCH,
    openTurnSessions: health.queueDepth === 0 ? target.state.currentDocument().sessions.filter((s) => s.openTurnId !== null).map((s) => s.sessionId) : [],
    sessions: target.state.currentDocument().sessions.map((s) => s.sessionId),
    dropped: health.dropped,
    records: health.counters.written,
  }, null, 2) + '\n')
  console.log('live:', target.outDir, 'written=' + health.counters.written, 'dropped=' + health.dropped)
}

// --- rotated: the writer rotated events.jsonl, leaving an unread tail ---
{
  const target = scenario('rotated', { eventFileBytes: 1024, nowStepMs: 20 })
  seedSession(target, 'session-fixture-parent')
  const parent = sessionById('session-fixture-parent')
  replayEvents(target, 'session-fixture-parent', parent.events)
  // The rotation happens inside the drain path, exactly as in production.
  target.spool.flushSync()
  const records = target.spool.health().counters.written
  seedSession(target, 'session-fixture-child')
  const child = sessionById('session-fixture-child')
  replayEvents(target, 'session-fixture-child', child.events)
  target.spool.flushSync()
  // One more observation after the last rotation, so events.jsonl exists again
  // with the newest record while the rotated files hold the earlier stream.
  for (const record of target.state.heartbeats()) target.emit(record)
  target.spool.flushSync()
  const health = target.spool.health()
  closeFixture(target)
  writeFileSync(join(outRoot, 'rotated', 'expected.json'), JSON.stringify({
    description: 'Rotation happened in the middle of the stream; the tail of events.jsonl follows the rotated file.',
    sourceInstanceId: SOURCE,
    sourceEpoch: EPOCH,
    rotations: health.counters.rotations,
    recordsBeforeRotation: records,
    rotatedFiles: listRotated(target.outDir),
    sessions: target.state.currentDocument().sessions.map((s) => s.sessionId),
    dropped: health.dropped,
    records: health.counters.written,
  }, null, 2) + '\n')
  console.log('rotated:', target.outDir, 'rotations=' + health.counters.rotations, 'written=' + health.counters.written)
}

// --- dropped: the bounded queue dropped the oldest records ---
{
  const target = scenario('dropped', { queueLimit: 4, nowStepMs: 5 })
  seedSession(target, 'session-fixture-parent')
  const parent = sessionById('session-fixture-parent')
  replayEvents(target, 'session-fixture-parent', parent.events)
  seedSession(target, 'session-fixture-child')
  const child = sessionById('session-fixture-child')
  replayEvents(target, 'session-fixture-child', child.events)
  for (const record of target.state.heartbeats()) target.emit(record)
  target.spool.flushSync()
  const health = target.spool.health()
  closeFixture(target)
  writeFileSync(join(outRoot, 'dropped', 'expected.json'), JSON.stringify({
    description: 'The queue dropped the oldest records; current.json still reflects the full folded state.',
    sourceInstanceId: SOURCE,
    sourceEpoch: EPOCH,
    dropped: health.dropped,
    droppedOldest: health.counters.droppedOldest,
    written: health.counters.written,
    sessions: target.state.currentDocument().sessions.map((s) => s.sessionId),
    lastSequence: target.state.currentDocument().sessions[0]?.lastSequence ?? null,
  }, null, 2) + '\n')
  console.log('dropped:', target.outDir, 'dropped=' + health.dropped, 'written=' + health.counters.written)
}

mkdirSync(outRoot, { recursive: true })
console.log('fixtures written under', outRoot)
