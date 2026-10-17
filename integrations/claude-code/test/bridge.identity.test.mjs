import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { pathToFileURL } from 'node:url';
import { setTimeout as delay } from 'node:timers/promises';
import { ensureSourceInstance } from '../lib/spool.mjs';
import { INTEGRATION_ROOT, removeDir, runNode, tempDir } from '../lib/testkit.mjs';

test('a concurrent first attach never reads a partially published source identity', async (t) => {
  const root = tempDir();
  t.after(() => removeDir(root));
  const spool = path.join(root, 'spool');
  const signal = path.join(root, 'writing');
  const worker = path.join(root, 'slow-writer.mjs');
  const moduleUrl = pathToFileURL(path.join(INTEGRATION_ROOT, 'lib/spool.mjs')).href;
  // Slow only an owned synthetic identity write. The observer must see either
  // no destination or a complete document while another creator is writing.
  fs.writeFileSync(worker, `
import fs from 'node:fs';
const original = fs.writeFileSync;
fs.writeFileSync = (file, value, options) => {
  if (String(file).includes('instance.json') && options?.flag === 'wx') {
    original(file, '{', options);
    original(${JSON.stringify(signal)}, 'ready');
    Atomics.wait(new Int32Array(new SharedArrayBuffer(4)), 0, 0, 150);
    return original(file, value, { ...options, flag: 'w' });
  }
  return original(file, value, options);
};
const { ensureSourceInstance } = await import(${JSON.stringify(moduleUrl)});
console.log(JSON.stringify(ensureSourceInstance(${JSON.stringify(spool)})));
`);
  const pending = runNode(worker, [], { timeoutMs: 20000 });
  try {
    const deadline = Date.now() + 10000;
    while (!fs.existsSync(signal) && Date.now() < deadline) await delay(5);
    assert.ok(fs.existsSync(signal), 'the slow synthetic creator reached its write');
    const identity = path.join(spool, 'claude/instance.json');
    if (fs.existsSync(identity)) assert.doesNotThrow(() => JSON.parse(fs.readFileSync(identity, 'utf8')),
      'the shared identity is never visible as a partial document');
    const peer = ensureSourceInstance(spool);
    assert.ok(peer.sourceInstanceId, 'a simultaneous creator receives a stable identity');
    const result = await pending;
    assert.equal(result.status, 0, result.stderr);
    const first = JSON.parse(result.stdout);
    assert.equal(first.sourceInstanceId, peer.sourceInstanceId, 'both invocations use the winner identity');
    assert.equal(JSON.parse(fs.readFileSync(identity, 'utf8')).sourceInstanceId, peer.sourceInstanceId);
    assert.equal(fs.readdirSync(path.dirname(identity)).filter(name => name.includes('.tmp-')).length, 0);
  } finally {
    await pending; // do not remove the fixture while its owned child is running
  }
});
