import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { pathToFileURL } from 'node:url';
import { BRIDGE_PATH, fixture, instanceDirectory, readHealthJson, readLines, removeDir, runBridge, runNode, tempDir } from '../lib/testkit.mjs';
import { beginHealthAccounting, bumpHealth, Deadline, HEALTH_RECEIPT_SLOTS, readHealth } from '../lib/spool.mjs';
import { randomUUID } from 'node:crypto';

// Capacity correctness is independent of machine scheduling. Keep its expected
// counts exact under a virtual clock; the separate real-lock and injected slow-I/O
// tests below exercise deadline exhaustion without requiring 64 filesystem slots
// to compact inside one wall-clock slice on every Windows run.
async function recoverCapacity(root, env) {
  const worker = path.join(root, 'capacity-recovery.mjs');
  fs.writeFileSync(worker, `
const clock = Date.now();
Date.now = () => clock;
await import(${JSON.stringify(pathToFileURL(BRIDGE_PATH).href)});
`);
  return runNode(worker, [], { env, input: JSON.stringify(fixture('stop.json')) });
}

test('accounting sub-deadlines never extend the parent write budget', (t) => {
  let now = 1000;
  t.mock.method(Date, 'now', () => { now += 10; return now; });
  const parent = new Deadline(250);
  now = parent.end - 15;
  const health = parent.child(60);
  const fold = health.child(60);
  assert.ok(health.end <= parent.end);
  assert.ok(fold.end <= health.end);
});

test('expiration before the event lock leaves durable evidence of an unmatched hook', async (t) => {
  const root = tempDir();
  t.after(() => removeDir(root));
  const env = { OVERSEER_SPOOL_ROOT: root };
  await runBridge(fixture('stop.json'), { env });
  const dir = instanceDirectory(root);
  const worker = path.join(root, 'deadline.mjs');
  fs.writeFileSync(worker, [
    "import fs from 'node:fs';",
    'let now = Date.now();',
    'Date.now = () => now;',
    'const stat = fs.lstatSync;',
    "fs.lstatSync = (file, ...args) => { if (String(file).endsWith('sessions')) now += 251; return stat(file, ...args); };",
    'await import(' + JSON.stringify(pathToFileURL(BRIDGE_PATH).href) + ');',
  ].join('\n'));
  const result = await runNode(worker, [], { env, input: JSON.stringify(fixture('stop.json')) });
  assert.equal(result.status, 0);
  assert.equal(result.stdout, '');
  assert.equal(readLines(dir).length, 1);
  assert.equal(readHealth(dir).counters.recordsWritten, 1);
  assert.equal(readHealth(dir).accountingIncomplete, true);
});

test('cold failure-accounting setup uses the parent budget before the health slice', async (t) => {
  const root = tempDir();
  t.after(() => removeDir(root));
  const env = { OVERSEER_SPOOL_ROOT: root };
  const worker = path.join(root, 'slow-cold-health-directory.mjs');
  fs.writeFileSync(worker, `
import fs from 'node:fs';
let now = 1000;
Date.now = () => now;
const mkdir = fs.mkdirSync;
let delayed = false;
fs.mkdirSync = (...args) => {
  if (!delayed && String(args[0]).replaceAll('\\\\', '/').endsWith('/health-pending') && !fs.existsSync(args[0])) {
    delayed = true;
    now += 70; // Cold directory setup exceeds 60 ms, but not the 250 ms parent.
  }
  return mkdir(...args);
};
await import(${JSON.stringify(pathToFileURL(BRIDGE_PATH).href)});
`);
  for (let i = 0; i < 2; i++) {
    const result = await runNode(worker, [], { env, input: 'synthetic malformed JSON' });
    assert.equal(result.status, 0);
    assert.equal(result.stdout, '');
    assert.equal(result.timedOut, false);
  }
  const health = readHealthJson(instanceDirectory(root));
  assert.equal(health?.counters.droppedMalformedInput, 2);
  assert.equal(health.accountingIncomplete, false);
});

