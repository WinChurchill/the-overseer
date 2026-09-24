import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';

import { RECORD_KEYS, buildRecord, classify, validateRecord } from '../lib/mapping.mjs';
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

const CTX = {
  sourceInstanceId: 'cc-fixture-instance',
  sourceEpoch: 'epoch-20260924T000000Z-deadbeef',
  observedAtUtc: '2026-09-24T00:00:00.000Z',
  producerVersion: 'overseer-claude-bridge/1.0.0',
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
  'permission-denied.json': ['approval/decided', 'working', 'aborted', 'explicit'],
  'notification-permission-prompt.json': ['approval/asked', 'waiting', null, 'inferred'],
  'notification-idle.json': ['heartbeat', 'idle', null, 'inferred'],
  'notification-agent-needs-input.json': ['input/asked', 'waiting', null, 'inferred'],
  'subagent-start.json': ['turn/start', 'working', null, 'explicit'],
  'subagent-stop.json': ['turn/end', 'complete', 'completed', 'explicit'],
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
