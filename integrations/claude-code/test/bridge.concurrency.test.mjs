import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';

import { fixture, instanceDirectory, readHealthJson, readLines, removeDir, runBridge, tempDir } from '../lib/testkit.mjs';

const spoolEnv = (spoolRoot) => ({ OVERSEER_SPOOL_ROOT: spoolRoot });

function parallelBridgeRuns(payloads, env) {
  return Promise.all(payloads.map((payload) => runBridge(payload, { env, timeoutMs: 40000 })));
}

test('concurrent writers produce whole, non-interleaved lines', async (t) => {
  const spool = tempDir();
  t.after(() => removeDir(spool));

  const writers = 10;
  const payloads = [];
  for (let i = 0; i < writers; i++) {
    payloads.push({
      ...fixture('post-tool-use.json'),
      tool_use_id: 'fixture-concurrent-' + String(i).padStart(4, '0'),
      tool_response: { stdout: 'x'.repeat(2000 + i) },
    });
  }

  const results = await parallelBridgeRuns(payloads, spoolEnv(spool));
  for (const result of results) {
    assert.equal(result.status, 0);
    assert.equal(result.stdout, '');
    assert.equal(result.timedOut, false);
  }

  const dir = instanceDirectory(spool);
  const lines = readLines(dir);
  assert.equal(lines.length, writers, 'every concurrent writer must land exactly one complete line');

  const records = lines.map((line) => JSON.parse(line));
  for (const record of records) {
    assert.equal(record.sessionId, 'fixture-session-0001');
    assert.ok(Buffer.byteLength(JSON.stringify(record), 'utf8') <= 64 * 1024);
  }
  assert.equal(new Set(records.map((record) => record.callId)).size, writers);
  const sequences = records.map((record) => record.sequence);
  assert.deepEqual(sequences, Array.from({ length: writers }, (_, index) => index + 1), 'one session takes 1..N in ingestion order');
  assert.equal(readHealthJson(dir).counters.recordsWritten, writers);
});

test('sequence is monotonic per session under concurrency', async (t) => {
  const spool = tempDir();
  t.after(() => removeDir(spool));

  const perSession = { 'fixture-session-A': 5, 'fixture-session-B': 4 };
  const payloads = [];
  for (const [sessionId, count] of Object.entries(perSession)) {
    for (let i = 0; i < count; i++) {
      payloads.push({
        ...fixture('pre-tool-use.json'),
        session_id: sessionId,
        tool_use_id: sessionId + '-tool-' + i,
      });
    }
  }

  const results = await parallelBridgeRuns(payloads, spoolEnv(spool));
  for (const result of results) assert.equal(result.status, 0);

  const dir = instanceDirectory(spool);
  const records = readLines(dir).map((line) => JSON.parse(line));
  assert.equal(records.length, payloads.length);

  for (const [sessionId, count] of Object.entries(perSession)) {
    // Ingestion order is per session: counters are session-scoped, so the file
    // interleaves sessions while each session stays monotonic on its own.
    const sequences = records.filter((record) => record.sessionId === sessionId).map((record) => record.sequence);
    assert.equal(sequences.length, count);
    assert.deepEqual(sequences, [...sequences].sort((a, b) => a - b), sessionId + ' must be monotonic in ingestion order');
    assert.deepEqual(sequences, Array.from({ length: count }, (_, index) => index + 1), sessionId + ' must take 1..N');
  }
});

test('a held writer lock is abandoned inside the 250 ms write budget', async (t) => {
  const spool = tempDir();
  t.after(() => removeDir(spool));
  const env = spoolEnv(spool);

  assert.equal((await runBridge(fixture('stop.json'), { env })).status, 0);
  const dir = instanceDirectory(spool);
  const lockPath = path.join(dir, 'events.lock');
  fs.writeFileSync(lockPath, 'held by the test\n', 'utf8');

  const blocked = await runBridge(fixture('pre-tool-use.json'), { env });
  assert.equal(blocked.status, 0, 'a contended spool still fails open');
  assert.equal(blocked.stdout, '');
  assert.ok(blocked.durationMs >= 150, 'expected the bridge to wait for the lock, took ' + blocked.durationMs + ' ms');
  assert.ok(blocked.durationMs < 2000, 'the write budget must bound the wait, took ' + blocked.durationMs + ' ms');
  assert.equal(readLines(dir).length, 1, 'the record is dropped rather than queued');
  assert.equal(readHealthJson(dir).counters.droppedLockTimeout, 1, 'the drop is counted');

  fs.rmSync(lockPath, { force: true });
  assert.equal((await runBridge(fixture('pre-tool-use.json'), { env })).status, 0);
  assert.equal(readLines(dir).length, 2, 'the next invocation writes normally');
});

test('the sequence counter is per session and starts at one', async (t) => {
  const spool = tempDir();
  t.after(() => removeDir(spool));
  const env = spoolEnv(spool);

  await runBridge({ ...fixture('stop.json'), session_id: 'fixture-session-A' }, { env });
  await runBridge({ ...fixture('stop.json'), session_id: 'fixture-session-B' }, { env });
  await runBridge({ ...fixture('stop.json'), session_id: 'fixture-session-A' }, { env });

  const dir = instanceDirectory(spool);
  const records = readLines(dir).map((line) => JSON.parse(line));
  const sessionA = records.filter((record) => record.sessionId === 'fixture-session-A').map((record) => record.sequence);
  const sessionB = records.filter((record) => record.sessionId === 'fixture-session-B').map((record) => record.sequence);
  assert.deepEqual(sessionA, [1, 2]);
  assert.deepEqual(sessionB, [1]);
});
