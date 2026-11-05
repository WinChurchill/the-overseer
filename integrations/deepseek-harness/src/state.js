/**
 * Per-session observation state and normalized record assembly.
 *
 * Upstream contracts read (pinned read-only reference checkout, commit
 * 46a7f68b0922371ce7144b668b90e377d8e799f4):
 * - packages/core/session/src/types.ts — `SessionEvent.seq` is the per-session
 *   monotonic number with the `seq = log.length` contiguity contract, so a
 *   received `seq` is `Session.seq - 1` at emission time; `Session.seq` is the
 *   next event's number.
 * - packages/core/session/src/index.ts — `session/created`, `session/event`,
 *   `session/disposed` are post-commit, fire-and-forget feeds whose listener
 *   failures are logged and contained. `Session.header` carries `id`,
 *   `createdAt`, `cwd`, and `parentSession`; `SessionStore.list()` returns the
 *   live sessions in creation order.
 *
 * Sequence rules (per source epoch, per session):
 * - `sequence` numbers PUBLISHED records contiguously: every exported record
 *   takes `lastSequence + 1`, whether it came from a host event or from the
 *   observer itself (session/start, session/end, heartbeat, snapshot). The host's
 *   `seq` is NOT the published sequence: most host events are never exported, so
 *   using it would leave holes, and a hole is precisely how the consumer detects
 *   a LOST record. A hole must mean a lost record, not "the host did something we
 *   deliberately do not export".
 * - `eventId` remains `dsh:<sessionId>:<sequence>` and stays identical when the
 *   same logical event is delivered again (replay/reconnect), because the host
 *   seq is remembered per session; that replay is exactly when `hydration` is
 *   set to true.
 * - A duplicate delivery repeats its original sequence (same `eventId`); that
 *   is the point of the duplicate rule, and distinct events never repeat one.
 *   `lastHostSeq` in `current.json` still reports the highest host seq seen.
 *
 * @module overseer-dsh-observer/state
 */

import {
  CATEGORIES,
  EVIDENCES,
  HARNESS_ID,
  KINDS,
  LIMITS,
  OUTCOMES,
  PRODUCER_VERSION,
  SCHEMA_VERSION,
  clampText,
  describeEvent,
  wireId,
} from './normalize.js'

/** Maximum number of session states kept in memory (least-recently-observed evicted first). */
export const MAX_TRACKED_SESSIONS = 512
/** Maximum number of ended turn numbers remembered per session (for the late-event rule). */
export const MAX_ENDED_TURNS = 64
/** Maximum number of host seq -> sequence mappings remembered per session. */
export const HOST_SEQ_MEMORY = 1024
/** Hard bound on concurrently answerable question call IDs per session. */
export const MAX_PENDING_INPUTS = 32
/** Key of the source-scoped pseudo session used by session-less heartbeats/snapshots. */
const SOURCE_SCOPE_KEY = '*'

/**
 * Build the stable event id of one logical event.
 * @param sessionId - session id, or null for a source-scoped record.
 * @param sequence - per-session (or per-source) sequence number.
 * @returns `dsh:<sessionId>:<sequence>`, with `*` for a null session id.
 */
export function eventIdFor(sessionId, sequence) {
  return wireId(`${HARNESS_ID}:${sessionId ?? SOURCE_SCOPE_KEY}:${sequence}`)
}

/**
 * Build the stable turn id of one turn.
 * @param sessionId - owning session id.
 * @param turnNumber - the upstream `turn` number.
 * @returns `dsh:<sessionId>:turn:<n>`, or null without a turn number.
 */
export function turnIdFor(sessionId, turnNumber) {
  if (sessionId === null || turnNumber === null || turnNumber === undefined) return null
  return wireId(`${HARNESS_ID}:${sessionId}:turn:${turnNumber}`)
}

/**
 * Create the observer's per-session state machine.
 *
 * @param options - `{ sourceInstanceId, sourceEpoch, producerVersion, now, maxSessions }`.
 * @returns the state API used by the plugin wiring and by tests.
 */
