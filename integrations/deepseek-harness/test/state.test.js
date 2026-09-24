/**
 * Observer state behaviour: per-session ordering, duplicate publication,
 * late-event containment, and the documented mappings.
 */
import { test } from 'node:test'
import assert from 'node:assert/strict'
import {
  CATEGORIES,
  EVIDENCES,
  KINDS,
  LIMITS,
  OUTCOMES,
  RECORD_KEYS,
} from '../src/normalize.js'
import { createObserverState } from '../src/state.js'

const t0 = 1767225600000

function newState(epoch = 'epoch-test', now = () => t0) {
  return createObserverState({
    sourceInstanceId: 'test-source',
    sourceEpoch: epoch,
    producerVersion: 'dsh-observer/1.0.0',
    now,
  })
}

function assertShape(record) {
  assert.deepEqual(Object.keys(record), RECORD_KEYS, 'a record carries exactly the allowlisted keys')
  assert.equal(record.schemaVersion, 1)
  assert.equal(record.harnessId, 'dsh')
  assert.equal(record.eventId, `dsh:${record.sessionId ?? '*' }:${record.sequence}`)
  assert.ok(KINDS.has(record.kind), `kind ${record.kind} is allowlisted`)
  assert.ok(CATEGORIES.has(record.category), `category ${record.category} is allowlisted`)
  assert.ok(EVIDENCES.has(record.evidence), `evidence ${record.evidence} is allowlisted`)
  assert.ok(record.outcome === null || OUTCOMES.has(record.outcome), `outcome ${record.outcome} is allowlisted`)
  assert.equal(typeof record.observedAtUtc, 'string')
  assert.equal(record.producerVersion, 'dsh-observer/1.0.0')
}

test('(c) concurrent observation keeps a strictly increasing sequence per session', () => {
  const state = newState()
  const sessionIds = ['session-a', 'session-b', 'session-c']
  for (const sessionId of sessionIds) assertShape(state.sessionStart(sessionId, { projectRoot: 'C:/demo' }))

  const records = []
  for (let index = 0; index < 50; index += 1) {
    for (const sessionId of sessionIds) {
      records.push(state.observe({
        sessionId,
        type: 'turn/start',
        seq: index * 2,
        time: t0 + index,
        data: { turn: 1 },
      }))
      records.push(state.observe({
        sessionId,
        type: 'tool/call',
        seq: index * 2 + 1,
        time: t0 + index,
        data: { turn: 1, step: 1, callId: `call-${index}`, name: 'ignored', arguments: 'ignored' },
      }))
    }
  }
  records.push(...state.heartbeats())

  for (const sessionId of sessionIds) {
    const owned = records.filter((record) => record !== null && record.sessionId === sessionId)
    assert.ok(owned.length > 50, `${sessionId} produced records`)
    for (let index = 1; index < owned.length; index += 1) {
      assert.ok(
        owned[index].sequence > owned[index - 1].sequence,
        `${sessionId}: sequence ${owned[index].sequence} must exceed ${owned[index - 1].sequence}`,
      )
    }
  }
  const eventIds = records.filter((record) => record !== null).map((record) => record.eventId)
  assert.equal(new Set(eventIds).size, eventIds.length, 'no two logical events share an event id')
  for (const record of records.filter((entry) => entry !== null)) assertShape(record)
})

test('(d) duplicate publication keeps the same eventId and flags hydration', () => {
  const state = newState()
  state.sessionStart('session-dup', {})
  const delivery = { type: 'tool/call', seq: 4, time: t0, data: { turn: 1, step: 1, callId: 'call-1', name: 'x', arguments: 'y' } }
  const first = state.observe({ sessionId: 'session-dup', ...delivery })
  const duplicate = state.observe({ sessionId: 'session-dup', ...delivery })

  assert.equal(first.hydration, false)
  assert.equal(duplicate.hydration, true, 'a re-delivered logical event is hydration, so OVERSEER suppresses voice')
  assert.equal(duplicate.eventId, first.eventId)
  assert.equal(duplicate.sequence, first.sequence)
  assert.equal(duplicate.kind, first.kind)

  // A reconnect under a NEW source epoch re-sends the same host seq: same eventId.
  const reconnected = newState('epoch-after-restart')
  const replay = reconnected.observe({ sessionId: 'session-dup', ...delivery })
  assert.equal(replay.eventId, first.eventId)
  assert.equal(replay.sourceEpoch, 'epoch-after-restart')
  assert.equal(replay.sequence, 4, 'the host seq is the sequence when it is available')
})

