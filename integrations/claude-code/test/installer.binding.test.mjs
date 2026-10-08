import test from 'node:test';
import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import fs from 'node:fs';
import path from 'node:path';

import {
  BRIDGE_PATH,
  INSTALL_PATH,
  INTEGRATION_ROOT,
  fixture,
  listBackups,
  readRecords,
  removeDir,
  runNode,
  tempDir,
} from '../lib/testkit.mjs';

const MARKER = 'overseer-claude-bridge';
const DEFAULT_EVENTS = 15;

for (const failure of ['missing', 'broken', 'missing-no-verify', 'mixed-paths']) {
  test(`same-path mutation refuses ${failure} payload without changing settings`, async (t) => {
    const dir = tempDir();
    t.after(() => removeDir(dir));
    const root = fullExtraction(dir, 'payload');
    const bridge = path.join(root, 'bridge.mjs');
    const settings = path.join(dir, 'settings.json');
    const applied = await runNode(INSTALL_PATH, [settings, `--bridge=${bridge}`, '--events=SessionStart', '--apply']);
    assert.equal(applied.status, 0, applied.stderr);
    if (failure === 'mixed-paths') {
      const data = readJson(settings);
      data.hooks.Stop = [{ hooks: [{ ...data.hooks.SessionStart[0].hooks[0], args: [path.join(dir, 'old.mjs'), MARKER] }] }];
      fs.writeFileSync(settings, JSON.stringify(data));
    }
    if (failure === 'broken') fs.writeFileSync(bridge, 'process.exit(3);');
    else fs.renameSync(bridge, bridge + '.unavailable');
    const before = fs.readFileSync(settings, 'utf8');
    const backups = listBackups(settings).length;
    const args = [settings, `--bridge=${bridge}`, '--events=SessionStart,Stop', '--apply'];
    if (failure === 'missing-no-verify') args.push('--no-verify');
    const refused = await runNode(INSTALL_PATH, args);
    assert.equal(refused.status, 2, refused.stdout);
    assert.equal(fs.readFileSync(settings, 'utf8'), before);
    assert.equal(listBackups(settings).length, backups);
  });
}

/**
 * A directory that looks like a user's extraction of the release archive: the
 * installer and its libraries are present, but bridge.mjs has been deleted (or was
 * never copied). This is the shape that used to be written into settings anyway.
 */
function partialExtraction(parent, name = 'partial') {
  const root = path.join(parent, name);
  fs.mkdirSync(path.join(root, 'lib'), { recursive: true });
  for (const file of ['install.mjs', 'uninstall.mjs']) {
    fs.copyFileSync(path.join(INTEGRATION_ROOT, file), path.join(root, file));
  }
  for (const file of ['mapping.mjs', 'settings.mjs', 'spool.mjs', 'verify.mjs']) {
    fs.copyFileSync(path.join(INTEGRATION_ROOT, 'lib', file), path.join(root, 'lib', file));
  }
  return root;
}

/** A complete copy of the integration, as an extracted archive would contain it. */
function fullExtraction(parent, name) {
  const root = path.join(parent, name);
  fs.cpSync(INTEGRATION_ROOT, root, { recursive: true });
  return root;
}

function ownedHandlers(data) {
  const found = [];
  for (const [event, groups] of Object.entries(data.hooks ?? {})) {
    if (!Array.isArray(groups)) continue;
    for (const group of groups) {
      if (!group || !Array.isArray(group.hooks)) continue;
      for (const handler of group.hooks) {
        const args = Array.isArray(handler.args) ? handler.args : [];
        if (args.some((arg) => typeof arg === 'string' && arg.includes(MARKER))) found.push({ event, handler });
      }
    }
  }
  return found;
}

/** Groups in an event array that contain no OVERSEER handler. */
function userGroupsIn(data, event) {
  return (data.hooks?.[event] ?? []).filter((group) => {
    const handlers = Array.isArray(group.hooks) ? group.hooks : [];
    return !handlers.some((handler) =>
      (Array.isArray(handler.args) ? handler.args : []).some(
        (arg) => typeof arg === 'string' && arg.includes(MARKER),
      ),
    );
  });
}

