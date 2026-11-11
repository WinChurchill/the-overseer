import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';

import { RECORD_KEYS, buildRecord, classify, validateRecord } from '../lib/mapping.mjs';
import { Deadline, HEALTH_COUNTERS, publishRecord } from '../lib/spool.mjs';
import {
  collectStrings,
  fixture,
  fixtureNames,
  instanceDirectory,
  readRecords,
  readSourceEpoch,
  removeDir,
  runBridge,
  tempDir,
} from '../lib/testkit.mjs';

const spoolEnv = (spoolRoot) => ({ OVERSEER_SPOOL_ROOT: spoolRoot });

test('opaque identity boundaries preserve distinct sessions, requests and event IDs', () => {
  const ids = ['x'.repeat(127), 'x'.repeat(128), 'x'.repeat(128) + 'A', 'x'.repeat(128) + 'B'.repeat(128)];
  const records = ids.map(id => buildRecord({ session_id: id, hook_event_name: 'PreToolUse',
    tool_name: 'AskUserQuestion', tool_use_id: id }, CTX));
  for (const field of ['sessionId', 'requestId', 'callId', 'eventId']) {
    assert.equal(new Set(records.map(record => record[field])).size, 4);
    assert.ok(records.every(record => record[field].length <= 128));
  }
  for (let index = 0; index < ids.length; index++) {
    const answer = buildRecord({ session_id: ids[index], hook_event_name: 'PostToolUse',
      tool_name: 'AskUserQuestion', tool_use_id: ids[index] }, CTX);
    assert.equal(answer.sessionId, records[index].sessionId);
    assert.equal(answer.requestId, records[index].requestId);
  }
  const literal = buildRecord({ session_id: records[2].sessionId, hook_event_name: 'SessionStart' }, CTX);
  assert.notEqual(literal.sessionId, records[2].sessionId, 'literal hash token cannot impersonate the long host ID');
});

const CTX = {
  sourceInstanceId: 'cc-fixture-instance',
  sourceEpoch: 'epoch-20260924T000000Z-deadbeef',
  observedAtUtc: '2026-09-24T00:00:00.000Z',
  producerVersion: 'overseer-claude-bridge/1.0.2',
};

/** fixture file -> [kind, category, outcome, evidence] */
const EXPECTED = {
  'session-start.json': ['session/start', 'idle', null, 'explicit'],
  'session-end.json': ['session/end', 'idle', 'completed', 'explicit'],
  'user-prompt-submit.json': ['turn/start', 'thinking', null, 'explicit'],
  'stop.json': ['turn/end', 'complete', 'completed', 'explicit'],
  'stop-failure.json': ['turn/end', 'error', 'error', 'explicit'],
  'pre-tool-use.json': ['tool/start', 'working', null, 'explicit'],
  'pre-tool-use-question.json': ['input/asked', 'waiting', null, 'explicit'],
  'pre-tool-use-plan-mode.json': ['plan/mode', 'planning', null, 'explicit'],
  'post-tool-use.json': ['tool/end', 'working', 'ok', 'explicit'],
  'post-tool-use-failure.json': ['tool/end', 'error', 'failed', 'explicit'],
  'permission-request.json': ['approval/asked', 'waiting', null, 'inferred'],
  'permission-request-question.json': ['heartbeat', 'waiting', null, 'inferred'],
  'permission-denied.json': ['approval/decided', 'working', 'aborted', 'explicit'],
  'notification-permission-prompt.json': ['approval/asked', 'waiting', null, 'inferred'],
  'notification-idle.json': ['heartbeat', 'idle', null, 'inferred'],
  'notification-agent-needs-input.json': ['input/asked', 'waiting', null, 'inferred'],
  'subagent-start.json': ['heartbeat', 'working', null, 'explicit'],
  'subagent-stop.json': ['heartbeat', 'working', null, 'explicit'],
  'pre-compact.json': ['snapshot', 'working', null, 'explicit'],
  'post-compact.json': ['snapshot', 'complete', 'completed', 'explicit'],
  'message-display.json': ['heartbeat', 'thinking', null, 'explicit'],
  'task-completed.json': ['heartbeat', 'complete', 'completed', 'explicit'],
  'elicitation.json': ['input/asked', 'waiting', null, 'explicit'],
  'unknown-event.json': ['heartbeat', 'unknown', null, 'unknown'],
};

