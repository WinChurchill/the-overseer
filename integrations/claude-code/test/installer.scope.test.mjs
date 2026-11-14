import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { buildOwnedHandler, planInstall } from '../lib/settings.mjs';
import { UNINSTALL_PATH, listBackups, removeDir, runNode, tempDir } from '../lib/testkit.mjs';

function fixture(t) {
  const dir = tempDir();
  t.after(() => removeDir(dir));
  const settings = path.join(dir, 'settings.json');
  const installed = path.join(dir, 'installed', 'bridge.mjs');
  const portable = path.join(dir, 'portable', 'bridge.mjs');
  return { dir, settings, installed, portable };
}

test('scoped uninstall preserves a portable install on another event and unrelated settings', async t => {
  const { settings, installed, portable } = fixture(t);
  let plan = planInstall({ data: { model: 'fixture-model', permissions: { allow: [] } } },
    { bridgePath: installed, events: ['SessionStart'] });
  plan = planInstall({ data: plan.data }, { bridgePath: portable, events: ['Stop'] });
  fs.writeFileSync(settings, JSON.stringify(plan.data));

  const removed = await runNode(UNINSTALL_PATH, [settings, `--bridge=${installed}`, '--apply', '--json']);
  assert.equal(removed.status, 0, removed.stderr);
  const after = JSON.parse(fs.readFileSync(settings, 'utf8'));
  assert.equal(after.hooks.SessionStart, undefined);
  assert.deepEqual(after.hooks.Stop, plan.data.hooks.Stop);
  assert.equal(after.model, plan.data.model);
  assert.deepEqual(after.permissions, plan.data.permissions);
  assert.equal(JSON.parse(removed.stdout).removed.length, 1);
});

test('scoped uninstall matches the exact bridge path inside a mixed group', async t => {
  const { settings, installed, portable } = fixture(t);
  const neighbor = installed + '.other';
  const foreign = { type: 'command', command: 'fixture-user-hook' };
  const data = { hooks: { Stop: [{ matcher: '', hooks: [buildOwnedHandler(installed),
    buildOwnedHandler(portable), buildOwnedHandler(neighbor), foreign] }] } };
  fs.writeFileSync(settings, JSON.stringify(data));
  const removed = await runNode(UNINSTALL_PATH, [settings, `--bridge=${installed}`, '--apply']);
  assert.equal(removed.status, 0, removed.stderr);
  assert.deepEqual(JSON.parse(fs.readFileSync(settings)).hooks.Stop[0].hooks,
    [buildOwnedHandler(portable), buildOwnedHandler(neighbor), foreign]);
});

test('scoped preview is read-only and apply is idempotent with a byte-exact backup', async t => {
  const { dir, settings, installed, portable } = fixture(t);
  const original = '\uFEFF' + JSON.stringify({ hooks: { Stop: [{ hooks:
    [buildOwnedHandler(installed), buildOwnedHandler(portable)] }] } }, null, 2) + '\n';
  fs.writeFileSync(settings, original);
  const preview = await runNode(UNINSTALL_PATH, [settings, `--bridge=${installed}`, '--json']);
  assert.equal(preview.status, 0, preview.stderr);
  assert.equal(fs.readFileSync(settings, 'utf8'), original);
  assert.equal(listBackups(settings).length, 0);
  const apply = await runNode(UNINSTALL_PATH, [settings, `--bridge=${installed}`, '--apply']);
  assert.equal(apply.status, 0, apply.stderr);
  assert.equal(fs.readFileSync(path.join(dir, listBackups(settings)[0]), 'utf8'), original);
  const after = fs.readFileSync(settings, 'utf8');
  const again = await runNode(UNINSTALL_PATH, [settings, `--bridge=${installed}`, '--apply', '--json']);
  assert.equal(again.status, 0, again.stderr);
  assert.equal(JSON.parse(again.stdout).applied, false);
  assert.equal(fs.readFileSync(settings, 'utf8'), after);
  assert.equal(listBackups(settings).length, 1);
});

test('scoped uninstall uses Windows path case and separator semantics', async t => {
  const { settings, installed, portable } = fixture(t);
  fs.writeFileSync(settings, JSON.stringify({ hooks: { Stop: [{ hooks:
    [buildOwnedHandler(installed), buildOwnedHandler(portable)] }] } }));
  const scope = process.platform === 'win32' ? installed.toUpperCase().replaceAll('\\', '/') : installed;
  const removed = await runNode(UNINSTALL_PATH, [settings, `--bridge=${scope}`, '--apply', '--json']);
  assert.equal(removed.status, 0, removed.stderr);
  assert.deepEqual(JSON.parse(fs.readFileSync(settings)).hooks.Stop[0].hooks, [buildOwnedHandler(portable)]);
});

test('empty scope is refused without falling back to global removal', async t => {
  const { settings, installed } = fixture(t);
  const original = JSON.stringify({ hooks: { Stop: [{ hooks: [buildOwnedHandler(installed)] }] } });
  fs.writeFileSync(settings, original);
  const refused = await runNode(UNINSTALL_PATH, [settings, '--bridge=', '--apply']);
  assert.notEqual(refused.status, 0);
  assert.equal(fs.readFileSync(settings, 'utf8'), original);
  assert.equal(listBackups(settings).length, 0);
});

test('a nonmatching scope leaves even empty unrelated groups byte-identical', async t => {
  const { settings, installed, portable } = fixture(t);
  const original = JSON.stringify({ hooks: { Stop: [{ matcher: 'fixture', hooks: [] },
    { hooks: [buildOwnedHandler(portable)] }] } }, null, 2) + '\n';
  fs.writeFileSync(settings, original);
  const removed = await runNode(UNINSTALL_PATH, [settings, `--bridge=${installed}`, '--apply', '--json']);
  assert.equal(removed.status, 0, removed.stderr);
  assert.equal(JSON.parse(removed.stdout).applied, false);
  assert.equal(fs.readFileSync(settings, 'utf8'), original);
  assert.equal(listBackups(settings).length, 0);
});