function readJson(file) {
  return JSON.parse(fs.readFileSync(file, 'utf8'));
}

/**
 * Run exactly what the settings file tells Claude Code to run: the handler's own
 * `command` with its own `args`, as an argument vector (the documented direct
 * executable form), and collect the spool the hook wrote.
 */
function invokeRecordedHandler(handler, spoolRoot) {
  const payload = JSON.stringify(fixture('session-start.json'));
  const run = spawnSync(handler.command, handler.args, {
    input: payload,
    encoding: 'utf8',
    timeout: 20000,
    windowsHide: true,
    env: { ...process.env, OVERSEER_SPOOL_ROOT: spoolRoot },
  });

  const instanceFile = path.join(spoolRoot, 'claude', 'instance.json');
  let instanceId = null;
  if (fs.existsSync(instanceFile)) instanceId = JSON.parse(fs.readFileSync(instanceFile, 'utf8')).sourceInstanceId;
  const directory = instanceId ? path.join(spoolRoot, 'claude', instanceId) : null;

  return {
    status: run.status,
    signal: run.signal,
    stdout: run.stdout ?? '',
    stderr: run.stderr ?? '',
    directory,
    records: directory ? readRecords(directory) : [],
    health: directory && fs.existsSync(path.join(directory, 'health.json'))
      ? readJson(path.join(directory, 'health.json')).counters : null,
  };
}

test('a missing bridge payload is refused and no settings are written', async (t) => {
  const dir = tempDir();
  t.after(() => removeDir(dir));

  const partial = partialExtraction(dir);
  assert.equal(fs.existsSync(path.join(partial, 'bridge.mjs')), false, 'the fixture really lacks bridge.mjs');

  const settingsPath = path.join(dir, 'settings.json');
  const original = JSON.stringify({ model: 'keep-me', hooks: {} }, null, 2) + '\n';
  fs.writeFileSync(settingsPath, original, 'utf8');

  const applied = await runNode(path.join(partial, 'install.mjs'), [settingsPath, '--apply']);
  assert.equal(applied.status, 2, 'apply against a missing bridge must be refused');
  assert.match(applied.stderr, /refus/i);
  assert.match(applied.stderr, /does not exist/i);
  assert.equal(fs.readFileSync(settingsPath, 'utf8'), original, 'a refused apply must not modify settings');
  assert.equal(listBackups(settingsPath).length, 0, 'a refused apply must not create a backup');

  const asJson = await runNode(path.join(partial, 'install.mjs'), [settingsPath, '--apply', '--json']);
  assert.equal(asJson.status, 2);
  const report = JSON.parse(asJson.stdout);
  assert.equal(report.applied, false);
  assert.equal(report.refused, true);
  assert.equal(report.bridgeExists, false);
  assert.equal(report.bridgeVerified, false);
  assert.equal(report.verification.ok, false);
  assert.equal(report.verification.code, 'bridge-missing');
  assert.equal(fs.readFileSync(settingsPath, 'utf8'), original, 'the json refusal must not write either');

  // A preview against the same missing payload still works and warns, so the user
  // can see the plan without a write path being available.
  const preview = await runNode(path.join(partial, 'install.mjs'), [settingsPath]);
  assert.equal(preview.status, 0);
  assert.match(preview.stdout, /MISSING/i);
  assert.match(preview.stdout, /would refuse to write/i);
  assert.equal(fs.readFileSync(settingsPath, 'utf8'), original);
});

