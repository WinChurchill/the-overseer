import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { verifyBridgeCommand } from '../lib/verify.mjs';
import { tempDir, removeDir } from '../lib/testkit.mjs';

test('installer retries one clean no-record probe, requiring a new record', (t) => {
  const root = tempDir();
  t.after(() => removeDir(root));
  const bridge = path.join(root, 'probe.mjs');
  fs.writeFileSync(bridge, `
    import fs from 'node:fs';
    import path from 'node:path';
    const root = process.env.OVERSEER_SPOOL_ROOT;
    const countFile = path.join(root, 'attempts');
    const count = fs.existsSync(countFile) ? Number(fs.readFileSync(countFile, 'utf8')) + 1 : 1;
    fs.writeFileSync(countFile, String(count));
    if (count === 2) {
      const dir = path.join(root, 'claude', 'fixture');
      fs.mkdirSync(dir, { recursive: true });
      fs.writeFileSync(path.join(dir, 'events.jsonl'), JSON.stringify({
        harnessId: 'claude', kind: 'session/start', sequence: 1,
        sessionId: 'overseer-install-verify', producerVersion: 'synthetic-probe'
      }) + '\\n');
    }
  `);
  const spoolRoot = path.join(root, 'spool');
  fs.mkdirSync(spoolRoot);
  const result = verifyBridgeCommand({ bridgePath: bridge, command: process.execPath, spoolRoot });
  assert.equal(result.ok, true);
  assert.equal(result.attempts, 2);
  assert.equal(fs.readFileSync(path.join(spoolRoot, 'attempts'), 'utf8'), '2');
  const stale = verifyBridgeCommand({ bridgePath: bridge, command: process.execPath, spoolRoot });
  assert.equal(stale.ok, false, 'earlier probe output cannot certify later invocations');
  assert.equal(stale.code, 'no-record-written');
  assert.equal(stale.attempts, 2);
  assert.equal(fs.readFileSync(path.join(spoolRoot, 'attempts'), 'utf8'), '4');
});

test('nonzero probes are refused without retry', (t) => {
  const root = tempDir();
  t.after(() => removeDir(root));
  const bridge = path.join(root, 'broken.mjs');
  fs.writeFileSync(bridge, 'process.exit(3);');
  const result = verifyBridgeCommand({ bridgePath: bridge, command: process.execPath });
  assert.equal(result.ok, false);
  assert.equal(result.code, 'command-exit-nonzero');
  assert.equal(result.attempts, 1);
});