test('every fixture maps to the documented normalized record', () => {
  const names = fixtureNames();
  assert.deepEqual(names, Object.keys(EXPECTED).sort(), 'every fixture needs an expectation and vice versa');

  for (const name of names) {
    const payload = fixture(name);
    const classification = classify(payload);
    const expected = EXPECTED[name];
    assert.equal(classification.kind, expected[0], name + ' kind');
    assert.equal(classification.category, expected[1], name + ' category');
    assert.equal(classification.outcome, expected[2], name + ' outcome');
    assert.equal(classification.evidence, expected[3], name + ' evidence');

    const record = buildRecord(payload, CTX);
    assert.deepEqual(Object.keys(record), [...RECORD_KEYS], name + ' key set and order');
    assert.deepEqual(validateRecord(record, { requireSequence: false }), [], name + ' fails the record contract');
    assert.equal(record.hydration, false, name + ' is a live publication, never a replay');
    assert.equal(record.harnessId, 'claude');
    assert.equal(record.schemaVersion, 1);
    assert.equal(record.eventAtUtc, record.observedAtUtc);
    assert.ok(Buffer.byteLength(JSON.stringify(record), 'utf8') <= 64 * 1024);
  }
});

test('child lifecycle hooks cannot end or restart their parent session turn', () => {
  const parent = buildRecord(fixture('user-prompt-submit.json'), CTX);
  for (const name of ['subagent-start.json', 'subagent-stop.json']) {
    const child = buildRecord(fixture(name), CTX);
    assert.equal(child.sessionId, parent.sessionId);
    assert.equal(child.kind, 'heartbeat', name + ' must not mutate parent lifecycle');
    assert.equal(child.outcome, null, name + ' must not announce parent completion');
  }
});

test('questions from the same agent retain independent request identities', () => {
  const question = { session_id: 'parent', agent_id: 'shared-agent', tool_name: 'AskUserQuestion' };
  const first = buildRecord({ ...question, hook_event_name: 'PreToolUse', tool_use_id: 'call-one' }, CTX);
  const second = buildRecord({ ...question, hook_event_name: 'PreToolUse', tool_use_id: 'call-two' }, CTX);
  const answer = buildRecord({ ...question, hook_event_name: 'PostToolUse', tool_use_id: 'call-one' }, CTX);
  assert.equal(first.requestId, 'call-one');
  assert.equal(second.requestId, 'call-two');
  assert.equal(answer.requestId, first.requestId);
  assert.notEqual(answer.requestId, second.requestId);
});

test('correlation ids come only from documented fields', () => {
  const tool = buildRecord(fixture('pre-tool-use.json'), CTX);
  assert.equal(tool.callId, 'fixture-tool-use-0001');
  assert.equal(tool.turnId, 'fixture-prompt-0001');
  assert.equal(tool.requestId, null);
  assert.equal(tool.projectRoot, '/home/fixture-user/example-project');

  // PermissionRequest is documented without tool_use_id: no invented call id.
  const request = buildRecord(fixture('permission-request.json'), CTX);
  assert.equal(request.callId, null);
  assert.equal(request.evidence, 'inferred');

  const subagent = buildRecord(fixture('subagent-start.json'), CTX);
  assert.equal(subagent.requestId, 'fixture-agent-0001');

  const display = buildRecord(fixture('message-display.json'), CTX);
  assert.equal(display.turnId, 'fixture-turn-0001');

  const elicitation = buildRecord(fixture('elicitation.json'), CTX);
  assert.equal(elicitation.requestId, 'fixture-elicitation-0001');

  const unknown = buildRecord(fixture('unknown-event.json'), CTX);
  assert.equal(unknown.evidence, 'unknown');
  assert.equal(unknown.category, 'unknown');

  const noSession = buildRecord({ hook_event_name: 'Stop' }, CTX);
  assert.equal(noSession.sessionId, 'unknown');
  assert.equal(noSession.evidence, 'unknown');
});

test('records never carry message content, tool data, transcripts or prompts', () => {
  for (const name of fixtureNames()) {
    const record = buildRecord(fixture(name), CTX);
    for (const value of collectStrings(record)) {
      assert.ok(!value.includes('SANITIZED'), name + ' leaked fixture content: ' + value);
      assert.ok(!value.includes('transcript'), name + ' leaked a transcript path');
      assert.ok(!value.includes('scratchpad'), name + ' leaked a scratchpad path');
    }
  }
});

test('duplicate publication keeps the same eventId; distinct calls stay distinct', async (t) => {
  const spool = tempDir();
  t.after(() => removeDir(spool));
  const env = spoolEnv(spool);
  const payload = fixture('post-tool-use.json');

  const first = await runBridge(payload, { env });
  const second = await runBridge(payload, { env });
  assert.equal(first.status, 0);
  assert.equal(second.status, 0);

  const dir = instanceDirectory(spool);
  const duplicates = readRecords(dir);
  assert.equal(duplicates.length, 2);
  assert.equal(duplicates[0].eventId, duplicates[1].eventId, 'duplicate publication must be idempotent');
  assert.notEqual(duplicates[0].sequence, duplicates[1].sequence, 'each publication is still ingested once');

  await runBridge({ ...payload, tool_use_id: 'fixture-tool-use-9999' }, { env });
  const records = readRecords(dir);
  assert.notEqual(records[2].eventId, records[0].eventId, 'a different call is a different event');
  assert.equal(new Set(records.map((record) => record.eventId)).size, 2);
});