test('concurrent dropped writers remain accounted while the health lock is held', async (t) => {
  const root = tempDir();
  t.after(() => removeDir(root));
  const env = { OVERSEER_SPOOL_ROOT: root };
  assert.equal((await runBridge(fixture('stop.json'), { env })).status, 0);
  const dir = instanceDirectory(root);
  for (const name of ['events.lock', 'health.lock']) fs.writeFileSync(path.join(dir, name), 'owned lock\n');
  const results = await Promise.all(Array.from({ length: 9 }, () => runBridge(fixture('pre-tool-use.json'), { env })));
  for (const result of results) {
    assert.equal(result.status, 0);
    assert.equal(result.stdout, '');
    assert.equal(result.timedOut, false);
  }
  assert.equal(readLines(dir).length, 1);
  assert.equal(readHealth(dir).counters.droppedLockTimeout, 9);
  assert.equal(readHealth(dir).accountingIncomplete, false);
});

test('retained receipts are not counted again after summary commit or slot reuse', async (t) => {
  const root = tempDir();
  t.after(() => removeDir(root));
  const env = { OVERSEER_SPOOL_ROOT: root };
  const worker = path.join(root, 'retain-receipts.mjs');
  fs.writeFileSync(worker, `
import fs from 'node:fs';
const unlink = fs.unlinkSync;
fs.unlinkSync = file => {
  if (String(file).replaceAll('\\\\', '/').match(/health-pending\\/\\d+\\.json$/)) throw new Error('owned cleanup failure');
  return unlink(file);
};
await import(${JSON.stringify(pathToFileURL(BRIDGE_PATH).href)});
`);
  assert.equal((await runBridge(fixture('stop.json'), { env })).status, 0);
  const dir = instanceDirectory(root);
  const pending = path.join(dir, 'health-pending');
  // An uncontended hook commits directly, so stand in for three contended
  // publishers whose receipts a fold commits but then fails to delete.
  for (let slot = 0; slot < 3; slot++) {
    fs.writeFileSync(path.join(pending, String(slot).padStart(2, '0') + '.json'),
      JSON.stringify({ id: randomUUID(), counters: ['recordsWritten'] }));
  }
  for (let i = 1; i <= 2; i++) {
    const result = await runNode(worker, [], { env, input: JSON.stringify(fixture('stop.json')) });
    assert.equal(result.status, 0);
    assert.equal(readHealth(dir).counters.recordsWritten, 4 + i, 'a retained committed receipt is counted once');
  }
  const retained = fs.readdirSync(pending).filter(name => name.endsWith('.json'));
  assert.equal(retained.length, 3, 'the injected cleanup failure must leave committed receipts');
  // Reuse one already-committed slot, as a later publisher does after cleanup.
  fs.writeFileSync(path.join(pending, retained[0]), JSON.stringify({ id: randomUUID(), counters: ['droppedLockTimeout'] }));
  assert.equal(readHealth(dir).counters.recordsWritten, 6);
  assert.equal(readHealth(dir).counters.droppedLockTimeout, 1);
  assert.equal((await runBridge(fixture('stop.json'), { env })).status, 0);
  assert.equal(readHealth(dir).counters.recordsWritten, 7);
  assert.equal(readHealth(dir).counters.droppedLockTimeout, 1);
  assert.equal(fs.readdirSync(pending).filter(name => name.endsWith('.json')).length, 0);
});

test('receipt capacity stays bounded and explicitly reports incomplete accounting', async (t) => {
  const root = tempDir();
  t.after(() => removeDir(root));
  const env = { OVERSEER_SPOOL_ROOT: root };
  assert.equal((await runBridge(fixture('stop.json'), { env })).status, 0);
  const dir = instanceDirectory(root);
  const pending = path.join(dir, 'health-pending');
  fs.writeFileSync(path.join(dir, 'health.lock'), 'owned lock\n');
  for (let slot = 0; slot < HEALTH_RECEIPT_SLOTS; slot++) {
    fs.writeFileSync(path.join(pending, String(slot).padStart(2, '0') + '.json'),
      JSON.stringify({ id: randomUUID(), counters: ['droppedLockTimeout'] }));
  }
  const result = await runBridge(fixture('stop.json'), { env });
  assert.equal(result.status, 0);
  assert.equal(result.stdout, '');
  assert.equal(result.timedOut, false);
  assert.equal(readHealth(dir).accountingIncomplete, true);
  assert.equal(readHealth(dir).counters.droppedLockTimeout, HEALTH_RECEIPT_SLOTS);
  assert.equal(fs.readdirSync(pending).length, HEALTH_RECEIPT_SLOTS);
  fs.unlinkSync(path.join(dir, 'health.lock'));
  assert.equal((await recoverCapacity(root, env)).status, 0);
  assert.equal(readHealth(dir).counters.recordsWritten, 2, 'publishing resumes after compaction frees capacity');
  assert.equal(readHealth(dir).counters.droppedLockTimeout, HEALTH_RECEIPT_SLOTS);
  assert.equal(readHealth(dir).counters.droppedUnaccounted, 1, 'the earlier missing count stays explicit as one counted episode');
  assert.equal(readHealth(dir).accountingIncomplete, false, 'a resolved episode must not hold the source incomplete forever');
  assert.equal(fs.existsSync(path.join(dir, 'health-incomplete')), false);
  assert.equal((await runBridge(fixture('stop.json'), { env })).status, 0);
  assert.equal(readHealth(dir).counters.droppedUnaccounted, 1, 'a counted episode is never counted again');
});

