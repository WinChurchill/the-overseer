/**
 * Plugin-level behaviour: host-callback containment, attach snapshot, seam
 * discipline, privacy of the exported metadata, and configuration clamps.
 */
import { test } from 'node:test'
import assert from 'node:assert/strict'
import * as nodeFs from 'node:fs'
import { existsSync, mkdtempSync, readFileSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { fileURLToPath } from 'node:url'
import {
  DEFAULT_SOURCE_ID,
  apply,
  createObserver,
  defaultSpoolRoot,
  isDisabled,
  resolveConfig,
  sanitizeSourceId,
} from '../src/index.js'
import { CATEGORIES, EVIDENCES, KINDS, OUTCOMES, RECORD_KEYS } from '../src/normalize.js'

const t0 = 1767225600000
const fixturesDir = fileURLToPath(new URL('../fixtures/', import.meta.url))

function tempRoot(label) {
  return mkdtempSync(join(tmpdir(), `overseer-dsh-${label}-`))
}

function readJson(name) {
  return JSON.parse(readFileSync(join(fixturesDir, name), 'utf8'))
}

function readRecords(path) {
  return readFileSync(path, 'utf8').split('\n').filter((line) => line !== '').map((line) => JSON.parse(line))
}

function fakeContext(services = {}) {
  const listeners = new Map()
  const effects = []
  const waterfalls = []
  return {
    listeners,
    effects,
    waterfalls,
    on(name, handler) {
      const list = listeners.get(name) ?? []
      list.push(handler)
      listeners.set(name, list)
      return () => {
        const current = listeners.get(name) ?? []
        const index = current.indexOf(handler)
        if (index >= 0) current.splice(index, 1)
      }
    },
    get(name) {
      return services[name]
    },
    effect(fn) {
      const cleanup = fn()
      effects.push({ fn, cleanup })
      return cleanup
    },
    waterfall(name) {
      waterfalls.push(name)
      return Promise.resolve(undefined)
    },
    emit(name, ...args) {
      for (const handler of [...(listeners.get(name) ?? [])]) handler(...args)
    },
  }
}

function fakeSession(sessionId, header = { cwd: 'C:/demo' }) {
  return {
    id: sessionId,
    header: { version: 4, id: sessionId, createdAt: t0, isSeeded: false, ...header },
    seq: 0,
    eventAt: () => undefined,
  }
}

function fakeLiveSession(entry) {
  return {
    id: entry.sessionId,
    header: entry.header,
    seq: entry.events.length,
    eventAt: (index) => entry.events[index],
  }
}

test('(a) host callbacks never throw when the spool target is unwritable', async (t) => {
  const root = tempRoot('unwritable')
  const blocker = join(root, 'blocker')
  writeFileSync(blocker, 'a file, not a directory\n')
  const observer = createObserver({
    spoolRoot: join(blocker, 'nested'),
    sourceInstanceId: 'unwritable',
    queueLimit: 16,
    currentMinIntervalMs: 0,
  })
  t.after(() => observer.dispose())
  const ctx = fakeContext()
  const session = fakeSession('session-unwritable')
  observer.attach(ctx)

  assert.doesNotThrow(() => ctx.emit('session/created', session), 'a session/created listener throw would roll back publication')
  for (let seq = 0; seq < 40; seq += 1) {
    assert.doesNotThrow(() => ctx.emit('session/event', session, {
      type: 'turn/start',
      seq,
      time: t0 + seq,
      data: { turn: 1 },
    }))
  }
  assert.doesNotThrow(() => ctx.emit('session/disposed', session))
  assert.doesNotThrow(() => observer.publishHeartbeat())
  assert.doesNotThrow(() => observer.publishSnapshot(ctx))
  assert.doesNotThrow(() => observer.spool.flushSync())
  await new Promise((resolve) => setTimeout(resolve, 50))

  const health = observer.health()
  assert.equal(health.unavailable, true, 'the spool reports itself unavailable')
  assert.ok(health.counters.unavailable > 0, 'failed spool attempts are counted')
  assert.equal(health.internalErrors, 0, 'surviving a dead spool needs no swallowed exception')
  assert.ok(health.dropped >= 24, 'overflow past the bounded queue is counted')
  assert.equal(existsSync(observer.spool.eventsPath), false)
})

test('(a) a disk failure degrades to counters without throwing', () => {
  const failingFs = {
    ...nodeFs,
    openSync: () => {
      throw Object.assign(new Error('EACCES: permission denied'), { code: 'EACCES' })
    },
  }
  const observer = createObserver({
    spoolRoot: tempRoot('disk-failure'),
    sourceInstanceId: 'disk-failure',
    fs: failingFs,
  })
  const ctx = fakeContext()
  observer.attach(ctx)
  assert.doesNotThrow(() => ctx.emit('session/created', fakeSession('session-disk')))
  assert.doesNotThrow(() => ctx.emit('session/event', fakeSession('session-disk'), {
    type: 'turn/start',
    seq: 0,
    time: t0,
    data: { turn: 1 },
  }))
  assert.doesNotThrow(() => observer.spool.flushSync())
  const health = observer.health()
  assert.equal(health.unavailable, true)
  assert.ok(health.counters.unavailable > 0)
  assert.equal(typeof health.lastError.message, 'string')
  assert.equal(health.internalErrors, 0)
  observer.dispose()
})

test('(e) attach publishes hydration-flagged snapshots, then heartbeats', () => {
  const fixture = readJson('live-sessions.json')
  const observer = createObserver({
    spoolRoot: tempRoot('snapshot'),
    sourceInstanceId: 'snapshot',
    currentMinIntervalMs: 0,
  })
  const ctx = fakeContext({ sessions: { list: () => fixture.sessions.map(fakeLiveSession) } })
  observer.attach(ctx)
  observer.spool.flushSync()

  const records = readRecords(observer.spool.eventsPath)
  const snapshots = records.filter((record) => record.kind === 'snapshot')
  assert.equal(snapshots.length, 2, 'one snapshot per known session')
  for (const snapshot of snapshots) {
    assert.deepEqual(Object.keys(snapshot), RECORD_KEYS)
    assert.equal(snapshot.hydration, true, 'replayed state is hydration, so OVERSEER stays silent')
    assert.equal(snapshot.evidence, 'inferred')
    assert.ok(KINDS.has(snapshot.kind))
  }
  const root = snapshots.find((record) => record.sessionId === 'session-live-root')
  assert.equal(root.category, 'working', 'the recovered open turn is working; plan mode is explicit state, not a category')
  assert.equal(root.turnId, 'dsh:session-live-root:turn:7')
  assert.equal(root.projectRoot, 'C:\\fixtures\\project')
  const child = snapshots.find((record) => record.sessionId === 'session-live-child')
  assert.equal(child.turnId, null, 'the child turn had already ended')
  assert.equal(child.category, 'idle')

  const heartbeats = records.filter((record) => record.kind === 'heartbeat')
  assert.ok(heartbeats.length >= 2, 'an attach publishes its heartbeat batch immediately')
  for (const heartbeat of heartbeats) {
    assert.equal(heartbeat.hydration, false)
    assert.equal(heartbeat.evidence, 'inferred')
  }

  const current = JSON.parse(readFileSync(observer.spool.currentPath, 'utf8'))
  const childEntry = current.sessions.find((entry) => entry.sessionId === 'session-live-child')
  assert.equal(childEntry.parentSessionId, 'session-live-root', 'fork lineage is exported in current.json')
  assert.equal(childEntry.planMode, false)
  const rootEntry = current.sessions.find((entry) => entry.sessionId === 'session-live-root')
  assert.equal(rootEntry.openTurnId, 'dsh:session-live-root:turn:7')
  assert.equal(rootEntry.planMode, true, 'plan mode was recovered from the trailing plan/mode event')
  assert.equal(rootEntry.category, 'working')
  observer.dispose()
})

test('a snapshot is published even when no sessions service is mounted', () => {
  const observer = createObserver({ spoolRoot: tempRoot('no-service'), sourceInstanceId: 'no-service', currentMinIntervalMs: 0 })
  const ctx = fakeContext()
  observer.attach(ctx)
  observer.spool.flushSync()
  const records = readRecords(observer.spool.eventsPath)
  const snapshot = records.find((record) => record.kind === 'snapshot')
  assert.equal(snapshot.sessionId, null)
  assert.equal(snapshot.hydration, true)
  assert.equal(snapshot.category, 'unknown')
  observer.dispose()
})

test('the observer subscribes to exactly the three observe-only seams', () => {
  const observer = createObserver({ spoolRoot: tempRoot('seams'), sourceInstanceId: 'seams' })
  const ctx = fakeContext()
  observer.attach(ctx)
  assert.deepEqual([...ctx.listeners.keys()].sort(), ['session/created', 'session/disposed', 'session/event'])
  assert.equal(ctx.listeners.has('session/flush'), false, 'never in the awaited durability path')
  assert.deepEqual(ctx.waterfalls, [], 'no waterfall is ever intercepted')
  assert.deepEqual(observer.seams, ['session/created', 'session/event', 'session/disposed'])
  observer.dispose()
})

test('the fixtures\' message bodies, tool arguments, outputs, reasons and credentials never reach the spool', () => {
  const fixture = readJson('session-events.json')
  const observer = createObserver({ spoolRoot: tempRoot('privacy'), sourceInstanceId: 'privacy', currentMinIntervalMs: 0 })
  const ctx = fakeContext()
  observer.attach(ctx)

  for (const entry of fixture.sessions) {
    const header = { version: 4, id: entry.sessionId, createdAt: t0, isSeeded: false }
    if (entry.projectRoot !== null) header.cwd = entry.projectRoot
    if (entry.parentSessionId !== undefined) header.parentSession = entry.parentSessionId
    const session = { id: entry.sessionId, header, seq: 0, eventAt: () => undefined }
    ctx.emit('session/created', session)
    for (const event of entry.events) ctx.emit('session/event', session, event)
  }
  observer.spool.flushSync()

  const exported = [
    readFileSync(observer.spool.eventsPath, 'utf8'),
    readFileSync(observer.spool.currentPath, 'utf8'),
    readFileSync(observer.spool.healthPath, 'utf8'),
  ].join('\n')
  for (const canary of fixture.canaries) {
    assert.equal(exported.includes(canary), false, `${canary} must never be exported`)
  }

  const records = readRecords(observer.spool.eventsPath)
  assert.ok(records.length > 10)
  for (const record of records) {
    assert.deepEqual(Object.keys(record), RECORD_KEYS, 'no record carries an unknown key')
    assert.ok(KINDS.has(record.kind))
    assert.ok(CATEGORIES.has(record.category))
    assert.ok(EVIDENCES.has(record.evidence))
    assert.ok(record.outcome === null || OUTCOMES.has(record.outcome))
  }
  const kinds = new Set(records.map((record) => record.kind))
  assert.ok(kinds.has('turn/start'))
  assert.ok(kinds.has('tool/start'))
  assert.ok(kinds.has('tool/end'))
  assert.ok(kinds.has('approval/asked'))
  assert.ok(kinds.has('approval/decided'))
  assert.ok(kinds.has('plan/mode'))
  assert.ok(kinds.has('turn/end'))
  assert.ok(kinds.has('session/start'))

  const failedTool = records.find((record) => record.kind === 'tool/end' && record.outcome === 'failed')
  assert.equal(failedTool.callId, 'call-2')
  const allowed = records.find((record) => record.kind === 'approval/decided' && record.requestId === 'approval-1')
  assert.equal(allowed.outcome, 'ok')
  const errorTurn = records.find((record) => record.kind === 'turn/end' && record.outcome === 'error')
  assert.equal(errorTurn.category, 'error')
  const abortedTurn = records.find((record) => record.kind === 'turn/end' && record.outcome === 'aborted' && record.evidence === 'explicit')
  assert.equal(abortedTurn.category, 'idle')
  const unknownTurn = records.find((record) => record.sessionId === 'session-fixture-unknown-reason' && record.kind === 'turn/end')
  assert.equal(unknownTurn.evidence, 'unknown')

  const planning = records.filter((record) => record.category === 'planning')
  assert.equal(planning.length, 1, 'planning appears only on the plan/mode record that switched plan mode on')
  assert.equal(planning[0].kind, 'plan/mode')
  assert.equal(planning[0].evidence, 'explicit')
  const lateTool = records.find((record) => record.sessionId === 'session-fixture-child' && record.kind === 'tool/start')
  assert.equal(lateTool.category, 'unknown')
  assert.equal(lateTool.evidence, 'inferred')

  for (const line of readFileSync(observer.spool.eventsPath, 'utf8').split('\n')) {
    if (line === '') continue
    assert.doesNotThrow(() => JSON.parse(line), 'every line is a complete JSON document')
  }
  observer.dispose()
})

test('OVERSEER_DSH_DISABLED disables every subscription and every write', () => {
  assert.equal(isDisabled({ OVERSEER_DSH_DISABLED: '1' }), true)
  assert.equal(isDisabled({ OVERSEER_DSH_DISABLED: '0' }), false)
  assert.equal(resolveConfig({ OVERSEER_DSH_DISABLED: '1' }, {}).disabled, true)

  const observer = createObserver({ spoolRoot: tempRoot('disabled'), sourceInstanceId: 'disabled', disabled: true })
  const ctx = fakeContext()
  observer.attach(ctx)
  assert.equal(ctx.listeners.size, 0)
  assert.equal(ctx.effects.length, 0)
  ctx.emit('session/created', fakeSession('session-disabled'))
  assert.equal(existsSync(observer.spool.eventsPath), false)
  const health = observer.health()
  assert.equal(health.attached, false)
  assert.equal(health.seams.length, 0)
  observer.dispose()
})

test('environment knobs have documented defaults and hard clamps', () => {
  const defaults = resolveConfig({}, {})
  assert.equal(defaults.sourceInstanceId, DEFAULT_SOURCE_ID)
  assert.equal(defaults.queueLimit, 512)
  assert.equal(defaults.heartbeatMs, 30_000)
  assert.equal(defaults.disabled, false)
  assert.equal(defaults.spoolRoot, defaultSpoolRoot({}))
  assert.match(defaults.spoolRoot, /bridges$/)

  assert.equal(resolveConfig({}, { queueLimit: 999_999 }).queueLimit, 8192)
  assert.equal(resolveConfig({}, { queueLimit: 1 }).queueLimit, 16)
  assert.equal(resolveConfig({}, { queueLimit: 'nonsense' }).queueLimit, 512)
  assert.equal(resolveConfig({ OVERSEER_DSH_QUEUE: '256' }, {}).queueLimit, 256)
  assert.equal(resolveConfig({}, { heartbeatMs: 1 }).heartbeatMs, 5000)
  assert.equal(resolveConfig({}, { heartbeatMs: 10 * 60_000 }).heartbeatMs, 300_000)
  assert.equal(resolveConfig({ OVERSEER_DSH_HEARTBEAT_SECONDS: '45' }, {}).heartbeatMs, 45_000)
  assert.equal(resolveConfig({ OVERSEER_DSH_SPOOL: 'D:/overseer/spool' }, {}).spoolRoot, 'D:/overseer/spool')
  assert.equal(sanitizeSourceId('a/b c'), 'a_b_c')
  assert.equal(sanitizeSourceId('   '), DEFAULT_SOURCE_ID)

  const observer = createObserver({ spoolRoot: tempRoot('clamps'), heartbeatMs: 1, queueLimit: 2 })
  const health = observer.health()
  assert.equal(health.heartbeatMs, 5000)
  assert.equal(health.queueLimit, 16)
  observer.dispose()
})

test('apply() mounts through a Cordis-shaped context and disposes through ctx.effect', () => {
  const ctx = fakeContext()
  const observer = apply(ctx, { spoolRoot: tempRoot('apply'), sourceInstanceId: 'apply', currentMinIntervalMs: 0 })
  assert.deepEqual(observer.seams, ['session/created', 'session/event', 'session/disposed'])
  assert.equal(ctx.effects.length, 1, 'the plugin registers one effect for its teardown')
  assert.equal(observer.health().attached, true)

  ctx.emit('session/created', fakeSession('session-apply'))
  observer.spool.flushSync()
  assert.equal(readRecords(observer.spool.eventsPath).some((record) => record.kind === 'session/start'), true)

  ctx.effects[0].cleanup()
  assert.equal(observer.health().disposed, true)
  const written = readRecords(observer.spool.eventsPath).length
  ctx.emit('session/created', fakeSession('session-after-dispose'))
  observer.spool.flushSync()
  assert.equal(readRecords(observer.spool.eventsPath).length, written, 'a disposed observer writes nothing more')
})

test('apply() never throws into the host, even with a hostile context', () => {
  const hostile = {
    on() {
      throw new Error('host rejected the listener')
    },
    get() {
      throw new Error('host rejected the lookup')
    },
  }
  assert.doesNotThrow(() => apply(hostile, { spoolRoot: tempRoot('hostile'), sourceInstanceId: 'hostile' }))
})
