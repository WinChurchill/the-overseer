import assert from 'node:assert/strict';
import test from 'node:test';

import { buildRecord, hostSessionIdFromEnv, validateRecord } from '../lib/mapping.mjs';

const ctx = { sourceInstanceId: 'cc-test', sourceEpoch: 'epoch-test', observedAtUtc: '2026-11-14T12:00:00.000Z' };
const payload = { hook_event_name: 'UserPromptSubmit', session_id: '11111111-2222-3333-4444-555555555555', cwd: 'C:\work' };

test('desktop sessions record the desktop id', () => {
  const id = hostSessionIdFromEnv({ CLAUDE_CODE_ENTRYPOINT: 'claude-desktop', CLAUDE_CODE_HOST_SESSION_ID: 'local_0818420e-ff5b-4789-a3be-05bc084bc1c6' });
  assert.equal(id, 'local_0818420e-ff5b-4789-a3be-05bc084bc1c6');
  const record = buildRecord(payload, { ...ctx, hostSessionId: id });
  assert.equal(record.hostSessionId, id);
  assert.deepEqual(validateRecord(record, { requireSequence: false }), []);
});

test('other launchers and malformed ids record nothing', () => {
  assert.equal(hostSessionIdFromEnv({ CLAUDE_CODE_ENTRYPOINT: 'cli', CLAUDE_CODE_HOST_SESSION_ID: 'local_abc' }), null);
  assert.equal(hostSessionIdFromEnv({ CLAUDE_CODE_ENTRYPOINT: 'claude-desktop' }), null);
  for (const bad of ['local_', 'local_a/b', 'claude://evil', 'local_' + 'a'.repeat(65), 'LOCAL_abc', 'local_abc\n']) {
    assert.equal(hostSessionIdFromEnv({ CLAUDE_CODE_ENTRYPOINT: 'claude-desktop', CLAUDE_CODE_HOST_SESSION_ID: bad }), null, bad);
  }
  const record = buildRecord(payload, { ...ctx, hostSessionId: 'claude://evil' });
  assert.equal(record.hostSessionId, null);
  assert.deepEqual(validateRecord(record, { requireSequence: false }), []);
});

test('only the two documented variables are read', () => {
  const touched = new Set();
  const env = new Proxy({ CLAUDE_CODE_ENTRYPOINT: 'claude-desktop', CLAUDE_CODE_HOST_SESSION_ID: 'local_abc', CLAUDE_CODE_MESSAGING_TOKEN: 'secret' }, {
    get(target, key) { touched.add(String(key)); return target[key]; },
  });
  hostSessionIdFromEnv(env);
  assert.deepEqual([...touched].sort(), ['CLAUDE_CODE_ENTRYPOINT', 'CLAUDE_CODE_HOST_SESSION_ID']);
});