test('an abandoned intent becomes one counted episode and then clears', async (t) => {
  const root = tempDir();
  t.after(() => removeDir(root));
  const env = { OVERSEER_SPOOL_ROOT: root };
  assert.equal((await runBridge(fixture('stop.json'), { env })).status, 0);
  const dir = instanceDirectory(root);
  const intent = path.join(dir, 'health-pending', '07.json.intent');
  // A process ID that cannot be alive, last touched well past the abandonment age.
  fs.writeFileSync(intent, '2147483646');
  const old = new Date(Date.now() - 60_000);
  fs.utimesSync(intent, old, old);
  assert.equal(readHealth(dir).accountingIncomplete, true);
  assert.equal((await runBridge(fixture('stop.json'), { env })).status, 0);
  const health = readHealth(dir);
  assert.equal(fs.existsSync(intent), false);
  assert.equal(health.counters.droppedUnaccounted, 1);
  assert.equal(health.counters.recordsWritten, 2);
  assert.equal(health.accountingIncomplete, false);
});

test('a live hook\'s intent keeps the marker until that hook resolves', async (t) => {
  const root = tempDir();
  t.after(() => removeDir(root));
  const env = { OVERSEER_SPOOL_ROOT: root };
  assert.equal((await runBridge(fixture('stop.json'), { env })).status, 0);
  const dir = instanceDirectory(root);
  const intent = path.join(dir, 'health-pending', '07.json.intent');
  fs.writeFileSync(intent, String(process.pid));
  fs.writeFileSync(path.join(dir, 'health-incomplete'), '');
  assert.equal((await runBridge(fixture('stop.json'), { env })).status, 0);
  assert.equal(readHealth(dir).accountingIncomplete, true);
  assert.equal(readHealth(dir).counters.droppedUnaccounted, undefined, 'nothing is counted while an outcome is still open');
  fs.unlinkSync(intent);
  assert.equal((await runBridge(fixture('stop.json'), { env })).status, 0);
  assert.equal(readHealth(dir).counters.droppedUnaccounted, 1);
  assert.equal(readHealth(dir).accountingIncomplete, false);
});

test('a corrupt receipt is removed and counted instead of latching forever', async (t) => {
  const root = tempDir();
  t.after(() => removeDir(root));
  const env = { OVERSEER_SPOOL_ROOT: root };
  assert.equal((await runBridge(fixture('stop.json'), { env })).status, 0);
  const dir = instanceDirectory(root);
  const corrupt = path.join(dir, 'health-pending', '03.json');
  fs.writeFileSync(corrupt, '{"id":"not-a-uuid"');
  assert.equal(readHealth(dir).accountingIncomplete, true);
  for (let i = 0; i < 2; i++) assert.equal((await runBridge(fixture('stop.json'), { env })).status, 0);
  const health = readHealth(dir);
  assert.equal(fs.existsSync(corrupt), false);
  assert.equal(health.counters.droppedUnaccounted, 1);
  assert.equal(health.counters.recordsWritten, 3);
  assert.equal(health.accountingIncomplete, false);
});

