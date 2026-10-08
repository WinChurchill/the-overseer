import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { pathToFileURL } from 'node:url';
import { BRIDGE_PATH, fixture, instanceDirectory, readHealthJson, readLines, removeDir, runBridge, runNode, tempDir } from '../lib/testkit.mjs';

test('slow identity acquisition reserves time to account for a contended writer drop', async (t) => {
  const root = tempDir();
  t.after(() => removeDir(root));
  const env = { OVERSEER_SPOOL_ROOT: root };
  assert.equal((await runBridge(fixture('stop.json'), { env })).status, 0);
  const dir = instanceDirectory(root);
  fs.writeFileSync(path.join(dir, 'events.lock'), 'synthetic held lock');
  const worker = path.join(root, 'slow-acquisition.mjs');
  fs.writeFileSync(worker, `
import fs from 'node:fs';
const original = fs.mkdirSync;
let delayed = false;
fs.mkdirSync = (...args) => {
  if (!delayed && String(args[0]).replaceAll('\\\\', '/').endsWith('/claude')) {
    delayed = true;
    Atomics.wait(new Int32Array(new SharedArrayBuffer(4)), 0, 0, 110);
  }
  return original(...args);
};
await import(${JSON.stringify(pathToFileURL(BRIDGE_PATH).href)});
`);
  const result = await runNode(worker, [], { env, input: JSON.stringify(fixture('pre-tool-use.json')) });
  assert.equal(result.status, 0, result.stderr);
  assert.equal(result.stdout, '');
  assert.equal(readLines(dir).length, 1, 'the held lock drops the second record');
  assert.equal(readHealthJson(dir).counters.droppedLockTimeout, 1,
    'the entire remaining parent budget must not be consumed before health accounting');
});
