/**
 * OVERSEER Claude Code hook bridge - documented payload mapping.
 *
 * Upstream source of truth (retrieved 2026-09-24):
 *   https://code.claude.com/docs/en/hooks
 *   https://code.claude.com/docs/en/hooks.md
 *   https://code.claude.com/docs/en/tools-reference.md
 *
 * Only fields documented on those pages are read. Message content, tool
 * arguments, tool results, transcripts, prompts, environment values and
 * credentials are never read, never logged and never stored.
 */

import { createHash } from 'node:crypto';

export const SCHEMA_VERSION = 1;
export const HARNESS_ID = 'claude';
export const PRODUCER_VERSION = 'overseer-claude-bridge/1.0.8';

export const MAX_ID_CHARS = 128;
export const MAX_PROJECT_ROOT_CHARS = 512;
export const MAX_RECORD_BYTES = 64 * 1024;

/** kind allowlist (fixed by the OVERSEER record contract). */
export const KINDS = Object.freeze([
  'session/start', 'session/end',
  'turn/start', 'turn/end',
  'tool/start', 'tool/end',
  'approval/asked', 'approval/decided',
  'input/asked', 'input/answered',
  'plan/mode', 'heartbeat', 'snapshot',
]);

export const CATEGORIES = Object.freeze([
  'thinking', 'planning', 'working', 'waiting', 'error', 'complete', 'idle', 'unknown',
]);

export const EVIDENCES = Object.freeze(['explicit', 'inferred', 'unknown']);
export const OUTCOMES = Object.freeze(['completed', 'aborted', 'error', 'ok', 'failed']);

/** Exact record key set and order required by the OVERSEER contract. */
export const RECORD_KEYS = Object.freeze([
  'schemaVersion', 'harnessId', 'sourceInstanceId', 'sourceEpoch', 'sessionId',
  'sequence', 'eventId', 'observedAtUtc', 'eventAtUtc', 'kind', 'turnId', 'callId',
  'requestId', 'projectRoot', 'category', 'evidence', 'outcome', 'hydration',
  'producerVersion', 'hostSessionId',
]);

/** Documented Claude Code hook events the bridge maps to a normalized kind. */
export const SUPPORTED_EVENTS = Object.freeze([
  'SessionStart', 'SessionEnd', 'UserPromptSubmit', 'Stop', 'StopFailure',
  'PreToolUse', 'PostToolUse', 'PostToolUseFailure', 'PermissionRequest',
  'PermissionDenied', 'Notification', 'SubagentStart', 'SubagentStop',
  'PreCompact', 'PostCompact', 'TaskCreated', 'TaskCompleted', 'TeammateIdle',
  'Elicitation', 'ElicitationResult', 'MessageDisplay', 'PostToolBatch',
]);

/** Events the installer wires by default (documented, low-volume, decision-free). */
export const DEFAULT_INSTALL_EVENTS = Object.freeze([
  'SessionStart', 'SessionEnd', 'UserPromptSubmit', 'Stop', 'StopFailure',
  'PreToolUse', 'PostToolUse', 'PostToolUseFailure', 'PermissionRequest',
  'PermissionDenied', 'Notification', 'SubagentStart', 'SubagentStop',
  'PreCompact', 'PostCompact',
]);

/**
 * Documented Notification sub-types
 * (https://code.claude.com/docs/en/hooks#notification) mapped to a kind.
 * The bridge never stores the sub-type itself, only the resulting kind.
 */
const NOTIFICATION_MAP = Object.freeze({
  // Sandboxed network prompts can emit this without PermissionRequest.
  permission_prompt: ['approval/asked', 'waiting', null],
  agent_needs_input: ['input/asked', 'waiting', null],
  elicitation_dialog: ['input/asked', 'waiting', null],
  elicitation_url_dialog: ['input/asked', 'waiting', null],
  idle_prompt: ['heartbeat', 'idle', null],
  agent_completed: ['heartbeat', 'complete', 'completed'],
  auth_success: ['heartbeat', 'idle', null],
  elicitation_complete: ['heartbeat', 'idle', 'completed'],
  elicitation_response: ['heartbeat', 'working', null],
  quota_auto_resume_fired: ['heartbeat', 'working', null],
  quota_auto_resume_stale: ['heartbeat', 'idle', null],
  quota_auto_resume_disabled: ['heartbeat', 'idle', null],
});

/**
 * Documented tools that carry their own meaning for the OVERSEER timeline.
 * EnterPlanMode / ExitPlanMode: https://code.claude.com/docs/en/tools-reference.md
 * AskUserQuestion: https://code.claude.com/docs/en/hooks#pretooluse-input
 */
