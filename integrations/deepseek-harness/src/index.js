/**
 * OVERSEER observer plugin for the official DeepSeek Harness (`dsh`).
 *
 * This module is the whole plugin: `dsh` loads it as a Cordis function plugin
 * (named `name`/`apply` exports, no default export — a default export would make
 * the loader's `unwrapExports` collapse the namespace; see
 * postmortem 0001-acp-default-export-drops-inject in the pinned reference).
 *
 * Seams observed, never intercepted:
 * - `session/created` — "A synchronous throw vetoes and rolls back with a paired
 *   disposal" (packages/core/session/src/index.ts), so this listener is wrapped
 *   and can never throw into the host.
 * - `session/event` — "Post-commit, fire-and-forget append feed ... observer
 *   failures are logged and contained without making the committed append fail".
 * - `session/disposed` — "Emitted once when an announced session leaves the store".
 *
 * NOT subscribed: `session/flush` (an awaited parallel durability checkpoint —
 * subscribing would put this adapter in the host's durability path), and every
 * waterfall (`approval/request`, question answerers). The observer returns no
 * execution decision and influences no approval or input chain.
 *
 * Every callback is synchronous, holds no handle, awaits nothing, and touches
 * no disk: normalized records enter a bounded in-process queue and a writer tick
 * drains them.
 *
 * @module overseer-dsh-observer
 */

import { randomBytes } from 'node:crypto'
import { homedir } from 'node:os'
import { join } from 'node:path'
import { LIMITS, PRODUCER_VERSION } from './normalize.js'
import { createObserverState, MAX_PENDING_INPUTS, MAX_TRACKED_SESSIONS } from './state.js'
import {
  DEFAULT_HEARTBEAT_MS,
  DEFAULT_QUEUE_LIMIT,
  HEARTBEAT_MS_MAX,
  HEARTBEAT_MS_MIN,
  QUEUE_LIMIT_MAX,
  QUEUE_LIMIT_MIN,
  clampNumber,
  createSpool,
} from './spool.js'

/** Cordis plugin name. */
export const name = 'overseer-dsh-observer'

/** Environment switch that disables the observer entirely. */
export const DISABLED_ENV = 'OVERSEER_DSH_DISABLED'
/** Environment knob: spool root directory. */
export const SPOOL_ENV = 'OVERSEER_DSH_SPOOL'
/** Environment knob: source instance id (a single path-safe segment). */
export const SOURCE_ID_ENV = 'OVERSEER_DSH_SOURCE_ID'
/** Environment knob: bounded in-process queue length. */
export const QUEUE_ENV = 'OVERSEER_DSH_QUEUE'
/** Environment knob: heartbeat period in seconds (OVERSEER extension, clamped 5..300). */
export const HEARTBEAT_ENV = 'OVERSEER_DSH_HEARTBEAT_SECONDS'

/** Default source instance id. */
export const DEFAULT_SOURCE_ID = 'dsh-default'
/** Maximum live sessions described by one attach snapshot. */
export const MAX_SNAPSHOT_SESSIONS = 256
/** Maximum trailing events read per session at attach to recover turn state. */
export const SNAPSHOT_SCAN_EVENTS = 256
/** Deferred second attach snapshot, for a sessions service that mounts after us. */
export const SNAPSHOT_RETRY_MS = 2_000

/**
 * Default spool root: `%LOCALAPPDATA%/OVERSEER/bridges` on Windows, else
 * `$XDG_STATE_HOME/overseer/bridges` (falling back to `~/.local/state`).
 * @param env - environment to read (defaults to `process.env`).
 * @returns the absolute-ish default root.
 */
export function defaultSpoolRoot(env = process.env) {
  if (process.platform === 'win32') {
    const base = typeof env.LOCALAPPDATA === 'string' && env.LOCALAPPDATA !== ''
      ? env.LOCALAPPDATA
      : join(homedir(), 'AppData', 'Local')
    return join(base, 'OVERSEER', 'bridges')
  }
  const base = typeof env.XDG_STATE_HOME === 'string' && env.XDG_STATE_HOME !== ''
    ? env.XDG_STATE_HOME
    : join(homedir(), '.local', 'state')
  return join(base, 'overseer', 'bridges')
}