test('a bridge path that is not a runnable file is refused', async (t) => {
  const dir = tempDir();
  t.after(() => removeDir(dir));
  const settingsPath = path.join(dir, 'settings.json');
  fs.writeFileSync(settingsPath, JSON.stringify({ hooks: {} }, null, 2) + '\n', 'utf8');

  const directoryTarget = path.join(dir, 'a-directory');
  fs.mkdirSync(directoryTarget);
  const asDirectory = await runNode(INSTALL_PATH, [settingsPath, `--bridge=${directoryTarget}`, '--apply']);
  assert.equal(asDirectory.status, 2);
  assert.match(asDirectory.stderr, /not a regular file/i);
  assert.deepEqual(readJson(settingsPath).hooks, {}, 'nothing may be written for a directory target');

  const broken = path.join(dir, 'broken.mjs');
  fs.writeFileSync(broken, 'throw new Error("this bridge cannot start");\n', 'utf8');
  const asBroken = await runNode(INSTALL_PATH, [settingsPath, `--bridge=${broken}`, '--apply']);
  assert.equal(asBroken.status, 2, 'a bridge that exits non-zero must be refused');
  assert.match(asBroken.stderr, /exited [1-9]/i);
  assert.deepEqual(readJson(settingsPath).hooks, {}, 'nothing may be written for a failing bridge');

  // A script that exits 0 but never publishes a record is equally unusable: the
  // integration would silently observe nothing.
  const silent = path.join(dir, 'silent.mjs');
  fs.writeFileSync(silent, 'process.exit(0);\n', 'utf8');
  const asSilent = await runNode(INSTALL_PATH, [settingsPath, `--bridge=${silent}`, '--apply']);
  assert.equal(asSilent.status, 2);
  assert.match(asSilent.stderr, /wrote no spool record/i);
  assert.deepEqual(readJson(settingsPath).hooks, {});
});

test('the generated hook command actually runs after rebinding to a new extraction', async (t) => {
  const dir = tempDir();
  t.after(() => removeDir(dir));

  // An old extraction, wired up, then made unavailable - the situation a user hits
  // after deleting the folder they first ran the installer from.
  const oldRoot = fullExtraction(dir, 'old-extraction');
  const settingsPath = path.join(dir, 'settings.json');
  const unrelated = {
    hooks: {
      PreToolUse: [{ matcher: 'Bash', hooks: [{ type: 'command', command: 'echo user-hook' }] }],
    },
    permissions: { allow: ['Bash(ls:*)'] },
    model: 'keep-me',
  };
  fs.writeFileSync(settingsPath, JSON.stringify(unrelated, null, 2) + '\n', 'utf8');

  const first = await runNode(path.join(oldRoot, 'install.mjs'), [settingsPath, '--apply']);
  assert.equal(first.status, 0, first.stderr);
  const oldBridge = path.join(oldRoot, 'bridge.mjs');
  const afterFirst = readJson(settingsPath);
  const installed = ownedHandlers(afterFirst);
  assert.equal(installed.length, DEFAULT_EVENTS);
  for (const { handler } of installed) {
    assert.equal(handler.args[0], oldBridge, 'the recorded path is the old extraction');
  }

  // A new extraction whose path contains spaces and non-ASCII characters.
  const newName = 'new extraction Ω with spaces';
  const newRoot = fullExtraction(dir, newName);
  removeDir(oldRoot);
  assert.equal(fs.existsSync(oldBridge), false, 'the old extraction is now unavailable');

  const rebound = await runNode(path.join(newRoot, 'install.mjs'), [settingsPath, '--apply']);
  assert.equal(rebound.status, 0, rebound.stderr);
  assert.match(rebound.stdout, /verified/i, 'the rebind verifies the new path before writing');

  const afterRebind = readJson(settingsPath);
  const newBridge = path.join(newRoot, 'bridge.mjs');
  const reboundHandlers = ownedHandlers(afterRebind);
  assert.equal(reboundHandlers.length, DEFAULT_EVENTS, 'rebinding removes duplicates rather than adding a second handler');
  for (const { handler } of reboundHandlers) {
    assert.equal(handler.args[0], newBridge, 'every owned handler points at the new extraction');
    assert.equal(handler.command, 'node');
    assert.ok(handler.args.includes(MARKER));
  }

  // Unrelated configuration survives the rebind untouched.
  assert.deepEqual(userGroupsIn(afterRebind, 'PreToolUse'), unrelated.hooks.PreToolUse, 'unrelated hooks survive');
  assert.deepEqual(afterRebind.permissions, unrelated.permissions);
  assert.equal(afterRebind.model, unrelated.model);
  assert.equal(JSON.stringify(afterRebind).includes(oldBridge), false, 'the old path is fully gone from settings');

  // Now run the recorded command itself with an invented payload and confirm the
  // spool receives a record: executable binding, not merely spool existence.
  const spoolRoot = path.join(dir, 'spool');
  const previews = [];
  for (const { event, handler } of reboundHandlers) {
    const result = invokeRecordedHandler(handler, spoolRoot);
    assert.equal(result.status, 0, `${event} hook must exit 0`);
    assert.equal(result.records.length, previews.length + 1,
      `${event} must publish a new record; earlier records cannot certify this invocation. Health: ${JSON.stringify(result.health)}`);
    previews.push({ event, handler, result });
  }

  const observed = previews.filter((entry) => entry.result.records.length > 0);
  assert.ok(observed.length > 0, 'at least one recorded hook command must write a record at the new location');
  assert.equal(observed.length, DEFAULT_EVENTS, 'every recorded event must publish when its own arguments are used');

  for (const entry of observed) {
    const [record] = entry.result.records;
    assert.equal(entry.result.status, 0, `${entry.event} hook must exit 0`);
    assert.equal(entry.result.stdout, '', 'the hook never writes to stdout');
    assert.equal(record.harnessId, 'claude');
    assert.equal(record.sessionId, 'fixture-session-0001');
    assert.equal(record.kind, 'session/start');
    assert.equal(record.producerVersion, 'overseer-claude-bridge/1.0.2');
    assert.ok(Number.isInteger(record.sequence) && record.sequence >= 1);
  }

  // Rollback: uninstall from the new extraction removes every owned handler and is
  // idempotent, leaving the unrelated configuration in place.
  const removed = await runNode(path.join(newRoot, 'uninstall.mjs'), [settingsPath, '--apply']);
  assert.equal(removed.status, 0, removed.stderr);
  const afterRemoval = readJson(settingsPath);
  assert.equal(ownedHandlers(afterRemoval).length, 0, 'every owned handler is gone');
  assert.deepEqual(userGroupsIn(afterRemoval, 'PreToolUse'), unrelated.hooks.PreToolUse);
  assert.deepEqual(afterRemoval.permissions, unrelated.permissions);
  assert.equal(afterRemoval.model, unrelated.model);
});