test('(f) a late tool-start after turn end never reopens the turn', () => {
  const state = newState()
  state.sessionStart('session-late', {})
  state.observe({ sessionId: 'session-late', type: 'turn/start', seq: 0, time: t0, data: { turn: 1 } })
  assert.equal(state.inspect('session-late').openTurn, 1)

  state.observe({
    sessionId: 'session-late',
    type: 'turn/end',
    seq: 1,
    time: t0 + 1,
    data: { turn: 1, reason: { kind: 'completed' } },
  })
  const ended = state.inspect('session-late')
  assert.equal(ended.openTurn, null)
  assert.equal(ended.category, 'idle')

  // Rule: a tool event for an ended turn is still published (the observation is
  // real) but it reports no active category, evidence becomes inferred, and the
  // tracked open turn and category are untouched.
  const late = state.observe({
    sessionId: 'session-late',
    type: 'tool/call',
    seq: 2,
    time: t0 + 2,
    data: { turn: 1, step: 1, callId: 'call-late', name: 'x', arguments: 'y' },
  })
  assertShape(late)
  assert.equal(late.kind, 'tool/start')
  assert.equal(late.category, 'unknown')
  assert.equal(late.evidence, 'inferred')
  assert.equal(late.turnId, 'dsh:session-late:turn:1')

  const after = state.inspect('session-late')
  assert.equal(after.openTurn, null, 'the closed turn is not reopened')
  assert.equal(after.category, 'idle')
  const beat = state.heartbeats().find((record) => record.sessionId === 'session-late')
  assert.equal(beat.category, 'idle', 'the heartbeat still reports idle, not the late tool')

  const lateResult = state.observe({
    sessionId: 'session-late',
    type: 'tool/result',
    seq: 3,
    time: t0 + 3,
    data: { turn: 1, step: 1, message: { role: 'tool', id: 'm', toolCallId: 'call-late', isError: false } },
  })
  assert.equal(lateResult.kind, 'tool/end')
  assert.equal(lateResult.outcome, 'ok')
  assert.equal(state.inspect('session-late').openTurn, null)
})

test('a replayed turn/start for a closed turn does not restart activity', () => {
  const state = newState()
  state.observe({ sessionId: 'session-replay', type: 'turn/start', seq: 0, time: t0, data: { turn: 1 } })
  state.observe({ sessionId: 'session-replay', type: 'turn/end', seq: 1, time: t0, data: { turn: 1, reason: { kind: 'completed' } } })
  const replayed = state.observe({ sessionId: 'session-replay', type: 'turn/start', seq: 2, time: t0, data: { turn: 1 } })
  assert.equal(replayed.category, 'unknown')
  assert.equal(replayed.evidence, 'inferred')
  assert.equal(state.inspect('session-replay').openTurn, null)
})

test('turn/end reasons map to the documented categories and outcomes', () => {
  const expected = {
    completed: { category: 'complete', outcome: 'completed' },
    aborted: { category: 'idle', outcome: 'aborted' },
    error: { category: 'error', outcome: 'error' },
    'max-tokens': { category: 'unknown', outcome: 'aborted' },
    blocked: { category: 'unknown', outcome: 'aborted' },
    interrupted: { category: 'error', outcome: 'aborted' },
    forked: { category: 'unknown', outcome: 'aborted' },
  }
  for (const [kind, mapped] of Object.entries(expected)) {
    const state = newState()
    state.observe({ sessionId: `session-${kind}`, type: 'turn/start', seq: 0, time: t0, data: { turn: 1 } })
    const record = state.observe({
      sessionId: `session-${kind}`,
      type: 'turn/end',
      seq: 1,
      time: t0,
      data: { turn: 1, reason: { kind } },
    })
    assertShape(record)
    assert.equal(record.category, mapped.category, `${kind} category`)
    assert.equal(record.outcome, mapped.outcome, `${kind} outcome`)
    assert.equal(record.evidence, 'explicit')
    assert.equal(state.inspect(`session-${kind}`).openTurn, null)
  }

  // Merge-extensible upstream reason: reported as unknown evidence, never guessed.
  const state = newState()
  const unknown = state.observe({
    sessionId: 'session-unknown-reason',
    type: 'turn/end',
    seq: 0,
    time: t0,
    data: { turn: 1, reason: { kind: 'added-by-a-newer-harness' } },
  })
  assert.equal(unknown.category, 'unknown')
  assert.equal(unknown.outcome, null)
  assert.equal(unknown.evidence, 'unknown')
})