/**
 * Restrict the source instance id to one path-safe directory segment.
 * @param value - configured value.
 * @returns a non-empty `[A-Za-z0-9._-]` segment, at most 128 characters.
 */
export function sanitizeSourceId(value) {
  const raw = typeof value === 'string' && value.trim() !== '' ? value.trim() : DEFAULT_SOURCE_ID
  const safe = raw.replace(/[^A-Za-z0-9._-]/g, '_').slice(0, LIMITS.sourceIdChars)
  return safe === '' ? DEFAULT_SOURCE_ID : safe
}

/**
 * Whether the observer is switched off by the environment.
 * @param env - environment to read.
 * @returns true when `OVERSEER_DSH_DISABLED` is `1` (or `true`).
 */
export function isDisabled(env = process.env) {
  const value = env[DISABLED_ENV]
  return value === '1' || value === 'true'
}

/**
 * Resolve effective observer configuration: explicit overrides, then environment
 * knobs, then defaults, with hard clamps.
 * @param env - environment to read.
 * @param overrides - explicit values (plugin config or tests) that win over the environment.
 * @returns the effective configuration.
 */
export function resolveConfig(env = process.env, overrides = {}) {
  const heartbeatOverrideSeconds = typeof overrides.heartbeatMs === 'number'
    ? overrides.heartbeatMs / 1000
    : undefined
  return {
    disabled: overrides.disabled ?? isDisabled(env),
    spoolRoot: String(overrides.spoolRoot ?? env[SPOOL_ENV] ?? defaultSpoolRoot(env)),
    sourceInstanceId: sanitizeSourceId(overrides.sourceInstanceId ?? env[SOURCE_ID_ENV]),
    queueLimit: clampNumber(
      overrides.queueLimit ?? env[QUEUE_ENV],
      QUEUE_LIMIT_MIN,
      QUEUE_LIMIT_MAX,
      DEFAULT_QUEUE_LIMIT,
    ),
    heartbeatMs: clampNumber(
      heartbeatOverrideSeconds ?? overrides.heartbeatSeconds ?? env[HEARTBEAT_ENV],
      HEARTBEAT_MS_MIN / 1000,
      HEARTBEAT_MS_MAX / 1000,
      DEFAULT_HEARTBEAT_MS / 1000,
    ) * 1000,
  }
}

/** @returns the session id of a live session, or null. */
function readSessionId(session) {
  const id = session?.id
  return typeof id === 'string' && id !== '' ? id : null
}

function questionId(value) {
  return typeof value === 'string' && value.trim() === value && value.length > 0
    && value.length <= LIMITS.idChars ? value : null
}

/**
 * DSH Desktop 0.2.0-rc.2's eager projection cache. Read only an already-current
 * cell: stateOf() would materialize unrelated units and potentially full history.
 * The guarded internal cache seam is version-specific; absence is incomplete
 * coverage. No native fold, wire schema, waterfall or private content is opened.
 */
function readQuestionMetadata(ctx, session) {
  const registry = typeof ctx?.get === 'function' ? ctx.get('sessionProjections') : undefined
  const registration = registry?.registrations?.get?.('userQuestions')
  if (registration?.def?.stateVersion !== 2) return null
  const cell = registration?.cells?.get?.(session)
  if (!Number.isSafeInteger(session?.seq) || cell?.observedSeq !== session.seq - 1) return null
  const native = cell.state
  const active = native?.questions?.active
  if (typeof native?.timed !== 'boolean' || !Array.isArray(active) || active.length > MAX_PENDING_INPUTS) return null
  const activeIds = []
  for (const item of active) {
    const id = questionId(item?.callId)
    if (id === null || !['open', 'continued'].includes(item?.state) || activeIds.includes(id)) return null
    activeIds.push(id)
  }
  return { timed: native.timed, activeIds }
}

/** Exact-name classification only; no tool arguments, result content or answers. */
function questionEvent(event) {
  const turn = event?.data?.turn
  return {
    type: event?.type, seq: event?.seq,
    turn: Number.isSafeInteger(turn) && turn >= 0 ? turn : null,
    isQuestion: event?.type === 'tool/call' && event?.data?.name === 'ask_user_question',
    callId: event?.type === 'tool/call' ? questionId(event?.data?.callId)
      : event?.type === 'tool/result' ? questionId(event?.data?.message?.toolCallId) : null,
  }
}

