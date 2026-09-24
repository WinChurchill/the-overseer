import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';

import { INSTALL_PATH, UNINSTALL_PATH, listBackups, removeDir, runNode, tempDir } from '../lib/testkit.mjs';

const MARKER = 'overseer-claude-bridge';

function readJson(file) {
  return JSON.parse(fs.readFileSync(file, 'utf8'));
}

function userSettings() {
  return {
    hooks: {
      PreToolUse: [
        { matcher: 'Bash', hooks: [{ type: 'command', command: 'echo user-pre-tool-use' }] },
      ],
      Stop: [
        { hooks: [{ type: 'command', command: 'echo user-stop', timeout: 30 }] },
      ],
    },
    permissions: { allow: ['Bash(ls:*)'], deny: [] },
    model: 'claude-fixture-model',
  };
}

function ownedHandlers(data) {
  const found = [];
  const hooks = data.hooks ?? {};
  for (const [event, groups] of Object.entries(hooks)) {
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

test('installer refuses malformed settings and never writes without --apply', async (t) => {
  const dir = tempDir();
  t.after(() => removeDir(dir));
  const settingsPath = path.join(dir, 'settings.json');

  const original = JSON.stringify(userSettings(), null, 2) + '\n';
  fs.writeFileSync(settingsPath, original, 'utf8');

  const preview = await runNode(INSTALL_PATH, [settingsPath]);
  assert.equal(preview.status, 0);
  assert.equal(fs.readFileSync(settingsPath, 'utf8'), original, 'preview must not write');
  assert.equal(listBackups(settingsPath).length, 0, 'preview must not create a backup');
  assert.match(preview.stdout, /dry run/i);
  assert.match(preview.stdout, /no files written/i);

  const asJson = await runNode(INSTALL_PATH, [settingsPath, '--json']);
  assert.equal(asJson.status, 0);
  const plan = JSON.parse(asJson.stdout);
  assert.equal(plan.applied, false);
  assert.equal(plan.marker, MARKER);
  assert.ok(plan.changes.length > 0);
  assert.equal(fs.readFileSync(settingsPath, 'utf8'), original, 'json preview must not write');

  const broken = '{ "hooks": { "PreToolUse": [';
  fs.writeFileSync(settingsPath, broken, 'utf8');
  const refused = await runNode(INSTALL_PATH, [settingsPath, '--apply']);
  assert.equal(refused.status, 2, 'malformed settings must be refused');
  assert.match(refused.stderr, /refus/i);
  assert.equal(fs.readFileSync(settingsPath, 'utf8'), broken, 'a refused install must not modify the file');
  assert.equal(listBackups(settingsPath).length, 0);

  const refusedUninstall = await runNode(UNINSTALL_PATH, [settingsPath, '--apply']);
  assert.equal(refusedUninstall.status, 2);
  assert.equal(fs.readFileSync(settingsPath, 'utf8'), broken);

  const notAnObject = path.join(dir, 'array.json');
  fs.writeFileSync(notAnObject, '[1,2,3]\n', 'utf8');
  const refusedArray = await runNode(INSTALL_PATH, [notAnObject, '--apply']);
  assert.equal(refusedArray.status, 2);
  assert.equal(fs.readFileSync(notAnObject, 'utf8'), '[1,2,3]\n');
});

test('install preserves unrelated entries, is idempotent, and uninstall removes only owned entries', async (t) => {
  const dir = tempDir();
  t.after(() => removeDir(dir));
  const settingsPath = path.join(dir, 'settings.json');
  const original = userSettings();
  fs.writeFileSync(settingsPath, JSON.stringify(original, null, 2) + '\n', 'utf8');

  const applied = await runNode(INSTALL_PATH, [settingsPath, '--apply']);
  assert.equal(applied.status, 0);
  const backups = listBackups(settingsPath);
  assert.equal(backups.length, 1, 'exactly one backup before the first write');
  assert.equal(fs.readFileSync(path.join(dir, backups[0]), 'utf8'), JSON.stringify(original, null, 2) + '\n', 'backup holds the pre-install bytes');

  const installed = readJson(settingsPath);
  assert.deepEqual(installed.permissions, original.permissions, 'unrelated settings survive');
  assert.equal(installed.model, original.model);
  const userGroupsIn = (event) => (installed.hooks[event] ?? []).filter((group) => {
    const handlers = Array.isArray(group.hooks) ? group.hooks : [];
    return !handlers.some((handler) => (Array.isArray(handler.args) ? handler.args : []).some((arg) => typeof arg === 'string' && arg.includes(MARKER)));
  });
  assert.deepEqual(userGroupsIn('PreToolUse'), original.hooks.PreToolUse, 'unrelated hooks survive');
  assert.deepEqual(userGroupsIn('Stop'), original.hooks.Stop);

  const owned = ownedHandlers(installed);
  assert.equal(owned.length, 15, 'the default install wires 15 documented events');
  for (const { handler } of owned) {
    assert.equal(handler.type, 'command');
    assert.equal(handler.command, 'node');
    assert.ok(fs.existsSync(handler.args[0]), 'the recorded bridge path must exist');
    assert.ok(handler.args[0].endsWith('bridge.mjs'));
    assert.ok(handler.args.includes(MARKER));
    assert.equal(handler.timeout, 10);
  }
  assert.ok(owned.some((entry) => entry.event === 'SessionStart'));
  assert.equal(installed.hooks.SessionStart.length, 1);
  assert.equal(installed.hooks.SessionStart[0].matcher, undefined, 'no matcher means every occurrence');

  const bytesAfterInstall = fs.readFileSync(settingsPath, 'utf8');
  const again = await runNode(INSTALL_PATH, [settingsPath, '--apply']);
  assert.equal(again.status, 0);
  assert.match(again.stdout, /Already installed/i);
  assert.equal(fs.readFileSync(settingsPath, 'utf8'), bytesAfterInstall, 'install must be idempotent');
  assert.equal(listBackups(settingsPath).length, 1, 'an idempotent install writes no second backup');

  const previewUninstall = await runNode(UNINSTALL_PATH, [settingsPath]);
  assert.equal(previewUninstall.status, 0);
  assert.equal(fs.readFileSync(settingsPath, 'utf8'), bytesAfterInstall, 'uninstall preview must not write');
  assert.equal(listBackups(settingsPath).length, 1);

  const removed = await runNode(UNINSTALL_PATH, [settingsPath, '--apply']);
  assert.equal(removed.status, 0);
  const finalSettings = readJson(settingsPath);
  assert.deepEqual(finalSettings.hooks.PreToolUse, original.hooks.PreToolUse, 'user hooks stay untouched');
  assert.deepEqual(finalSettings.hooks.Stop, original.hooks.Stop);
  assert.deepEqual(finalSettings.permissions, original.permissions);
  assert.equal(finalSettings.model, original.model);
  assert.equal(ownedHandlers(finalSettings).length, 0, 'every owned handler is gone');
  assert.deepEqual(finalSettings, original, 'uninstall restores the original content semantically');
  assert.ok(listBackups(settingsPath).length >= 2, 'uninstall backs up before writing');
});

test('uninstall reports owned entries it cannot remove', async (t) => {
  const dir = tempDir();
  t.after(() => removeDir(dir));
  const settingsPath = path.join(dir, 'settings.json');

  const weird = {
    hooks: {
      PreToolUse: [{ matcher: 'Bash', hooks: [{ type: 'command', command: 'node', args: ['/opt/overseer-claude-bridge', MARKER], timeout: 10 }] }],
      PostToolUse: { hooks: [{ type: 'command', command: 'node', args: ['/opt/overseer-claude-bridge', MARKER] }] },
      Stop: [{ hooks: [{ type: 'command', command: 'echo keep-me' }] }],
    },
  };
  fs.writeFileSync(settingsPath, JSON.stringify(weird, null, 2) + '\n', 'utf8');

  const result = await runNode(UNINSTALL_PATH, [settingsPath, '--apply']);
  assert.equal(result.status, 3, 'partial removal is reported through the exit code');
  assert.match(result.stdout, /could not be removed/i);

  const after = readJson(settingsPath);
  assert.equal(after.hooks.PreToolUse, undefined, 'the removable owned entry is gone');
  assert.deepEqual(after.hooks.Stop, weird.hooks.Stop, 'user hooks stay');
  assert.deepEqual(after.hooks.PostToolUse, weird.hooks.PostToolUse, 'the malformed owned entry is left untouched');
});

test('install refuses to edit a hooks value it does not own', async (t) => {
  const dir = tempDir();
  t.after(() => removeDir(dir));
  const settingsPath = path.join(dir, 'settings.json');
  const weird = JSON.stringify({ hooks: { PreToolUse: { hooks: [] } } }, null, 2) + '\n';
  fs.writeFileSync(settingsPath, weird, 'utf8');

  const result = await runNode(INSTALL_PATH, [settingsPath, '--apply']);
  assert.equal(result.status, 2);
  assert.match(result.stderr, /not an array/i);
  assert.equal(fs.readFileSync(settingsPath, 'utf8'), weird);
  assert.equal(listBackups(settingsPath).length, 0);
});