test('tool/end maps ok and failed, and a failed tool is not a task failure', () => {
  const state = newState()
  state.observe({ sessionId: 'session-tool', type: 'turn/start', seq: 0, time: t0, data: { turn: 1 } })
  const failed = state.observe({
    sessionId: 'session-tool',
    type: 'tool/result',
    seq: 1,
    time: t0,
    data: { turn: 1, step: 1, message: { role: 'tool', id: 'm', toolCallId: 'call-9', isError: true } },
  })
  assert.equal(failed.kind, 'tool/end')
  assert.equal(failed.outcome, 'failed')
  assert.equal(failed.callId, 'call-9')
  assert.equal(failed.turnId, 'dsh:session-tool:turn:1')
  assert.equal(state.inspect('session-tool').openTurn, 1, 'a failed tool leaves the turn open')
  assert.equal(state.inspect('session-tool').category, 'working')
})

test('plan/mode reports planning only while plan mode is on', () => {
  const state = newState()
  state.observe({ sessionId: 'session-plan', type: 'turn/start', seq: 0, time: t0, data: { turn: 1 } })
  const on = state.observe({ sessionId: 'session-plan', type: 'plan/mode', seq: 1, time: t0, data: { active: true } })
  assert.equal(on.kind, 'plan/mode')
  assert.equal(on.category, 'planning')
  assert.equal(on.evidence, 'explicit')
  assert.equal(state.inspect('session-plan').planActive, true)
  // Plan mode is explicit state, not a sustained activity category: only the
  // plan/mode record that switched it on carries `planning`.
  assert.equal(state.inspect('session-plan').category, 'working')
  const duringPlan = state.observe({
    sessionId: 'session-plan',
    type: 'tool/call',
    seq: 2,
    time: t0,
    data: { turn: 1, step: 1, callId: 'call-plan', name: 'SECRET_TOOL_NAME', arguments: 'y' },
  })
  assert.equal(duringPlan.category, 'working', 'a tool running under plan mode is not planning')
  const beatDuringPlan = state.heartbeats().find((record) => record.sessionId === 'session-plan')
  assert.equal(beatDuringPlan.category, 'working', 'plan mode never becomes a sustained planning duration')

  const off = state.observe({ sessionId: 'session-plan', type: 'plan/mode', seq: 3, time: t0, data: { active: false } })
  assert.equal(off.category, 'unknown')
  assert.equal(off.evidence, 'explicit')
  assert.equal(state.inspect('session-plan').planActive, false)
  assert.equal(state.inspect('session-plan').category, 'working')
})