/** @returns header-derived metadata: cwd, parent session, creation time, and nothing else. */
function readHeader(session) {
  const meta = {}
  const header = session?.header
  if (typeof header !== 'object' || header === null) return meta
  if (typeof header.cwd === 'string') meta.projectRoot = header.cwd
  if (typeof header.parentSession === 'string') meta.parentSessionId = header.parentSession
  if (typeof header.createdAt === 'number' && Number.isFinite(header.createdAt)) meta.createdAtMs = header.createdAt
  return meta
}

/**
 * Recover the current turn and plan flags of a live session from the trailing
 * metadata of its log. Reads event `type` plus `turn`/`active` scalars only —
 * never a message body, tool argument, or tool result.
 * @param session - a live upstream Session.
 * @returns `{ openTurnNumber, planActive, lastHostSeq }`.
 */
function scanLiveTurn(session) {
  const found = { openTurnNumber: null, closedTurnNumber: null, planActive: false, lastHostSeq: undefined, blockingInputIds: [], questionScanComplete: false }
  const end = typeof session?.seq === 'number' && Number.isSafeInteger(session.seq) && session.seq >= 0
    ? session.seq
    : null
  if (end === null) return found
  if (end === 0) { found.questionScanComplete = true; return found }
  found.lastHostSeq = end - 1
  const readAt = typeof session.eventAt === 'function' ? session.eventAt.bind(session) : null
  if (readAt === null) return found
  let sawTurn = false
  let sawPlan = false
  const results = new Set()
  const calls = []
  const from = Math.max(0, end - SNAPSHOT_SCAN_EVENTS)
  for (let index = end - 1; index >= from; index -= 1) {
    const event = readAt(index)
    const type = event?.type
    if (!sawTurn && type === 'tool/result') {
      const id = questionId(event?.data?.message?.toolCallId)
      if (id !== null) results.add(id)
    } else if (!sawTurn && type === 'tool/call' && event?.data?.name === 'ask_user_question') {
      const id = questionId(event?.data?.callId)
      if (id !== null && !results.has(id)) calls.push(id)
    }
    if (!sawTurn && (type === 'turn/start' || type === 'turn/end')) {
      sawTurn = true
      if (type === 'turn/start') {
        const turn = event?.data?.turn
        if (typeof turn === 'number' && Number.isSafeInteger(turn) && turn >= 0) found.openTurnNumber = turn
      } else {
        const turn = event?.data?.turn
        if (Number.isSafeInteger(turn) && turn >= 0) found.closedTurnNumber = turn
      }
    } else if (!sawPlan && type === 'plan/mode') {
      sawPlan = true
      found.planActive = event?.data?.active === true
    }
  }
  found.questionScanComplete = sawTurn || from === 0
  if (calls.length > MAX_PENDING_INPUTS) found.questionScanComplete = false
  else if (found.openTurnNumber !== null) found.blockingInputIds = calls
  return found
}

/**
 * Create one observer instance without attaching it to a host context.
 *
 * @param overrides - configuration overrides; `{ spoolRoot, sourceInstanceId, queueLimit,
 *   heartbeatMs, disabled, fs, now, sourceEpoch, producerVersion }`.
 * @returns the observer API.
 */
