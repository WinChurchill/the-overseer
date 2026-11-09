import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { BRIDGE_PATH, fixture, instanceDirectory, readRecords, removeDir, runBridge, tempDir } from '../lib/testkit.mjs';

const payload = fixture('stop.json');

function setup(t) {
  const root = tempDir();
  t.after(() => removeDir(root));
  const spool = path.join(root, 'spool');
  const outside = path.join(root, 'outside');
  fs.mkdirSync(outside);
  const env = { OVERSEER_SPOOL_ROOT: spool, OVERSEER_CLAUDE_SOURCE_INSTANCE_ID: 'test' };
  return { spool, outside, env };
}

for (const kind of ['hard link', 'junction']) {
  test('bridge declines an append through a ' + kind + ' and exits cleanly', async (t) => {
    const { spool, outside, env } = setup(t);
    const source = path.join(spool, 'claude', 'test');
    fs.mkdirSync(path.dirname(source), { recursive: true });
    const canary = path.join(outside, 'canary.txt');
    fs.writeFileSync(canary, 'canary\n');
    if (kind === 'junction') fs.symlinkSync(outside, source, 'junction');
    else {
      fs.mkdirSync(source);
      fs.linkSync(canary, path.join(source, 'events.jsonl'));
    }
    try {
      const result = await runBridge(payload, { env });
      assert.equal(result.status, 0);
      assert.equal(result.stdout, '');
      assert.equal(fs.readFileSync(canary, 'utf8'), 'canary\n');
      assert.deepEqual(fs.readdirSync(outside), ['canary.txt']);
    } finally {
      if (kind === 'junction') fs.unlinkSync(source);
    }
  });
}

test('an old-looking lock remains held by its live owner', async (t) => {
  const { spool, env } = setup(t);
  await runBridge(payload, { env });
  const source = instanceDirectory(spool, env);
  const lock = path.join(source, 'events.lock');
  fs.writeFileSync(lock, process.pid + ' 1\n');
  fs.utimesSync(lock, 1, 1);
  const attempt = await runBridge(payload, { env });
  assert.equal(attempt.status, 0);
  assert.equal(attempt.stdout, '');
  assert.equal(readRecords(source).length, 1);
  assert.equal(fs.existsSync(lock), true);
});

test('malformed epoch JSON is repaired and keeps at most one quarantine file', async (t) => {
  const { spool, env } = setup(t);
  await runBridge(payload, { env });
  const source = instanceDirectory(spool, env);
  const epoch = path.join(source, 'source-epoch.json');
  for (let index = 0; index < 2; index++) {
    fs.writeFileSync(epoch, '{"interrupted":');
    const result = await runBridge(payload, { env });
    assert.equal(result.status, 0);
    assert.equal(result.stdout, '');
  }
  const records = readRecords(source);
  assert.equal(records.length, 3);
  assert.equal(new Set(records.map(record => record.sourceEpoch)).size, 3);
  assert.deepEqual(fs.readdirSync(source).filter(file => file.startsWith('source-epoch.json.corrupt')),
    ['source-epoch.json.corrupt']);
});