test('approval asked/decided correlate by request id and never export the tool name', () => {
  const state = newState()
  state.observe({ sessionId: 'session-approval', type: 'turn/start', seq: 0, time: t0, data: { turn: 1 } })
  const asked = state.observe({
    sessionId: 'session-approval',
    type: 'approval/asked',
    seq: 1,
    time: t0,
    data: { id: 'approval-7', toolName: 'SECRET_TOOL_NAME', callId: 'call-7', reason: 'SECRET_APPROVAL_REASON' },
  })
  assertShape(asked)
  assert.equal(asked.kind, 'approval/asked')
  assert.equal(asked.requestId, 'approval-7')
  assert.equal(asked.callId, 'call-7')
  assert.equal(asked.category, 'waiting')
  assert.equal(JSON.stringify(asked).includes('SECRET_TOOL_NAME'), false)
  assert.equal(JSON.stringify(asked).includes('SECRET_APPROVAL_REASON'), false)
  assert.equal(state.inspect('session-approval').pendingApprovalRequestId, 'approval-7')

  const decided = state.observe({
    sessionId: 'session-approval',
    type: 'approval/decided',
    seq: 2,
    time: t0,
    data: { id: 'approval-7', outcome: 'allowed-once' },
  })
  assert.equal(decided.requestId, 'approval-7')
  assert.equal(decided.outcome, 'ok')
  assert.equal(decided.category, 'working')
  assert.equal(state.inspect('session-approval').pendingApprovalRequestId, null)

  const rejected = newState().observe({
    sessionId: 'session-rejected',
    type: 'approval/decided',
    seq: 0,
    time: t0,
    data: { id: 'approval-8', outcome: 'rejected' },
  })
  assert.equal(rejected.outcome, 'failed')
  assert.equal(rejected.category, 'idle')
})

test('unmapped upstream events produce no record at all', () => {
  const state = newState()
  for (const type of ['user/message', 'assistant/message', 'system/message', 'step/start', 'request/header', 'tool/ptc-dispatch']) {
    assert.equal(
      state.observe({ sessionId: 'session-quiet', type, seq: 0, time: t0, data: { message: { content: 'SECRET_BODY_TEXT' } } }),
      null,
      `${type} is outside the exported vocabulary`,
    )
  }
  assert.equal(state.inspect('session-quiet').records, 0)
})

test('snapshot and heartbeat records are hydration-flagged exactly as specified', () => {
  const state = newState()
  state.sessionStart('session-h', { projectRoot: 'C:/demo' })
  const snapshots = state.snapshots()
  assert.equal(snapshots.length, 1)
  assertShape(snapshots[0])
  assert.equal(snapshots[0].kind, 'snapshot')
  assert.equal(snapshots[0].hydration, true)
  assert.equal(snapshots[0].category, 'idle')
  assert.equal(snapshots[0].evidence, 'inferred')

  const heartbeats = state.heartbeats()
  assert.equal(heartbeats.length, 1)
  assert.equal(heartbeats[0].kind, 'heartbeat')
  assert.equal(heartbeats[0].hydration, false)

  // No known session: the attach itself is the snapshot, scoped to the source.
  const empty = newState()
  const sourceSnapshot = empty.snapshots()
  assert.equal(sourceSnapshot.length, 1)
  assert.equal(sourceSnapshot[0].sessionId, null)
  assert.equal(sourceSnapshot[0].eventId, 'dsh:*:0')
  assert.equal(sourceSnapshot[0].hydration, true)
  const sourceHeartbeat = empty.heartbeats()
  assert.equal(sourceHeartbeat[0].kind, 'heartbeat')
  assert.equal(sourceHeartbeat[0].category, 'unknown')
})

test('the project root and ids are bounded, and unknown fields are never read', () => {
  const state = newState()
  const started = state.sessionStart('session-bounded', { projectRoot: 'C:/'.concat('p'.repeat(900)) })
  assert.equal(started.projectRoot.length, LIMITS.projectRootChars)

  const hostile = {}
  Object.defineProperty(hostile, 'turn', {
    get() {
      throw new Error('hostile getter')
    },
  })
  assert.throws(() => state.observe({ sessionId: 'session-hostile', type: 'turn/start', seq: 0, time: t0, data: hostile }))
})

test('current.json state carries fork lineage from the session header', () => {
  const state = newState()
  state.sessionStart('session-parent', { projectRoot: 'C:/demo', createdAtMs: t0 })
  state.sessionStart('session-child', { projectRoot: 'C:/demo', parentSessionId: 'session-parent', createdAtMs: t0 + 5 })
  const document = state.currentDocument()
  assert.equal(document.harnessId, 'dsh')
  assert.equal(document.sessions.length, 2)
  const child = document.sessions.find((entry) => entry.sessionId === 'session-child')
  assert.equal(child.parentSessionId, 'session-parent')
  const parent = document.sessions.find((entry) => entry.sessionId === 'session-parent')
  assert.equal(parent.parentSessionId, null)
})
