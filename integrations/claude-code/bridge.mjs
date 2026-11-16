#!/usr/bin/env node
/**
 * OVERSEER Claude Code hook bridge - one-shot hook helper.
 *
 * Contract (see README.md):
 *   - reads at most 1 MiB of hook JSON from stdin with a hard 2 second deadline;
 *   - maps only allowlisted documented fields; never reads or logs prompts,
 *     tool arguments, tool results, transcripts or credentials. The only
 *     environment values read are CLAUDE_CODE_ENTRYPOINT and
 *     CLAUDE_CODE_HOST_SESSION_ID, to record the desktop session id;
 *   - appends one bounded (<= 64 KiB) JSON line to the OVERSEER spool;
 *   - never returns a permission decision, never prints to stdout, always exits 0;
 *   - all acquisition/write work is bounded by a 250 ms budget, after which the
 *     record is dropped and its accounting receipt is published within the same budget.
 *
 * Upstream documentation (retrieved 2026-09-24):
 *   https://code.claude.com/docs/en/hooks
 */

import process from 'node:process';

import { PRODUCER_VERSION, buildRecord, classify, hostSessionIdFromEnv, validateRecord } from './lib/mapping.mjs';
import {
  Deadline,
  beginHealthAccounting,
  bumpHealth,
  ensureSourceEpoch,
  ensureSourceInstance,
  instanceDir,
  publishRecord,
  resolveSpoolRoot,
} from './lib/spool.mjs';

const MAX_STDIN_BYTES = 1024 * 1024; // 1 MiB
const STDIN_DEADLINE_MS = 2000; // hard deadline for reading stdin
const WRITE_BUDGET_MS = 250; // acquisition + write + health budget
const HEALTH_BUDGET_MS = 60; // slice of the write budget for counters
const HARD_EXIT_MS = STDIN_DEADLINE_MS + WRITE_BUDGET_MS + 250; // watchdog

const DEBUG = process.env.OVERSEER_CLAUDE_BRIDGE_DEBUG === '1';

/** Sanitized diagnostics only: never payload content. */
function debug(...parts) {
  if (!DEBUG) return;
  try {
    process.stderr.write('[overseer-claude-bridge] ' + parts.join(' ') + '\n');
  } catch {
    /* ignore */
  }
}

/**
 * Read the hook payload with a byte cap and a hard deadline.
 * Reasons: ok | empty | oversize | deadline | error.
 */
function readStdin(deadlineMs) {
  return new Promise((resolve) => {
    const stdin = process.stdin;
    let settled = false;
    let bytes = 0;
    const chunks = [];

    const finish = (reason, text) => {
      if (settled) return;
      settled = true;
      clearTimeout(timer);
      try { stdin.removeAllListeners(); } catch { /* ignore */ }
      resolve({ reason, text });
    };

    if (!stdin || stdin.isTTY) {
      finish('empty');
      return;
    }

    const timer = setTimeout(() => {
      try { stdin.destroy(); } catch { /* ignore */ }
      finish('deadline');
    }, Math.max(1, deadlineMs));
    if (typeof timer.unref === 'function') timer.unref();

    const complete = () => finish(bytes === 0 ? 'empty' : 'ok', Buffer.concat(chunks, bytes).toString('utf8'));

    stdin.on('data', (chunk) => {
      if (settled) return;
      bytes += chunk.length;
      if (bytes > MAX_STDIN_BYTES) {
        try { stdin.destroy(); } catch { /* ignore */ }
        finish('oversize');
        return;
      }
      chunks.push(chunk);
    });
    stdin.on('end', complete);
    stdin.on('close', complete);
    stdin.on('error', () => finish('error'));
    try { stdin.resume(); } catch { /* ignore */ }
  });
}

const STDIN_FAILURE_COUNTER = {
  oversize: 'droppedOversizeInput',
  empty: 'droppedEmptyInput',
  deadline: 'droppedStdinDeadline',
  error: 'droppedStdinError',
};

/**
 * Failure accounting after stdin handling. Uses the same 250 ms acquisition
 * budget as a successful write, so a failing hook is never slower than a
 * successful one.
 */
function countFailure(spoolRoot, counter) {
  const budget = new Deadline(WRITE_BUDGET_MS);
  const instance = ensureSourceInstance(spoolRoot, process.env, budget);
  if (!instance.sourceInstanceId) {
    debug('failure-counter-skipped', counter);
    return;
  }
  const dir = instanceDir(spoolRoot, instance.sourceInstanceId);
  // A cold directory can consume the small health slice before any receipt is
  // attempted. Acquire it inside the unchanged parent budget first.
  const intent = beginHealthAccounting(dir, budget);
  if (!intent) return; // Admission failure is already an explicit loss episode.
  bumpHealth(dir, counter, budget, intent);
  debug('counted', counter);
}