const PLAN_MODE_TOOLS = Object.freeze(['EnterPlanMode', 'ExitPlanMode']);
const QUESTION_TOOLS = Object.freeze(['AskUserQuestion']);

/** Events without a sub-type classifier: kind, category, outcome, evidence. */
const EVENT_MAP = Object.freeze({
  SessionStart: ['session/start', 'idle', null, 'explicit'],
  SessionEnd: ['session/end', 'idle', 'completed', 'explicit'],
  UserPromptSubmit: ['turn/start', 'thinking', null, 'explicit'],
  Stop: ['turn/end', 'complete', 'completed', 'explicit'],
  StopFailure: ['turn/end', 'error', 'error', 'explicit'],
  PostToolUseFailure: ['tool/end', 'error', 'failed', 'explicit'],
  // PermissionRequest is documented without tool_use_id, so it cannot be
  // correlated to a call: callId stays null and evidence is "inferred".
  PermissionRequest: ['approval/asked', 'waiting', null, 'inferred'],
  PermissionDenied: ['approval/decided', 'working', 'aborted', 'explicit'],
  // These share the parent's session_id. A child finishing does not finish
  // the parent's turn or answer its requests (hooks reference, SubagentStop).
  SubagentStart: ['heartbeat', 'working', null, 'explicit'],
  SubagentStop: ['heartbeat', 'working', null, 'explicit'],
  PreCompact: ['snapshot', 'working', null, 'explicit'],
  PostCompact: ['snapshot', 'complete', 'completed', 'explicit'],
  TaskCreated: ['heartbeat', 'working', null, 'explicit'],
  TaskCompleted: ['heartbeat', 'complete', 'completed', 'explicit'],
  TeammateIdle: ['heartbeat', 'idle', null, 'explicit'],
  Elicitation: ['input/asked', 'waiting', null, 'explicit'],
  ElicitationResult: ['input/answered', 'working', 'completed', 'explicit'],
  MessageDisplay: ['heartbeat', 'thinking', null, 'explicit'],
  PostToolBatch: ['heartbeat', 'working', null, 'explicit'],
});