test('a corrupt receipt survives when its loss marker cannot be persisted', async (t) => {
  const root = tempDir();
  t.after(() => removeDir(root));
  const env = { OVERSEER_SPOOL_ROOT: root };
  assert.equal((await runBridge(fixture('stop.json'), { env })).status, 0);
  const dir = instanceDirectory(root);
  const corrupt = path.join(dir, 'health-pending', '03.json');
  fs.writeFileSync(corrupt, '{"id":"not-a-uuid"');
  const parent = new Deadline(250);
  const intent = beginHealthAccounting(dir, parent);
  const open = fs.openSync;
  t.mock.method(fs, 'openSync', (file, ...args) => {
    if (path.basename(String(file)) === 'health-incomplete') {
      throw Object.assign(new Error('owned marker write failure'), { code: 'EIO' });
    }
    return open(file, ...args);
  });

  bumpHealth(dir, 'recordsWritten', parent, intent);
  assert.equal(fs.existsSync(corrupt), true, 'the only evidence of a lost outcome must remain');
  assert.equal(readHealth(dir).accountingIncomplete, true);
  assert.equal(readHealth(dir).counters.droppedUnaccounted, undefined);

  assert.equal((await runBridge(fixture('stop.json'), { env })).status, 0);
  assert.equal(fs.existsSync(corrupt), false);
  assert.equal(readHealth(dir).counters.droppedUnaccounted, 1);
  assert.equal(readHealth(dir).accountingIncomplete, false);
});

test('excess foreign pending files stay untouched and keep accounting incomplete', async (t) => {
  const root = tempDir();
  t.after(() => removeDir(root));
  const env = { OVERSEER_SPOOL_ROOT: root };
  assert.equal((await runBridge(fixture('stop.json'), { env })).status, 0);
  const dir = instanceDirectory(root);
  const pending = path.join(dir, 'health-pending');
  const foreign = path.join(pending, '00-unrelated-backup.intent');
  fs.writeFileSync(foreign, '2147483646');
  const old = new Date(Date.now() - 60_000);
  fs.utimesSync(foreign, old, old);
  for (let index = 0; index < HEALTH_RECEIPT_SLOTS * 3 + 20; index++) {
    fs.writeFileSync(path.join(pending, 'foreign-' + index + '.txt'), 'owned fixture');
  }
  const parent = new Deadline(250);
  const intent = beginHealthAccounting(dir, parent);
  const openDirectory = fs.opendirSync;
  let inspectedEntries = 0;
  t.mock.method(fs, 'opendirSync', (...args) => {
    const directory = openDirectory(...args);
    const read = directory.readSync.bind(directory);
    directory.readSync = () => { inspectedEntries++; return read(); };
    return directory;
  });

  bumpHealth(dir, 'recordsWritten', parent, intent);
  assert.equal(fs.existsSync(foreign), true, 'only the 64 owned intent names may be retired');
  assert.ok(inspectedEntries <= HEALTH_RECEIPT_SLOTS * 3 + 1, 'directory inspection has a fixed cap');
  assert.equal(readHealth(dir).accountingIncomplete, true, 'a partial directory listing must remain explicit');
});

test('slow pending enumeration reserves time to persist incomplete coverage', async (t) => {
  const root = tempDir();
  t.after(() => removeDir(root));
  const env = { OVERSEER_SPOOL_ROOT: root };
  assert.equal((await runBridge(fixture('stop.json'), { env })).status, 0);
  const dir = instanceDirectory(root);
  const pending = path.join(dir, 'health-pending');
  for (let index = 0; index < 10; index++) fs.writeFileSync(path.join(pending, 'foreign-' + index + '.txt'), 'owned fixture');
  const intent = beginHealthAccounting(dir, new Deadline(250));
  let now = 1000;
  t.mock.method(Date, 'now', () => now);
  const openDirectory = fs.opendirSync;
  let inspectedEntries = 0;
  t.mock.method(fs, 'opendirSync', (...args) => {
    const directory = openDirectory(...args);
    const read = directory.readSync.bind(directory);
    directory.readSync = () => { now += 10; inspectedEntries++; return read(); };
    return directory;
  });

  bumpHealth(dir, 'recordsWritten', new Deadline(250), intent);
  assert.ok(now <= 1060, 'pending enumeration stays inside the existing health slice');
  assert.ok(inspectedEntries < 10, 'enumeration stops before the slow directory is exhausted');
  assert.equal(readHealth(dir).accountingIncomplete, true);
});

