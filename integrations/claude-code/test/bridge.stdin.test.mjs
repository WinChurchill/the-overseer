import test from 'node:test';
import assert from 'node:assert/strict';

import { fixture, instanceDirectory, readHealthJson, readRecords, removeDir, runBridge, tempDir } from '../lib/testkit.mjs';

const spoolEnv = (spoolRoot) => ({ OVERSEER_SPOOL_ROOT: spoolRoot });

test('oversized stdin (> 1 MiB) fails open: exit 0, empty stdout, no record', async (t) => {
  const spool = tempDir();
  t.after(() => removeDir(spool));

  const padding = 'x'.repeat(1024 * 1024 + 4096);
  const input = '{"session_id":"fixture-oversize","hook_event_name":"Stop","padding":"' + padding + '"}';
  assert.ok(Buffer.byteLength(input, 'utf8') > 1024 * 1024, 'fixture input must exceed 1 MiB');

  const result = await runBridge(input, { env: spoolEnv(spool) });

  assert.equal(result.status, 0);
  assert.equal(result.stdout, '');
  assert.equal(result.timedOut, false);
  const dir = instanceDirectory(spool);
  assert.equal(readRecords(dir).length, 0, 'oversized payload must not be recorded');
  assert.equal(readHealthJson(dir).counters.droppedOversizeInput, 1);
});

test('stalled stdin hits the 2 s deadline: exit 0, empty stdout, no record', async (t) => {
  const spool = tempDir();
  t.after(() => removeDir(spool));

  const partial = '{"session_id":"fixture-stall","hook_event_name":"Stop"';
  const result = await runBridge(partial, { env: spoolEnv(spool), endStdin: false, timeoutMs: 20000, clock: 'wall' });

  assert.equal(result.status, 0);
  assert.equal(result.stdout, '');
  assert.equal(result.timedOut, false, 'the bridge must end the hook itself, not be killed by the test');
  assert.ok(result.durationMs >= 1900, 'expected the 2 s stdin deadline, took ' + result.durationMs + ' ms');
  assert.ok(result.durationMs < 6000, 'hook must not stall the harness far past its deadline: ' + result.durationMs + ' ms');

  const dir = instanceDirectory(spool);
  assert.equal(readRecords(dir).length, 0);
  assert.equal(readHealthJson(dir).counters.droppedStdinDeadline, 1);
});

test('malformed and non-object stdin fail open', async (t) => {
  const spool = tempDir();
  t.after(() => removeDir(spool));
  const env = spoolEnv(spool);

  const truncated = await runBridge('{"session_id": "fixture-malformed", ', { env });
  assert.equal(truncated.status, 0);
  assert.equal(truncated.stdout, '');

  const garbage = await runBridge('this is not json at all', { env });
  assert.equal(garbage.status, 0);
  assert.equal(garbage.stdout, '');

  const arrayPayload = await runBridge('[1,2,3]', { env });
  assert.equal(arrayPayload.status, 0);
  assert.equal(arrayPayload.stdout, '');

  const empty = await runBridge('', { env });
  assert.equal(empty.status, 0);
  assert.equal(empty.stdout, '');

  const dir = instanceDirectory(spool);
  assert.equal(readRecords(dir).length, 0);
  const health = readHealthJson(dir).counters;
  assert.equal(health.droppedMalformedInput, 2);
  assert.equal(health.droppedUnsupportedPayload, 1);
  assert.equal(health.droppedEmptyInput, 1);
});

test('a documented payload produces exactly one record and never writes to stdout', async (t) => {
  const spool = tempDir();
  t.after(() => removeDir(spool));

  const result = await runBridge(fixture('post-tool-use.json'), { env: { ...spoolEnv(spool), OVERSEER_CLAUDE_BRIDGE_DEBUG: '1' } });

  assert.equal(result.status, 0);
  assert.equal(result.stdout, '', 'stdout must stay empty even with debug enabled');
  const dir = instanceDirectory(spool);
  const records = readRecords(dir);
  assert.equal(records.length, 1);
  assert.equal(records[0].kind, 'tool/end');
  assert.match(result.stderr, /overseer-claude-bridge/, 'debug diagnostics belong on stderr');
});

test('health counters stay bounded and payload-free', async (t) => {
  const spool = tempDir();
  t.after(() => removeDir(spool));
  const env = spoolEnv(spool);

  await runBridge('nonsense', { env });
  await runBridge('nonsense', { env });
  const dir = instanceDirectory(spool);
  const health = readHealthJson(dir);

  assert.ok(health.counters.droppedMalformedInput >= 2);
  assert.ok(Object.keys(health.counters).length <= 32, 'health counters must stay bounded');
  assert.ok(!JSON.stringify(health).includes('nonsense'), 'health must never contain payload text');
});