export function isPlainObject(value) {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

/**
 * Bounded, control-character-free copy of a documented string field.
 * Returns null when the field is absent, empty or not a string.
 */
export function safeToken(value, max = MAX_ID_CHARS) {
  if (typeof value !== 'string') return null;
  let out = '';
  for (const ch of value) {
    const code = ch.codePointAt(0);
    if (code < 0x20 || code === 0x7f || (code >= 0xd800 && code <= 0xdfff)) continue;
    if (out.length + ch.length > max) break;
    out += ch;
  }
  out = out.trim();
  return out.length > 0 ? out : null;
}

export function safeProjectRoot(value) {
  return safeToken(value, MAX_PROJECT_ROOT_CHARS);
}

/** Opaque host identities must never alias through prefix truncation. */
export function safeIdentity(value) {
  if (typeof value !== 'string' || value.trim() === '') return null;
  if (/[\uD800-\uDFFF]/u.test(value)) return 'sha256-u16-' + createHash('sha256').update(value, 'utf16le').digest('hex');
  if (value.length > MAX_ID_CHARS || value.startsWith('sha256-') || value !== value.trim() || /[\x00-\x1f\x7f]/.test(value)) {
    return 'sha256-' + createHash('sha256').update(value, 'utf8').digest('hex');
  }
  return value;
}

function isQuestionTool(tool) {
  return typeof tool === 'string' && QUESTION_TOOLS.includes(tool);
}

function isPlanModeTool(tool) {
  return typeof tool === 'string' && PLAN_MODE_TOOLS.includes(tool);
}

/**
 * Classify one documented hook payload.
 * Returns { event, kind, category, evidence, outcome, known }.
 */
export function classify(payload) {
  const event = safeToken(payload.hook_event_name, 64);
  if (!event) {
    return { event: null, kind: 'heartbeat', category: 'unknown', evidence: 'unknown', outcome: null, known: false };
  }

  if (event === 'Notification') {
    const type = safeToken(payload.notification_type, 64);
    const mapped = type && Object.hasOwn(NOTIFICATION_MAP, type) ? NOTIFICATION_MAP[type] : undefined;
    if (!mapped) {
      return { event, kind: 'heartbeat', category: 'unknown', evidence: 'unknown', outcome: null, known: false };
    }
    return { event, kind: mapped[0], category: mapped[1], evidence: 'inferred', outcome: mapped[2], known: true };
  }

  if (event === 'PreToolUse') {
    const tool = safeToken(payload.tool_name, 64);
    if (isQuestionTool(tool)) {
      return { event, kind: 'input/asked', category: 'waiting', evidence: 'explicit', outcome: null, known: true };
    }
    if (isPlanModeTool(tool)) {
      return { event, kind: 'plan/mode', category: 'planning', evidence: 'explicit', outcome: null, known: true };
    }
    return { event, kind: 'tool/start', category: 'working', evidence: 'explicit', outcome: null, known: true };
  }

  if (event === 'PermissionRequest' && isQuestionTool(safeToken(payload.tool_name, 64))) {
    // The question dialog is presented through the permission flow. PreToolUse
    // already recorded it as input/asked with its call ID; it is not an approval.
    return { event, kind: 'heartbeat', category: 'waiting', evidence: 'inferred', outcome: null, known: true };
  }

  if (event === 'PostToolUse') {
    const tool = safeToken(payload.tool_name, 64);
    if (isQuestionTool(tool)) {
      return { event, kind: 'input/answered', category: 'working', evidence: 'explicit', outcome: 'ok', known: true };
    }
    if (isPlanModeTool(tool)) {
      return { event, kind: 'plan/mode', category: 'planning', evidence: 'explicit', outcome: 'ok', known: true };
    }
    return { event, kind: 'tool/end', category: 'working', evidence: 'explicit', outcome: 'ok', known: true };
  }

  const mapped = Object.hasOwn(EVENT_MAP, event) ? EVENT_MAP[event] : undefined;
  if (!mapped) {
    // Documented but unmapped, or an event this bridge has never seen.
    return { event, kind: 'heartbeat', category: 'unknown', evidence: 'unknown', outcome: null, known: false };
  }
  return { event, kind: mapped[0], category: mapped[1], evidence: mapped[3], outcome: mapped[2], known: true };
}

/**
 * Stable id for one hook event identity. The same published event produces the
 * same id, so duplicate publication is idempotent for a consumer, while two
 * distinct calls in one session stay distinct.
 */
export function deriveEventId(payload, { sourceInstanceId, sessionId, kind }) {
  const identity = [
    HARNESS_ID,
    sourceInstanceId,
    sessionId,
    kind,
    safeToken(payload.hook_event_name, 64) ?? '',
    safeIdentity(payload.tool_use_id) ?? '',
    safeIdentity(payload.prompt_id) ?? '',
    safeIdentity(payload.turn_id) ?? '',
    safeIdentity(payload.agent_id) ?? '',
    safeIdentity(payload.task_id) ?? '',
    safeIdentity(payload.elicitation_id) ?? '',
    safeIdentity(payload.message_id) ?? '',
    safeToken(payload.notification_type, 64) ?? '',
    safeToken(payload.source, 32) ?? '',
    safeToken(payload.reason, 64) ?? '',
    safeToken(payload.trigger, 32) ?? '',
  ].join('\u0000');
  return 'ccev-' + createHash('sha256').update(identity, 'utf8').digest('hex').slice(0, 32);
}

/**
 * Build the normalized record for one documented hook payload.
 * ctx: { sourceInstanceId, sourceEpoch, observedAtUtc, producerVersion, hostSessionId }
 * sequence is a placeholder that the spool replaces with the atomically
 * allocated per-session ingestion order.
 */
export function buildRecord(payload, ctx) {
  if (!isPlainObject(payload)) return null;
  const cls = classify(payload);
  const sessionId = safeIdentity(payload.session_id) ?? 'unknown';
  const turnId = safeIdentity(payload.prompt_id) ?? safeIdentity(payload.turn_id);
  const callId = safeIdentity(payload.tool_use_id);
  const isRequest = ['approval/asked', 'approval/decided', 'input/asked', 'input/answered'].includes(cls.kind);
  // Agent/task identities are not request identities. Two questions from one
  // agent must remain independently answerable by their tool/elicitation IDs.
  const requestId = safeIdentity(payload.elicitation_id) ?? (isRequest
    ? callId
    : safeIdentity(payload.agent_id) ?? safeIdentity(payload.task_id));
  const projectRoot = safeProjectRoot(payload.cwd);
  // A payload without session_id cannot be attributed; say so instead of guessing.
  const evidence = sessionId === 'unknown' ? 'unknown' : cls.evidence;
  const observedAtUtc = ctx.observedAtUtc;

  return {
    schemaVersion: SCHEMA_VERSION,
    harnessId: HARNESS_ID,
    sourceInstanceId: ctx.sourceInstanceId,
    sourceEpoch: ctx.sourceEpoch,
    sessionId,
    sequence: 0,
    eventId: deriveEventId(payload, { sourceInstanceId: ctx.sourceInstanceId, sessionId, kind: cls.kind }),
    observedAtUtc,
    // Claude Code documents no timestamp field on any hook payload, so
    // eventAtUtc is the bridge's receipt time (see README, "Known limits").
    eventAtUtc: observedAtUtc,
    kind: cls.kind,
    turnId,
    callId,
    requestId,
    projectRoot,
    category: cls.category,
    evidence,
    outcome: cls.outcome,
    // Always false: a live one-shot hook publication, never a replay.
    hydration: false,
    producerVersion: ctx.producerVersion ?? PRODUCER_VERSION,
    // The Claude desktop app's own id for this session, so OVERSEER can open it.
    hostSessionId: safeHostSessionId(ctx.hostSessionId),
  };
}

/** Desktop session ids look like local_<uuid>; anything else is dropped. */
const HOST_SESSION_ID = /^local_[A-Za-z0-9-]{1,64}$/;
export function safeHostSessionId(value) {
  return typeof value === 'string' && HOST_SESSION_ID.test(value) ? value : null;
}

/**
 * The desktop session id for this hook, read from exactly two variables Claude
 * Code sets for its hooks. Nothing else in the environment is read.
 */
export function hostSessionIdFromEnv(env) {
  if (!env || env.CLAUDE_CODE_ENTRYPOINT !== 'claude-desktop') return null;
  return safeHostSessionId(env.CLAUDE_CODE_HOST_SESSION_ID);
}

/** Contract check used by the bridge before publishing and by the tests. */
export function validateRecord(record, { requireSequence = true } = {}) {
  const problems = [];
  if (!isPlainObject(record)) return ['record is not an object'];
  const keys = Object.keys(record);
  if (keys.length !== RECORD_KEYS.length) problems.push(`expected ${RECORD_KEYS.length} keys, found ${keys.length}`);
  for (let i = 0; i < RECORD_KEYS.length; i++) {
    if (keys[i] !== RECORD_KEYS[i]) { problems.push(`key ${i} is "${keys[i]}" not "${RECORD_KEYS[i]}"`); break; }
  }
  if (record.schemaVersion !== SCHEMA_VERSION) problems.push('schemaVersion must be 1');
  if (record.harnessId !== HARNESS_ID) problems.push('harnessId must be "claude"');
  for (const key of ['sourceInstanceId', 'sourceEpoch', 'sessionId', 'eventId', 'producerVersion']) {
    if (typeof record[key] !== 'string' || record[key].length === 0) problems.push(`${key} must be a non-empty string`);
  }
  if (!Number.isSafeInteger(record.sequence) || record.sequence < (requireSequence ? 1 : 0)) {
    problems.push('sequence must be a non-negative safe integer');
  }
  for (const key of ['observedAtUtc', 'eventAtUtc']) {
    if (typeof record[key] !== 'string' || Number.isNaN(Date.parse(record[key]))) problems.push(`${key} must be an ISO timestamp`);
  }
  if (!KINDS.includes(record.kind)) problems.push(`kind "${record.kind}" is not allowlisted`);
  if (!CATEGORIES.includes(record.category)) problems.push(`category "${record.category}" is not allowlisted`);
  if (!EVIDENCES.includes(record.evidence)) problems.push(`evidence "${record.evidence}" is not allowlisted`);
  if (record.outcome !== null && !OUTCOMES.includes(record.outcome)) problems.push(`outcome "${record.outcome}" is not allowlisted`);
  for (const key of ['turnId', 'callId', 'requestId', 'projectRoot']) {
    const value = record[key];
    if (value !== null && typeof value !== 'string') problems.push(`${key} must be a string or null`);
  }
  if (typeof record.projectRoot === 'string' && record.projectRoot.length > MAX_PROJECT_ROOT_CHARS) {
    problems.push('projectRoot exceeds 512 characters');
  }
  if (typeof record.hydration !== 'boolean') problems.push('hydration must be a boolean');
  if (record.hostSessionId !== null && safeHostSessionId(record.hostSessionId) === null) problems.push('hostSessionId must be null or a desktop local_ id');
  const line = JSON.stringify(record);
  if (Buffer.byteLength(line, 'utf8') > MAX_RECORD_BYTES) problems.push('record exceeds 64 KiB');
  return problems;
}
