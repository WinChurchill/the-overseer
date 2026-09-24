/**
 * Allowlisted normalization of DeepSeek Harness session events into OVERSEER
 * observer records.
 *
 * Privacy rule: only the metadata named in {@link RECORD_KEYS} ever leaves the
 * process. This module reads no message body, no reasoning text, no tool
 * arguments, no tool output, no credentials, and no environment value.
 * {@link describeEvent} switches on the session event `type` FIRST and then
 * touches only the specific scalar fields the allowlist needs, so unmapped
 * events (user/message, assistant/message, system/message, tool/ptc-dispatch,
 * ...) are never opened at all.
 *
 * Upstream contracts read (pinned read-only reference checkout, commit
 * 46a7f68b0922371ce7144b668b90e377d8e799f4):
 * - packages/core/session/src/types.ts — SessionEvent envelope
 *   `{ type, seq, time, data }`; `seq` is the per-session monotonic number
 *   (`seq = log.length`, contiguous from 0) and `time` is Unix epoch ms;
 *   `turn/start` is `{ turn }` and `turn/end` is `{ turn, reason }` where
 *   `TurnEndReason` is a merge-extensible sum over `kind`.
 * - packages/core/session/src/known-event-types.ts — the emitted vocabulary
 *   used here: tool/call, tool/result, approval/asked, approval/decided,
 *   plan/mode, turn/start, turn/end.
 * - packages/interaction/user-approval/src/types.ts — `approval/asked`
 *   carries `{ id, toolName, callId?, reason? }` and `approval/decided`
 *   carries `{ id, outcome }`; `id` pairs the two, `callId` is the exact
 *   tool call when the asker had one. This observer reads `id`, `callId`,
 *   and `outcome` only — never `toolName` and never `reason`.
 * - packages/plan/plan-mode/src/index.ts — `plan/mode` is `{ active }`,
 *   "log-only, non-surface, whole-value replace. The last plan/mode wins".
 *
 * @module overseer-dsh-observer/normalize
 */

/** Normalized record schema version. */
export const SCHEMA_VERSION = 1

/** Harness identity. Fixed, never derived from the host environment. */
export const HARNESS_ID = 'dsh'

/** Producer string published on every record. */
export const PRODUCER_VERSION = 'dsh-observer/1.0.0'

/**
 * The complete and closed key set of one normalized record, in canonical
 * serialization order. A record is built from exactly these keys; nothing else
 * is ever added.
 */
export const RECORD_KEYS = Object.freeze([
  'schemaVersion',
  'harnessId',
  'sourceInstanceId',
  'sourceEpoch',
  'sessionId',
  'sequence',
  'eventId',
  'observedAtUtc',
  'eventAtUtc',
  'kind',
  'turnId',
  'callId',
  'requestId',
  'projectRoot',
  'category',
  'evidence',
  'outcome',
  'hydration',
  'producerVersion',
])

/** Closed normalized-kind allowlist. `input/asked`/`input/answered` are reserved (see README). */
export const KINDS = Object.freeze(new Set([
  'session/start',
  'session/end',
  'turn/start',
  'turn/end',
  'tool/start',
  'tool/end',
  'approval/asked',
  'approval/decided',
  'input/asked',
  'input/answered',
  'plan/mode',
  'heartbeat',
  'snapshot',
]))

/** Closed category allowlist. */
export const CATEGORIES = Object.freeze(new Set([
  'thinking', 'planning', 'working', 'waiting', 'error', 'complete', 'idle', 'unknown',
]))

/** Closed evidence allowlist. */
export const EVIDENCES = Object.freeze(new Set(['explicit', 'inferred', 'unknown']))

/** Closed outcome allowlist, plus null for "no outcome applies". */
export const OUTCOMES = Object.freeze(new Set(['completed', 'aborted', 'error', 'ok', 'failed']))

/** Field bounds that keep one record far below the spool record cap. */
export const LIMITS = Object.freeze({
  /** Maximum serialized size of one normalized record (spool-enforced). */
  recordBytes: 64 * 1024,
  /** Bound for opaque ids (sessionId, callId, requestId). */
  idChars: 256,
  /** Bound for the exported project root (session header `cwd`). */
  projectRootChars: 512,
  /** Bound for the configured source instance id. */
  sourceIdChars: 128,
  /** Bound for the per-process source epoch id. */
  sourceEpochChars: 128,
})

/**
 * Bound one string value to `max` characters.
 * @param value - candidate value; non-strings return null.
 * @param max - maximum retained character count.
 * @returns the bounded string, or null when the value is not a usable string.
 */
export function clampText(value, max) {
  if (typeof value !== 'string') return null
  const trimmed = value.trim()
  if (trimmed === '') return null
  return trimmed.length > max ? trimmed.slice(0, max) : trimmed
}

/** @returns the value as a plain JSON record, or undefined. */
function asRecord(value) {
  return typeof value === 'object' && value !== null && !Array.isArray(value) ? value : undefined
}

/** @returns a bounded non-empty string field, or null. */
function readString(source, key, max) {
  return clampText(asRecord(source)?.[key], max)
}

/** @returns a non-negative safe integer field, or null. */
function readCount(source, key) {
  const value = asRecord(source)?.[key]
  return typeof value === 'number' && Number.isSafeInteger(value) && value >= 0 ? value : null
}

/** @returns a boolean field, or undefined when absent/unusable. */
function readFlag(source, key) {
  const value = asRecord(source)?.[key]
  return typeof value === 'boolean' ? value : undefined
}