test('rebinding is refused when the new location is not runnable, and the old settings survive', async (t) => {
  const dir = tempDir();
  t.after(() => removeDir(dir));

  const root = fullExtraction(dir, 'extraction');
  const settingsPath = path.join(dir, 'settings.json');
  fs.writeFileSync(settingsPath, JSON.stringify({ hooks: {} }, null, 2) + '\n', 'utf8');

  const applied = await runNode(path.join(root, 'install.mjs'), [settingsPath, '--apply']);
  assert.equal(applied.status, 0, applied.stderr);
  const wiredBytes = fs.readFileSync(settingsPath, 'utf8');

  // Point the installer at a directory that has no bridge: the write must be refused
  // and the working configuration must be left exactly as it was.
  const empty = path.join(dir, 'empty');
  fs.mkdirSync(empty);
  const refused = await runNode(INSTALL_PATH, [
    settingsPath,
    `--bridge=${path.join(empty, 'bridge.mjs')}`,
    '--apply',
  ]);
  assert.equal(refused.status, 2);
  assert.match(refused.stderr, /does not exist/i);
  assert.equal(fs.readFileSync(settingsPath, 'utf8'), wiredBytes, 'a refused rebind leaves the working install intact');

  // Re-running against the already-recorded path stays idempotent and needs no probe.
  const again = await runNode(path.join(root, 'install.mjs'), [settingsPath, '--apply']);
  assert.equal(again.status, 0, again.stderr);
  assert.match(again.stdout, /Already installed/i);
  assert.equal(fs.readFileSync(settingsPath, 'utf8'), wiredBytes);
  assert.equal(listBackups(settingsPath).length, 1, 'an idempotent re-run writes no second backup');
});