export function createObserverState(options = {}) {
  const now = typeof options.now === 'function' ? options.now : Date.now
  const sourceInstanceId = clampText(options.sourceInstanceId, LIMITS.sourceIdChars) ?? 'dsh-default'
  const sourceEpoch = clampText(options.sourceEpoch, LIMITS.sourceEpochChars) ?? 'epoch-unknown'
  const producerVersion = clampText(options.producerVersion, LIMITS.idChars) ?? PRODUCER_VERSION
  const maxSessions = typeof options.maxSessions === 'number' && options.maxSessions > 0
    ? Math.trunc(options.maxSessions)
    : MAX_TRACKED_SESSIONS

  /** @type {Map<string, object>} */
  const sessions = new Map()
  let evictions = 0

  function newTrack(sessionId) {
    return {
      sessionId,
      projectRoot: null,
      parentSessionId: null,
      createdAtMs: null,
      firstSeenMs: now(),
      lastObservedMs: now(),
      lastSequence: -1,
      lastHostSeq: -1,
      hostSeqToSequence: new Map(),
      openTurn: null,
      endedTurns: new Set(),
      lastEndedTurn: -1,
      planActive: false,
      pendingApprovalRequestId: null,
      pendingInputs: new Map(),
      inputStateComplete: false,
      questionHostSeq: -1,
      legacyBaselineComplete: true,
      lastKind: null,
      lastEventId: null,
      lastCategory: 'idle',
      hydration: false,
      disposed: false,
      records: 0,
    }
  }

  function sourceTrack() {
    let track = sessions.get(SOURCE_SCOPE_KEY)
    if (track === undefined) {
      track = newTrack(null)
      sessions.set(SOURCE_SCOPE_KEY, track)
    }
    return track
  }

  function evictIfNeeded() {
    while (sessions.size > maxSessions) {
      let oldestKey
      let oldestMs = Infinity
      for (const [key, track] of sessions) {
        if (track.sessionId === null) continue
        if (track.lastObservedMs < oldestMs) {
          oldestMs = track.lastObservedMs
          oldestKey = key
        }
      }
      if (oldestKey === undefined) return
      sessions.delete(oldestKey)
      evictions++
    }
  }

  /**
   * Get or create the state of one session.
   * @param sessionId - session id string.
   * @returns the session track.
   */
  function trackFor(sessionId) {
    const key = sessionId === null || sessionId === undefined ? SOURCE_SCOPE_KEY : String(sessionId)
    let track = sessions.get(key)
    if (track === undefined) {
      track = newTrack(key === SOURCE_SCOPE_KEY ? null : key)
      sessions.set(key, track)
      evictIfNeeded()
    }
    return track
  }

  /**
   * Tracked activity category: no open turn is idle, an unanswered approval is
   * waiting, otherwise working. Plan mode is deliberately NOT folded in here:
   * `planning` appears only on an explicit `plan/mode` record while plan mode is
   * on, so it can never become a sustained planning duration (its state travels
   * in `current.json` as the explicit `planMode` flag instead).
   */
  function currentCategory(track) {
    if (track.disposed) return 'idle'
    if (track.pendingApprovalRequestId !== null || track.pendingInputs.size > 0) return 'waiting'
    if (track.openTurn === null) return 'idle'
    return 'working'
  }

  function nextSynthetic(track) {
    track.lastSequence += 1
    return track.lastSequence
  }

  function allocateForHostEvent(track, hostSeq) {
    if (hostSeq === null) return { sequence: nextSynthetic(track), hydration: false }
    const known = track.hostSeqToSequence.get(hostSeq)
    if (known !== undefined) {
      // Duplicate delivery of the same logical event: same eventId, hydration set.
      return { sequence: known, hydration: true }
    }
    // The published sequence numbers PUBLISHED records contiguously. Using the
    // host's seq would leave holes wherever the host event was not exported, and a
    // hole is how the consumer detects a lost record: it must mean exactly that.
    const sequence = track.lastSequence + 1
    track.hostSeqToSequence.set(hostSeq, sequence)
    if (track.hostSeqToSequence.size > HOST_SEQ_MEMORY) {
      const oldest = track.hostSeqToSequence.keys().next().value
      if (oldest !== undefined) track.hostSeqToSequence.delete(oldest)
    }
    if (hostSeq > track.lastHostSeq) track.lastHostSeq = hostSeq
    track.lastSequence = sequence
    return { sequence, hydration: false }
  }

  function isoTimestamp(ms) {
    if (typeof ms !== 'number' || !Number.isFinite(ms)) return null
    try {
      return new Date(ms).toISOString()
    } catch {
      // Timestamp outside the Date range: publish no upstream time rather than a wrong one.
      return null
    }
  }

  function buildRecord(track, fields) {
    if (!KINDS.has(fields.kind)) return null
    const sessionId = track.sessionId
    const sequence = fields.sequence
    const category = CATEGORIES.has(fields.category) ? fields.category : 'unknown'
    const evidence = EVIDENCES.has(fields.evidence) ? fields.evidence : 'unknown'
    const outcome = OUTCOMES.has(fields.outcome) ? fields.outcome : null
    const record = {
      schemaVersion: SCHEMA_VERSION,
      harnessId: HARNESS_ID,
      sourceInstanceId,
      sourceEpoch,
      sessionId: wireId(sessionId),
      sequence,
      eventId: eventIdFor(sessionId, sequence),
      observedAtUtc: isoTimestamp(now()),
      eventAtUtc: isoTimestamp(fields.eventAtMs ?? null),
      kind: fields.kind,
      turnId: fields.turnNumber === null || fields.turnNumber === undefined
        ? null
        : turnIdFor(sessionId, fields.turnNumber),
      // Event normalization/native metadata canonicalize opaque call IDs once.
      callId: fields.callId ?? null,
      requestId: fields.requestId ?? null,
      projectRoot: track.projectRoot,
      category,
      evidence,
      outcome,
      hydration: fields.hydration === true,
      producerVersion,
    }
    track.lastKind = record.kind
    track.lastEventId = record.eventId
    track.lastCategory = category
    track.records += 1
    return record
  }

  /**
   * Membership of a turn number relative to the observed turn lifecycle.
   * A turn that already ended never becomes the open turn again.
   */
  function membership(track, turnNumber) {
    if (turnNumber === null) return 'unknown'
    if (track.endedTurns.has(turnNumber) || turnNumber <= track.lastEndedTurn) return 'closed'
    if (track.openTurn === turnNumber) return 'open'
    if (track.openTurn === null) return 'adopt'
    return turnNumber > track.openTurn ? 'adopt' : 'stale'
  }

  function closeTurn(track, turnNumber) {
    if (turnNumber === null) return
    track.lastEndedTurn = Math.max(track.lastEndedTurn, turnNumber)
    track.endedTurns.add(turnNumber)
    while (track.endedTurns.size > MAX_ENDED_TURNS) {
      const oldest = track.endedTurns.values().next().value
      if (oldest === undefined) break
      track.endedTurns.delete(oldest)
    }
    if (track.openTurn === turnNumber) track.openTurn = null
  }

  /**
   * Publish a `session/start` record for a session the observer saw created.
   * @param sessionId - session id string.
   * @param meta - `{ projectRoot, parentSessionId, createdAtMs }` read from the session header only.
   * @returns the normalized record.
   */
  function sessionStart(sessionId, meta = {}) {
    const track = trackFor(sessionId)
    if (meta.projectRoot !== undefined) track.projectRoot = clampText(meta.projectRoot, LIMITS.projectRootChars)
    if (meta.parentSessionId !== undefined) track.parentSessionId = wireId(meta.parentSessionId)
    if (meta.createdAtMs !== undefined) track.createdAtMs = meta.createdAtMs
    track.disposed = false
    track.lastObservedMs = now()
    return buildRecord(track, {
      kind: 'session/start',
      sequence: nextSynthetic(track),
      eventAtMs: track.createdAtMs,
      turnNumber: track.openTurn,
      callId: null,
      requestId: null,
      category: 'unknown',
      evidence: 'explicit',
      outcome: null,
      hydration: false,
    })
  }

  /**
   * Publish a `session/end` record. A session disposed with an open turn is a
   * silent cancellation (`aborted`); otherwise the end carries `ok`.
   * @param sessionId - session id string.
   * @returns the normalized record.
   */
  function sessionEnd(sessionId) {
    const track = trackFor(sessionId)
    const openTurn = track.openTurn
    const outcome = openTurn === null ? 'ok' : 'aborted'
    if (openTurn !== null) closeTurn(track, openTurn)
    const record = buildRecord(track, {
      kind: 'session/end',
      sequence: nextSynthetic(track),
      eventAtMs: null,
      turnNumber: null,
      callId: null,
      requestId: null,
      category: 'unknown',
      evidence: 'explicit',
      outcome,
      hydration: false,
    })
    track.disposed = true
    track.pendingApprovalRequestId = null
    track.pendingInputs.clear()
    track.inputStateComplete = true
    return record
  }

  /**
   * Fold bounded question metadata, never question/answer content. Native cached
   * active IDs are authoritative for timed calls, including continued waits.
   * Blocking calls settle on their own result/end. Missing native metadata keeps
   * known attention and marks current coverage incomplete instead of guessing.
   */
  function reconcileQuestions(sessionId, metadata, event = null, hydration = false) {
    const track = trackFor(sessionId)
    if (track.disposed) return []
    const before = new Set(track.pendingInputs.keys())
    const valid = metadata !== null && metadata !== undefined
    track.inputStateComplete = valid && (metadata.timed || track.legacyBaselineComplete)
    const fresh = event !== null && Number.isSafeInteger(event.seq) && event.seq > track.questionHostSeq
    if (fresh) {
      track.questionHostSeq = event.seq
      if (event.type === 'turn/start' && membership(track, event.turn) === 'open') track.legacyBaselineComplete = true
      const turnState = membership(track, event.turn)
      if (event.type === 'tool/call' && event.isQuestion && event.callId !== null
        && turnState !== 'closed' && turnState !== 'stale') {
        track.pendingInputs.set(event.callId, {
          mode: valid ? (metadata.timed ? 'timed' : 'legacy') : 'unknown',
          turn: event.turn, finished: false,
        })
      } else if (event.type === 'tool/result') {
        const pending = track.pendingInputs.get(event.callId)
        if (pending?.mode === 'legacy') track.pendingInputs.delete(event.callId)
        else if (pending !== undefined) pending.finished = true
      } else if (event.type === 'turn/end') {
        for (const [id, pending] of track.pendingInputs) {
          if (pending.mode === 'legacy' && pending.turn === event.turn) track.pendingInputs.delete(id)
        }
      }
    }
    if (valid) {
      const active = new Set(metadata.activeIds)
      for (const [id, pending] of track.pendingInputs) {
        if (pending.mode === 'unknown' && !metadata.timed && !active.has(id)) {
          pending.mode = 'legacy'
          if (pending.finished || track.endedTurns.has(pending.turn)) track.pendingInputs.delete(id)
        } else if ((pending.mode === 'timed' || pending.mode === 'unknown') && !active.has(id)) {
          track.pendingInputs.delete(id)
        }
      }
      for (const id of active) {
        track.pendingInputs.set(id, { mode: 'timed', turn: track.openTurn, finished: false })
      }
    }
    if (track.pendingInputs.size > MAX_PENDING_INPUTS) {
      // Do not publish a truncated set as authoritative. Retain the bounded known
      // set, disclose missing coverage, and let a later valid cache recover it.
      track.inputStateComplete = false
      track.legacyBaselineComplete = false
      for (const id of track.pendingInputs.keys()) {
        if (track.pendingInputs.size <= MAX_PENDING_INPUTS) break
        if (!before.has(id)) track.pendingInputs.delete(id)
      }
    }
    const records = []
    for (const id of before) {
      if (!track.pendingInputs.has(id)) records.push(questionRecord(track, 'input/answered', id, hydration))
    }
    for (const id of track.pendingInputs.keys()) {
      if (!before.has(id)) records.push(questionRecord(track, 'input/asked', id, hydration))
    }
    return records
  }

  function questionRecord(track, kind, id, hydration) {
    return buildRecord(track, {
      kind, sequence: nextSynthetic(track), eventAtMs: null,
      turnNumber: track.openTurn, callId: id, requestId: id,
      category: currentCategory(track), evidence: 'explicit',
      outcome: kind === 'input/answered' ? 'ok' : null, hydration,
    })
  }

  function noteQuestionGap(sessionId) {
    const track = trackFor(sessionId)
    track.legacyBaselineComplete = false
    track.inputStateComplete = false
  }

  /**
   * Observe one `session/event` payload and publish a record when the event
   * type is in the exported vocabulary.
   *
   * @param input - `{ sessionId, type, seq, time, data }`.
   * @returns the normalized record, or null when nothing is exported.
   */
  function observe(input) {
    const track = trackFor(input.sessionId)
    track.lastObservedMs = now()
    const descriptor = describeEvent({ type: input.type, data: input.data })
    if (descriptor === null) return null
    const hostSeq = typeof input.seq === 'number' && Number.isSafeInteger(input.seq) && input.seq >= 0
      ? input.seq
      : null
    const { sequence, hydration } = allocateForHostEvent(track, hostSeq)
    const membershipState = membership(track, descriptor.turnNumber)
    let category = descriptor.category
    let evidence = descriptor.evidence
    let effectiveTurn = descriptor.turnNumber
    let hydrationFlag = hydration

    switch (descriptor.kind) {
      case 'turn/start':
        if (membershipState === 'closed' || membershipState === 'stale') {
          // Replayed turn/start for a turn that already ended: current state only.
          category = 'unknown'
          evidence = 'inferred'
        } else if (membershipState === 'open') {
          // Duplicate delivery of the live turn/start: same eventId, hydration set.
          hydrationFlag = true
        } else {
          track.openTurn = descriptor.turnNumber
        }
        break
      case 'turn/end':
        closeTurn(track, descriptor.turnNumber)
        track.pendingApprovalRequestId = null
        break
      case 'tool/start':
      case 'tool/end':
        if (membershipState === 'closed' || membershipState === 'stale') {
          // A late tool event never reopens a closed turn: the record is still
          // published (history is real), but it reports no active category and
          // the tracked turn/category stay exactly as they were.
          category = 'unknown'
          evidence = 'inferred'
          effectiveTurn = descriptor.turnNumber
        } else if (membershipState === 'adopt') {
          // Attached mid-turn: adopt the observed turn without inventing turn/start.
          track.openTurn = descriptor.turnNumber
        }
        break
      case 'approval/asked':
        track.pendingApprovalRequestId = descriptor.requestId
        break
      case 'approval/decided':
        if (track.pendingApprovalRequestId === descriptor.requestId) track.pendingApprovalRequestId = null
        break
      case 'plan/mode':
        track.planActive = category === 'planning'
        break
      default:
        break
    }

    // Plan mode is reported only by the `plan/mode` record that switched it on
    // (`category: planning`, `evidence: explicit`) and is never smeared across
    // other records as a sustained planning category.
    return buildRecord(track, {
      kind: descriptor.kind,
      sequence,
      eventAtMs: input.time,
      turnNumber: effectiveTurn,
      callId: descriptor.callId,
      requestId: descriptor.requestId,
      category,
      evidence,
      outcome: descriptor.outcome,
      hydration: hydrationFlag,
    })
  }

  /**
   * Seed or refresh a session track from a live `Session` read at attach time.
   * @param meta - `{ sessionId, projectRoot, parentSessionId, createdAtMs, openTurnNumber, planActive, lastHostSeq }`.
   * @returns nothing.
   */
  function seedSession(meta) {
    const track = trackFor(meta.sessionId)
    if (meta.projectRoot !== undefined) track.projectRoot = clampText(meta.projectRoot, LIMITS.projectRootChars)
    if (meta.parentSessionId !== undefined) track.parentSessionId = wireId(meta.parentSessionId)
    if (meta.createdAtMs !== undefined) track.createdAtMs = meta.createdAtMs
    if (meta.openTurnNumber !== undefined) track.openTurn = meta.openTurnNumber
    if (meta.closedTurnNumber !== null && meta.closedTurnNumber !== undefined) closeTurn(track, meta.closedTurnNumber)
    if (meta.planActive !== undefined) track.planActive = meta.planActive === true
    if (track.records === 0 && meta.questionScanComplete !== undefined) {
      track.legacyBaselineComplete = meta.questionScanComplete
      for (const id of meta.blockingInputIds ?? []) {
        track.pendingInputs.set(id, { mode: 'unknown', turn: track.openTurn, finished: false })
      }
    }
    if (typeof meta.lastHostSeq === 'number' && meta.lastHostSeq > track.lastHostSeq && track.records === 0
      && track.hostSeqToSequence.size === 0) {
      track.lastHostSeq = meta.lastHostSeq
      track.lastSequence = Math.max(track.lastSequence, meta.lastHostSeq)
    }
  }

  function liveTracks() {
    const live = []
    for (const track of sessions.values()) {
      if (track.sessionId === null || track.disposed) continue
      live.push(track)
    }
    return live
  }

  /**
   * Publish one `heartbeat` per tracked live session, or one source-scoped
   * heartbeat when no session is known. Heartbeats prove the observer is
   * attached; they never invent activity, they report the tracked category.
   * @returns the normalized records.
   */
  function heartbeats() {
    const live = liveTracks()
    if (live.length === 0) {
      const track = sourceTrack()
      const record = buildRecord(track, {
        kind: 'heartbeat',
        sequence: nextSynthetic(track),
        eventAtMs: null,
        turnNumber: null,
        callId: null,
        requestId: null,
        category: 'unknown',
        evidence: 'inferred',
        outcome: null,
        hydration: false,
      })
      return record === null ? [] : [record]
    }
    const records = []
    for (const track of live) {
      const record = buildRecord(track, {
        kind: 'heartbeat',
        sequence: nextSynthetic(track),
        eventAtMs: null,
        turnNumber: track.openTurn,
        callId: null,
        requestId: null,
        category: currentCategory(track),
        evidence: 'inferred',
        outcome: null,
        hydration: false,
      })
      if (record !== null) records.push(record)
    }
    return records
  }

  /**
   * Publish one `snapshot` record per tracked live session, flagged
   * `hydration: true` so OVERSEER suppresses voice for replayed state. With no
   * known session, one source-scoped snapshot states the attach itself.
   * @returns the normalized records.
   */
  function snapshots() {
    const live = liveTracks()
    if (live.length === 0) {
      const track = sourceTrack()
      const record = buildRecord(track, {
        kind: 'snapshot',
        sequence: nextSynthetic(track),
        eventAtMs: null,
        turnNumber: null,
        callId: null,
        requestId: null,
        category: 'unknown',
        evidence: 'inferred',
        outcome: null,
        hydration: true,
      })
      return record === null ? [] : [record]
    }
    const records = []
    for (const track of live) {
      const record = buildRecord(track, {
        kind: 'snapshot',
        sequence: nextSynthetic(track),
        eventAtMs: track.createdAtMs,
        turnNumber: track.openTurn,
        callId: null,
        requestId: null,
        category: currentCategory(track),
        evidence: 'inferred',
        outcome: null,
        hydration: true,
      })
      if (record !== null) records.push(record)
    }
    return records
  }

  /**
   * The atomically replaced `current.json` document: the current known session
   * state, including fork lineage read from the session header.
   * @returns the current-state document.
   */
  function currentDocument() {
    const entries = []
    for (const track of sessions.values()) {
      if (track.sessionId === null) continue
      entries.push({
        sessionId: wireId(track.sessionId),
        parentSessionId: track.parentSessionId,
        projectRoot: track.projectRoot,
        createdAtUtc: isoTimestamp(track.createdAtMs),
        firstSeenUtc: isoTimestamp(track.firstSeenMs),
        lastObservedUtc: isoTimestamp(track.lastObservedMs),
        openTurnId: track.openTurn === null ? null : turnIdFor(track.sessionId, track.openTurn),
        planMode: track.planActive === true,
        pendingApprovalRequestId: track.pendingApprovalRequestId,
        pendingInputRequestIds: [...track.pendingInputs.keys()],
        inputStateComplete: track.inputStateComplete,
        category: currentCategory(track),
        disposed: track.disposed,
        lastSequence: track.lastSequence,
        lastHostSeq: track.lastHostSeq,
        lastKind: track.lastKind,
        lastEventId: track.lastEventId,
        records: track.records,
      })
    }
    return {
      schemaVersion: SCHEMA_VERSION,
      harnessId: HARNESS_ID,
      sourceInstanceId,
      sourceEpoch,
      producerVersion,
      updatedAtUtc: isoTimestamp(now()),
      evictedSessions: evictions,
      sessions: entries,
    }
  }

  /**
   * Read-only view of one tracked session, for tests and diagnostics.
   * @param sessionId - session id string.
   * @returns a detached copy of the tracked state, or null.
   */
  function inspect(sessionId) {
    const track = sessions.get(String(sessionId))
    if (track === undefined) return null
    return {
      sessionId: track.sessionId,
      openTurn: track.openTurn,
      endedTurns: [...track.endedTurns],
      planActive: track.planActive,
      pendingApprovalRequestId: track.pendingApprovalRequestId,
      pendingInputRequestIds: [...track.pendingInputs.keys()],
      inputStateComplete: track.inputStateComplete,
      category: currentCategory(track),
      lastSequence: track.lastSequence,
      lastHostSeq: track.lastHostSeq,
      lastKind: track.lastKind,
      lastEventId: track.lastEventId,
      projectRoot: track.projectRoot,
      parentSessionId: track.parentSessionId,
      disposed: track.disposed,
      records: track.records,
    }
  }

  return {
    sessionStart,
    sessionEnd,
    observe,
    reconcileQuestions,
    noteQuestionGap,
    seedSession,
    heartbeats,
    snapshots,
    currentDocument,
    inspect,
    trackFor,
    currentCategory,
    sourceInstanceId,
    sourceEpoch,
    producerVersion,
  }
}