test('a full receipt buffer reclaims slots without double counting', async (t) => {
  const root = tempDir();
  t.after(() => removeDir(root));
  const env = { OVERSEER_SPOOL_ROOT: root };
  assert.equal((await runBridge(fixture('stop.json'), { env })).status, 0);
  const dir = instanceDirectory(root);
  for (let slot = 0; slot < HEALTH_RECEIPT_SLOTS; slot++) {
    fs.writeFileSync(path.join(dir, 'health-pending', String(slot).padStart(2, '0') + '.json'),
      JSON.stringify({ id: randomUUID(), counters: ['droppedLockTimeout'] }));
  }
  const result = await recoverCapacity(root, env);
  assert.equal(result.status, 0);
  assert.equal(result.stdout, '');
  const health = readHealth(dir);
  assert.equal(health.counters.recordsWritten, 2);
  assert.equal(health.counters.droppedLockTimeout, HEALTH_RECEIPT_SLOTS);
  assert.equal(health.accountingIncomplete, false);
});

test('slow full-buffer I/O reports incomplete accounting without extending the health slice', async (t) => {
  const root = tempDir();
  t.after(() => removeDir(root));
  const env = { OVERSEER_SPOOL_ROOT: root };
  assert.equal((await runBridge(fixture('stop.json'), { env })).status, 0);
  const dir = instanceDirectory(root);
  for (let slot = 0; slot < HEALTH_RECEIPT_SLOTS; slot++) {
    fs.writeFileSync(path.join(dir, 'health-pending', String(slot).padStart(2, '0') + '.json'),
      JSON.stringify({ id: randomUUID(), counters: ['droppedLockTimeout'] }));
  }
  let now = 1000;
  t.mock.method(Date, 'now', () => now);
  const exists = fs.existsSync;
  t.mock.method(fs, 'existsSync', (file) => {
    if (String(file).includes('health-pending') && String(file).endsWith('.json')) now += 10;
    return exists(file);
  });
  // The direct fold opens each receipt; the fallback publisher probes each slot.
  const open = fs.openSync;
  t.mock.method(fs, 'openSync', (file, ...rest) => {
    if (String(file).includes('health-pending') && String(file).endsWith('.json')) now += 10;
    return open(file, ...rest);
  });
  bumpHealth(dir, 'recordsWritten', new Deadline(250));
  assert.ok(now <= 1060, 'accounting keeps the original 60 ms slice');
  const health = readHealth(dir);
  assert.equal(health.counters.recordsWritten, 1, 'no invented count when receipt publication cannot finish');
  assert.equal(health.counters.droppedLockTimeout, HEALTH_RECEIPT_SLOTS);
  assert.equal(health.accountingIncomplete, true);
});

test('a drop remains accounted when both the event and health locks are held', async (t) => {
  const root = tempDir();
  t.after(() => removeDir(root));
  const env = { OVERSEER_SPOOL_ROOT: root };
  assert.equal((await runBridge(fixture('stop.json'), { env })).status, 0);
  const dir = instanceDirectory(root);
  for (const name of ['events.lock', 'health.lock']) {
    fs.writeFileSync(path.join(dir, name), 'held by this regression\n', { flag: 'wx' });
  }

  const blocked = await runBridge(fixture('pre-tool-use.json'), { env });
  assert.equal(blocked.status, 0, 'lock contention must fail open');
  assert.equal(blocked.stdout, '');
  assert.equal(blocked.timedOut, false);
  assert.ok(blocked.durationMs < 2000, 'must not wait for the held locks to become stale');
  assert.equal(readLines(dir).length, 1, 'the blocked event must not be appended');
  assert.equal(readHealth(dir).counters.droppedLockTimeout, 1,
    'the drop must remain visible while health.lock is still held');

  for (const name of ['events.lock', 'health.lock']) fs.unlinkSync(path.join(dir, name));
  for (let i = 0; i < 2; i++) {
    assert.equal((await runBridge(fixture('pre-tool-use.json'), { env })).status, 0);
    const health = readHealth(dir);
    assert.equal(health.counters.recordsWritten, i + 2);
    assert.equal(health.counters.droppedLockTimeout, 1, 'folding a receipt must not count it twice');
  }
  assert.equal(JSON.parse(fs.readFileSync(path.join(dir, 'health.json'), 'utf8')).counters.droppedLockTimeout, 1,
    'a later writer folds the deferred count into the legacy summary');
});

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
