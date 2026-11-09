import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';

import { buildRecord } from '../lib/mapping.mjs';
import { Deadline, EVENT_FILE_BYTES, publishRecord } from '../lib/spool.mjs';
import { fixture, instanceDirectory, readHealthJson, readRecords, removeDir, runBridge, tempDir } from '../lib/testkit.mjs';

test('a boundary emoji is truncated and inherited event keys count as unknown', async (t) => {
  const root = tempDir();
  t.after(() => removeDir(root));
  const env = { OVERSEER_SPOOL_ROOT: root };
  const base = 'C:/' + 'a'.repeat(508);
  const payload = fixture('stop.json');
  for (const input of [
    { ...payload, cwd: base + '😀tail' },
    { ...payload, hook_event_name: 'toString' },
    { ...payload, hook_event_name: 'Notification', notification_type: '__proto__' },
  ]) {
    const result = await runBridge(input, { env });
    assert.equal(result.status, 0);
    assert.equal(result.stdout, '');
  }
  const dir = instanceDirectory(root);
  const records = readRecords(dir);
  assert.deepEqual(records.map(record => record.kind), ['turn/end', 'heartbeat', 'heartbeat']);
  assert.equal(records[0].projectRoot, base);
  assert.equal(readHealthJson(dir).counters.unknownEvents, 2);
  assert.equal(readHealthJson(dir).counters.droppedInternalError ?? 0, 0);
});

test('missing and corrupt sequence counters recover without rewinding another session', async (t) => {
  const root = tempDir();
  t.after(() => removeDir(root));
  const env = { OVERSEER_SPOOL_ROOT: root };
  const payload = fixture('stop.json');
  const emit = (id) => runBridge({ ...payload, session_id: id }, { env });
  await emit('affected');
  await emit('peer');
  await emit('affected');
  const dir = instanceDirectory(root);
  const counters = path.join(dir, 'sessions');
  const counter = path.join(counters, fs.readdirSync(counters).find(name => /^affected-.*\.seq$/.test(name)));
  fs.unlinkSync(counter);
  await emit('affected');
  fs.writeFileSync(counter, '1-corrupt');
  await emit('affected');
  await emit('peer');
  // Simulate an upgrade from a legacy spool whose only counter was already lost.
  fs.unlinkSync(counter);
  fs.unlinkSync(counter + '.recovery');
  await emit('affected');
  const records = readRecords(dir);
  assert.deepEqual(records.filter(record => record.sessionId === 'affected').map(record => record.sequence), [1, 2, 3, 4, 5]);
  assert.deepEqual(records.filter(record => record.sessionId === 'peer').map(record => record.sequence), [1, 2]);
  assert.equal(new Set(records.map(record => record.sourceEpoch)).size, 1);
  assert.equal(readHealthJson(dir).counters.recoveredCorruptFile, 3);
});

test('the writer rotates in place and bounds oversized legacy history on its next append', (t) => {
  const dir = tempDir();
  t.after(() => removeDir(dir));
  const record = buildRecord(fixture('stop.json'), {
    sourceInstanceId: 'test', sourceEpoch: 'test-epoch', observedAtUtc: '2026-11-08T00:00:00Z',
  });
  const publish = () => publishRecord({ dir, record, deadline: new Deadline(250) });
  const events = path.join(dir, 'events.jsonl');
  const previous = path.join(dir, 'events-previous.jsonl');
  const unrelated = path.join(dir, 'events-user-owned.jsonl');
  fs.writeFileSync(unrelated, 'untouched');

  assert.equal(publish().sequence, 1);
  const legacy = fs.readFileSync(events, 'utf8');
  fs.writeFileSync(events, 'x'.repeat(EVENT_FILE_BYTES) + '\n' + legacy);
  assert.equal(publish().sequence, 2);
  assert.equal(fs.readFileSync(previous, 'utf8'), legacy);
  assert.equal(readRecords(dir)[0].sequence, 2);

  fs.truncateSync(events, EVENT_FILE_BYTES);
  assert.equal(publish().sequence, 3);
  assert.equal(fs.statSync(previous).size, EVENT_FILE_BYTES);
  assert.deepEqual(readRecords(dir).map(event => event.sequence), [3]);
  assert.equal(fs.readFileSync(unrelated, 'utf8'), 'untouched');
});

test('an interrupted append and a failed primary checkpoint cannot hide the next event', (t) => {
  const dir = tempDir();
  t.after(() => removeDir(dir));
  const record = buildRecord(fixture('stop.json'), {
    sourceInstanceId: 'test', sourceEpoch: 'test-epoch', observedAtUtc: '2026-11-08T00:00:00Z',
  });
  const publish = () => publishRecord({ dir, record, deadline: new Deadline(250) });
  assert.equal(publish().sequence, 1);
  const rename = fs.renameSync;
  const failingCommit = t.mock.method(fs, 'renameSync', (from, to) => {
    if (to.endsWith('.seq')) throw new Error('simulated primary checkpoint failure');
    return rename(from, to);
  });
  const second = publish();
  assert.equal(second.ok, true);
  assert.equal(second.sequence, 2);
  failingCommit.mock.restore();

  const events = path.join(dir, 'events.jsonl');
  fs.appendFileSync(events, '{"interrupted":');
  assert.equal(publish().sequence, 3);
  const lines = fs.readFileSync(events, 'utf8').trimEnd().split('\n');
  assert.equal(lines.length, 4);
  assert.equal(lines[2], '{"interrupted":');
  assert.deepEqual([lines[0], lines[1], lines[3]].map(line => JSON.parse(line).sequence), [1, 2, 3]);
});