export function createObserver(overrides = {}) {
  const config = resolveConfig(process.env, overrides)
  const now = typeof overrides.now === 'function' ? overrides.now : Date.now
  const sourceEpoch = String(
    overrides.sourceEpoch
    ?? `dsh-${process.pid}-${Date.now().toString(36)}-${randomBytes(4).toString('hex')}`,
  ).slice(0, LIMITS.sourceEpochChars)
  const state = createObserverState({
    sourceInstanceId: config.sourceInstanceId,
    sourceEpoch,
    producerVersion: overrides.producerVersion ?? PRODUCER_VERSION,
    now,
  })
  const spool = createSpool({
    spoolRoot: config.spoolRoot,
    sourceInstanceId: config.sourceInstanceId,
    sourceEpoch,
    queueLimit: config.queueLimit,
    fs: overrides.fs,
    now,
    currentMinIntervalMs: overrides.currentMinIntervalMs,
    cleanupIntervalMs: overrides.cleanupIntervalMs,
  })

  let internalErrors = 0
  let lastInternalError = null
  let attached = false
  let disposed = false
  let snapshotSeeded = 0
  let heartbeatTimer = null
  let snapshotRetryTimer = null
  let hostContext = null
  const refreshSessions = new Map()
  let refreshScheduled = false
  const seams = []

  function noteInternalError(error) {
    internalErrors += 1
    lastInternalError = {
      atUtc: new Date(now()).toISOString(),
      message: error instanceof Error ? error.message : String(error),
    }
  }

  /** Run one observer step; a failure is counted, never rethrown into the host. */
  function safely(step) {
    try {
      return step()
    } catch (error) {
      noteInternalError(error)
      return undefined
    }
  }

  function emit(record) {
    if (record === null || record === undefined) return
    spool.enqueue(record)
    spool.setCurrent(state.currentDocument())
  }

  function emitAll(records) {
    for (const record of records) emit(record)
  }

  function refreshQuestions(session, hydration = false, events = [], overflow = false) {
    const sessionId = readSessionId(session)
    if (sessionId === null) return
    let metadata = null
    try { metadata = readQuestionMetadata(hostContext, session) } catch {
      // Never retain host exception messages, which could contain private text.
      noteInternalError(new Error('question metadata unavailable'))
    }
    for (const event of events) emitAll(state.reconcileQuestions(sessionId, metadata, event, hydration))
    if (overflow) state.noteQuestionGap(sessionId)
    emitAll(state.reconcileQuestions(sessionId, metadata, null, hydration))
    spool.setCurrent(state.currentDocument())
  }

  function deferQuestionRefresh(session, event = null) {
    const id = readSessionId(session)
    if (id === null) return
    if (refreshSessions.size >= MAX_TRACKED_SESSIONS && !refreshSessions.has(id)) return
    const pending = refreshSessions.get(id) ?? { session, events: [] }
    if (event !== null && ['turn/start', 'turn/end', 'tool/call', 'tool/result'].includes(event.type)) {
      if (pending.events.length < MAX_PENDING_INPUTS * 4) pending.events.push(event)
      else pending.overflow = true
    }
    refreshSessions.set(id, pending)
    if (refreshScheduled) return
    refreshScheduled = true
    queueMicrotask(() => {
      refreshScheduled = false
      if (!disposed) for (const pending of refreshSessions.values()) safely(() => refreshQuestions(pending.session, false, pending.events, pending.overflow))
      refreshSessions.clear()
    })
  }

  function onCreated(session) {
    safely(() => {
      const sessionId = readSessionId(session)
      if (sessionId === null) return
      emit(state.sessionStart(sessionId, readHeader(session)))
      deferQuestionRefresh(session)
    })
  }

  function onEvent(session, event) {
    safely(() => {
      const sessionId = readSessionId(session)
      if (sessionId === null) return
      emit(state.observe({
        sessionId,
        type: event?.type,
        seq: event?.seq,
        time: event?.time,
        data: event?.data,
      }))
      // Allow the native eager drive to run regardless of listener order.
      deferQuestionRefresh(session, questionEvent(event))
    })
  }

  function onDisposed(session) {
    safely(() => {
      const sessionId = readSessionId(session)
      if (sessionId === null) return
      emit(state.sessionEnd(sessionId))
      refreshSessions.delete(sessionId)
    })
  }

  /** @returns the live sessions described by the `sessions` service, bounded and metadata-only. */
  function collectLiveSessions(ctx, scan = true) {
    let service
    try {
      service = typeof ctx?.get === 'function' ? ctx.get('sessions') : undefined
    } catch (error) {
      noteInternalError(error)
      return []
    }
    if (typeof service !== 'object' || service === null || typeof service.list !== 'function') return []
    let list
    try {
      list = service.list()
    } catch (error) {
      noteInternalError(error)
      return []
    }
    if (!Array.isArray(list)) return []
    const metas = []
    for (const session of list.slice(0, MAX_SNAPSHOT_SESSIONS)) {
      const sessionId = readSessionId(session)
      if (sessionId === null) continue
      metas.push({ sessionId, ...readHeader(session), ...(scan ? scanLiveTurn(session) : {}), session })
    }
    return metas
  }

  /**
   * Publish hydration-flagged `snapshot` records for the current known sessions
   * and turns. Called at attach (and on a short deferred retry, for a sessions
   * service that mounts after this plugin).
   * @param ctx - the host context.
   * @returns the number of live sessions described.
   */
  function publishSnapshot(ctx) {
    return safely(() => {
      const metas = collectLiveSessions(ctx)
      for (const meta of metas) safely(() => {
        state.seedSession(meta)
        refreshQuestions(meta.session, true)
      })
      snapshotSeeded += metas.length
      emitAll(state.snapshots())
      return metas.length
    }) ?? 0
  }

  /**
   * Publish the attach heartbeat batch: one `heartbeat` per tracked live
   * session, or a single source-scoped heartbeat when no session is known.
   * @returns nothing.
   */
  function publishHeartbeat() {
    safely(() => {
      // Recheck cached state only, bounded by the same live-session attach limit.
      for (const meta of collectLiveSessions(hostContext, false)) safely(() => refreshQuestions(meta.session))
      emitAll(state.heartbeats())
    })
  }

  /**
   * Subscribe to the observer seams and start attach snapshot + heartbeat.
   * @param ctx - the Cordis context (or a test double with `on`/`get`).
   * @returns a dispose function; safe to call twice.
   */
  function attach(ctx) {
    if (config.disabled || attached) return () => {}
    attached = true
    hostContext = ctx
    if (typeof ctx?.on === 'function') {
      ctx.on('session/created', onCreated)
      seams.push('session/created')
      ctx.on('session/event', onEvent)
      seams.push('session/event')
      ctx.on('session/disposed', onDisposed)
      seams.push('session/disposed')
    }
    publishSnapshot(ctx)
    publishHeartbeat()
    heartbeatTimer = setInterval(() => publishHeartbeat(), config.heartbeatMs)
    heartbeatTimer?.unref?.()
    snapshotRetryTimer = setTimeout(() => {
      if (snapshotSeeded === 0) publishSnapshot(ctx)
    }, SNAPSHOT_RETRY_MS)
    snapshotRetryTimer?.unref?.()
    return dispose
  }

  /** Stop timers and drain the spool. Idempotent. */
  function dispose() {
    if (disposed) return
    disposed = true
    refreshSessions.clear()
    hostContext = null
    if (heartbeatTimer !== null) {
      clearInterval(heartbeatTimer)
      heartbeatTimer = null
    }
    if (snapshotRetryTimer !== null) {
      clearTimeout(snapshotRetryTimer)
      snapshotRetryTimer = null
    }
    safely(() => spool.close())
  }

  /**
   * Writer health: spool counters plus this adapter's internal error counters.
   * @returns the health document.
   */
  function health() {
    const document = safely(() => spool.health()) ?? null
    return {
      ...(document ?? {}),
      heartbeatMs: config.heartbeatMs,
      queueLimit: config.queueLimit,
      spoolRoot: config.spoolRoot,
      internalErrors,
      lastInternalError,
      seams: [...seams],
      attached,
      disposed,
    }
  }

  return {
    name,
    config,
    state,
    spool,
    attach,
    dispose,
    publishSnapshot,
    publishHeartbeat,
    health,
    noteInternalError,
    get seams() {
      return [...seams]
    },
  }
}

/**
 * Cordis plugin entry point. Never throws: a failed observer must not prevent
 * the host from publishing a session.
 *
 * @param ctx - the host Cordis context.
 * @param config - optional plugin config (the shipped bundle row configures nothing;
 *   the environment knobs are the documented interface).
 * @returns the observer instance, for tests and diagnostics.
 */
export function apply(ctx, config = {}) {
  const observer = createObserver(config ?? {})
  try {
    const dispose = observer.attach(ctx)
    const cleanup = () => observer.dispose()
    if (typeof ctx?.effect === 'function') ctx.effect(() => cleanup)
    else if (typeof ctx?.on === 'function') ctx.on('dispose', cleanup)
    void dispose
  } catch (error) {
    observer.noteInternalError(error)
  }
  return observer
}