/**
 * Map a `turn/end` reason to category/outcome. Merge-extensible upstream:
 * an unrecognized `kind` yields `unknown` evidence instead of a guess.
 * @param reason - the `TurnEndReason` value from the event data.
 * @returns category, outcome, and evidence for the record.
 */
export function describeTurnEnd(reason) {
  const kind = readString(reason, 'kind', 64)
  switch (kind) {
    case 'completed':
      return { category: 'complete', outcome: 'completed', evidence: 'explicit' }
    case 'aborted':
      // Silent cancellation: the live turn was interrupted, no failure recorded.
      return { category: 'idle', outcome: 'aborted', evidence: 'explicit' }
    case 'error':
      return { category: 'error', outcome: 'error', evidence: 'explicit' }
    case 'max-tokens':
      // Output ceiling reached; the turn did not fail, it did not complete either.
      return { category: 'unknown', outcome: 'aborted', evidence: 'explicit' }
    case 'blocked':
      return { category: 'unknown', outcome: 'aborted', evidence: 'explicit' }
    case 'interrupted':
      // Crash-orphaned turn closed after the fact.
      return { category: 'error', outcome: 'aborted', evidence: 'explicit' }
    case 'forked':
      // A fork boundary closed a turn still open in the source session.
      return { category: 'unknown', outcome: 'aborted', evidence: 'explicit' }
    default:
      return { category: 'unknown', outcome: null, evidence: 'unknown' }
  }
}

/**
 * Map an approval outcome to category/outcome. Upstream `ApprovalOutcome` is a
 * closed union: allowed-once | rejected | cancelled | unavailable.
 * @param outcome - the `approval/decided` outcome value.
 * @returns category, outcome, and evidence for the record.
 */
export function describeApprovalOutcome(outcome) {
  switch (outcome) {
    case 'allowed-once':
      return { category: 'working', outcome: 'ok', evidence: 'explicit' }
    case 'rejected':
      return { category: 'idle', outcome: 'failed', evidence: 'explicit' }
    case 'cancelled':
      return { category: 'idle', outcome: 'aborted', evidence: 'explicit' }
    case 'unavailable':
      // Fail-closed answerer absence: a real error condition, reported as such.
      return { category: 'error', outcome: 'error', evidence: 'explicit' }
    default:
      return { category: 'unknown', outcome: null, evidence: 'unknown' }
  }
}

/**
 * Describe one session event as allowlisted metadata.
 *
 * @param event - the `session/event` payload: `{ type, seq, time, data }`.
 * @returns a descriptor `{ kind, turnNumber, callId, requestId, category, evidence, outcome }`,
 *   or null when the event type is outside the exported vocabulary.
 */
export function describeEvent(event) {
  const type = asRecord(event)?.['type']
  const data = asRecord(event)?.['data']
  switch (type) {
    case 'turn/start':
      return {
        kind: 'turn/start',
        turnNumber: readCount(data, 'turn'),
        callId: null,
        requestId: null,
        category: 'working',
        evidence: 'explicit',
        outcome: null,
      }
    case 'turn/end': {
      const mapped = describeTurnEnd(asRecord(data)?.['reason'])
      return {
        kind: 'turn/end',
        turnNumber: readCount(data, 'turn'),
        callId: null,
        requestId: null,
        ...mapped,
      }
    }
    case 'tool/call':
      // Reads the call identity only: never `name`, never `arguments`.
      return {
        kind: 'tool/start',
        turnNumber: readCount(data, 'turn'),
        callId: readString(data, 'callId', LIMITS.idChars),
        requestId: null,
        category: 'working',
        evidence: 'explicit',
        outcome: null,
      }
    case 'tool/result': {
      // Reads the call identity and the failure flag only: never `message.content`,
      // never `meta`. A failed tool is NOT a task failure — outcome stays local
      // to this tool/end record.
      const message = asRecord(data)?.['message']
      return {
        kind: 'tool/end',
        turnNumber: readCount(data, 'turn'),
        callId: readString(message, 'toolCallId', LIMITS.idChars),
        requestId: null,
        category: 'working',
        evidence: 'explicit',
        outcome: readFlag(message, 'isError') === true ? 'failed' : 'ok',
      }
    }
    case 'approval/asked':
      // Reads the correlation ids only: never `toolName`, never `reason`.
      return {
        kind: 'approval/asked',
        turnNumber: null,
        callId: readString(data, 'callId', LIMITS.idChars),
        requestId: readString(data, 'id', LIMITS.idChars),
        category: 'waiting',
        evidence: 'explicit',
        outcome: null,
      }
    case 'approval/decided': {
      const mapped = describeApprovalOutcome(asRecord(data)?.['outcome'])
      return {
        kind: 'approval/decided',
        turnNumber: null,
        callId: null,
        requestId: readString(data, 'id', LIMITS.idChars),
        ...mapped,
      }
    }
    case 'plan/mode':
      // `active` is the whole payload and the last value wins upstream. Planning
      // is reported ONLY while plan mode is actually on; the switch itself is not
      // a sustained planning duration.
      return {
        kind: 'plan/mode',
        turnNumber: null,
        callId: null,
        requestId: null,
        category: readFlag(data, 'active') === true ? 'planning' : 'unknown',
        evidence: 'explicit',
        outcome: null,
      }
    default:
      return null
  }
}