async function run() {
  const stdinPhase = new Deadline(STDIN_DEADLINE_MS);
  const spoolRoot = resolveSpoolRoot();

  const input = await readStdin(stdinPhase.remaining());
  if (input.reason !== 'ok') {
    debug('stdin', input.reason);
    countFailure(spoolRoot, STDIN_FAILURE_COUNTER[input.reason] ?? 'droppedInternalError');
    return;
  }

  let payload;
  try {
    payload = JSON.parse(input.text);
  } catch {
    debug('stdin malformed-json');
    countFailure(spoolRoot, 'droppedMalformedInput');
    return;
  }
  if (payload === null || typeof payload !== 'object' || Array.isArray(payload)) {
    debug('stdin unsupported-payload');
    countFailure(spoolRoot, 'droppedUnsupportedPayload');
    return;
  }

  // Everything below shares one bounded acquisition/write budget.
  const writePhase = new Deadline(WRITE_BUDGET_MS);

  const instance = ensureSourceInstance(spoolRoot, process.env, writePhase);
  if (!instance.sourceInstanceId) {
    debug('instance-unavailable');
    return;
  }
  const dir = instanceDir(spoolRoot, instance.sourceInstanceId);
  const intent = beginHealthAccounting(dir, writePhase);
  if (!intent) {
    // Never land an event after its bounded accounting admission failed: the
    // durable loss marker must not later count a successfully published event.
    debug('accounting-admission-unavailable');
    return;
  }
  const account = counters => bumpHealth(dir, counters, writePhase, intent);
  const counters = [];

  const epoch = ensureSourceEpoch(dir, instance.sourceInstanceId, writePhase);
  if (!epoch.sourceEpoch) {
    debug('source-epoch-unavailable', epoch.error ?? 'unknown');
    account(epoch.error === 'identity-unstable' ? 'droppedCorruptSourceEpoch' : 'droppedInternalError');
    return;
  }
  if (epoch.recovered || instance.recovered) counters.push('recoveredCorruptFile');

  const classification = classify(payload);
  if (!classification.known) counters.push('unknownEvents');

  const record = buildRecord(payload, {
    sourceInstanceId: instance.sourceInstanceId,
    sourceEpoch: epoch.sourceEpoch,
    observedAtUtc: new Date().toISOString(),
    producerVersion: PRODUCER_VERSION,
    hostSessionId: hostSessionIdFromEnv(process.env),
  });
  if (!record) {
    account('droppedUnsupportedPayload');
    return;
  }

  const problems = validateRecord(record, { requireSequence: false });
  if (problems.length > 0) {
    debug('record-rejected', problems.length);
    account('droppedInternalError');
    return;
  }

  const result = publishRecord({
    dir,
    record,
    // Acquisition has already spent part of the parent budget. Reserve health
    // from what remains, not from the original allowance; otherwise a slow
    // first attach can consume the entire remainder and silently lose its drop.
    deadline: writePhase.child(Math.max(0, writePhase.remaining() - HEALTH_BUDGET_MS)),
  });

  if (result.ok) {
    if (result.recovered && !counters.includes('recoveredCorruptFile')) counters.push('recoveredCorruptFile');
    counters.push('recordsWritten');
    account(counters);
    debug('wrote', record.kind, 'sequence', String(result.sequence));
    return;
  }

  // Every non-ok outcome is accounted for honestly: a genuine write failure is
  // never mislabeled as an oversized record.
  if (result.reason === 'lock-timeout') {
    counters.push('droppedLockTimeout');
  } else if (result.reason === 'record-too-large') {
    counters.push('droppedOversizeRecord');
  } else {
    counters.push('droppedWriteFailed');
  }

  account(counters);
  debug('dropped', result.reason);
}

// Never hang Claude Code: fail open even if something inside the bridge stalls.
const watchdog = setTimeout(() => process.exit(0), HARD_EXIT_MS);
if (typeof watchdog.unref === 'function') watchdog.unref();

process.on('uncaughtException', () => process.exit(0));
process.on('unhandledRejection', () => process.exit(0));

run()
  .catch(() => { /* fail open */ })
  .finally(() => {
    // Empty stdout, exit 0: the bridge never makes a permission decision.
    process.exit(0);
  });