test('sourceEpoch is stable across invocations and stored once', async (t) => {
  const spool = tempDir();
  t.after(() => removeDir(spool));
  const env = spoolEnv(spool);

  assert.equal((await runBridge(fixture('session-start.json'), { env })).status, 0);
  const dir = instanceDirectory(spool);
  const epochFile = readSourceEpoch(dir);

  assert.equal((await runBridge(fixture('stop.json'), { env })).status, 0);
  const records = readRecords(dir);

  assert.equal(records.length, 2);
  assert.equal(records[0].sourceEpoch, records[1].sourceEpoch);
  assert.equal(records[0].sourceEpoch, epochFile.sourceEpoch);
  assert.equal(records[0].sourceInstanceId, records[1].sourceInstanceId);
  assert.equal(readSourceEpoch(dir).sourceEpoch, epochFile.sourceEpoch, 'the epoch file is never rewritten');
  assert.equal(readSourceEpoch(dir).createdAtUtc, epochFile.createdAtUtc);
});

test('the end-to-end spool line is bounded and contract-valid', async (t) => {
  const spool = tempDir();
  t.after(() => removeDir(spool));
  const env = spoolEnv(spool);

  for (const name of ['session-start.json', 'pre-tool-use.json', 'post-tool-use-failure.json', 'permission-request.json', 'stop.json']) {
    const result = await runBridge(fixture(name), { env });
    assert.equal(result.status, 0, name + ' exited non-zero');
    assert.equal(result.stdout, '', name + ' wrote to stdout');
  }

  const dir = instanceDirectory(spool);
  const raw = fs.readFileSync(path.join(dir, 'events.jsonl'), 'utf8');
  const lines = raw.split('\n').filter(Boolean);
  assert.equal(lines.length, 5);
  for (const line of lines) {
    assert.ok(Buffer.byteLength(line, 'utf8') <= 64 * 1024);
    const record = JSON.parse(line);
    assert.deepEqual(validateRecord(record), []);
  }
  assert.equal(lines.join('\n').includes('SANITIZED'), false);
});

test('every failure counter the bridge can emit is an allowlisted counter', () => {
  // A counter name that is not in HEALTH_COUNTERS is silently discarded by
  // bumpHealth, which turns a lost record into an invisible one. Guard the whole
  // class of bug structurally instead of trusting the two lists to agree.
  const source = fs.readFileSync(new URL('../bridge.mjs', import.meta.url), 'utf8');
  const names = [...source.matchAll(/'((?:dropped|recovered|unknown)[A-Za-z]+)'/g)].map((match) => match[1]);
  assert.ok(names.length >= 8, 'expected the bridge to name its failure counters');
  for (const name of new Set(names)) {
    assert.ok(HEALTH_COUNTERS.includes(name), name + ' must be an allowlisted health counter');
  }
});

test('a failed append consumes no sequence number and is reported as a write failure', async (t) => {
  const spool = tempDir();
  t.after(() => removeDir(spool));
  const dir = path.join(spool, 'claude', 'cc-write-failure');
  fs.mkdirSync(dir, { recursive: true });

  const record = buildRecord(fixture('stop.json'), CTX);
  assert.ok(record);

  // First append succeeds and takes sequence 1.
  const first = publishRecord({ dir, record, deadline: new Deadline(250) });
  assert.equal(first.ok, true);
  assert.equal(first.sequence, 1);

  // Make the event path unappendable while keeping the landed line: the append
  // fails inside the lock.
  const eventsPath = path.join(dir, 'events.jsonl');
  const keptPath = eventsPath + '.kept';
  fs.renameSync(eventsPath, keptPath);
  fs.mkdirSync(eventsPath);

  const second = publishRecord({ dir, record, deadline: new Deadline(250) });
  assert.equal(second.ok, false);
  assert.equal(second.reason, 'write-failed');

  // The failed append must not have consumed sequence 2: the next successful
  // record takes it, so the published sequence stays gap-free.
  fs.rmdirSync(eventsPath);
  fs.renameSync(keptPath, eventsPath);
  const third = publishRecord({ dir, record, deadline: new Deadline(250) });
  assert.equal(third.ok, true);
  assert.equal(third.sequence, 2, 'a failed append must not consume a sequence number');
  const lines = fs.readFileSync(eventsPath, 'utf8').split('\n').filter(Boolean);
  assert.deepEqual(lines.map((line) => JSON.parse(line).sequence), [1, 2]);
});
