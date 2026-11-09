import { test } from 'node:test'
import assert from 'node:assert/strict'
import * as fs from 'node:fs'
import { tmpdir } from 'node:os'
import { dirname, join } from 'node:path'
import { createSpool } from '../src/spool.js'

function setup(t, options = {}) {
  const root = fs.mkdtempSync(join(tmpdir(), 'overseer-dsh-guard-'))
  const outside = join(root, 'outside')
  fs.mkdirSync(outside)
  const spool = createSpool({ spoolRoot: join(root, 'spool'), sourceInstanceId: 'guard',
    currentMinIntervalMs: 0, cleanupIntervalMs: 0, ...options })
  t.after(() => {
    spool.close()
    fs.rmSync(root, { recursive: true, force: true })
  })
  return { spool, outside }
}

for (const kind of ['hard link', 'junction']) {
  test('writer refuses a spool ' + kind + ' and preserves the outside target', (t) => {
    const { spool, outside } = setup(t)
    const canary = join(outside, 'canary.txt')
    fs.writeFileSync(canary, 'canary\n')
    fs.mkdirSync(dirname(spool.directory), { recursive: true })
    if (kind === 'junction') fs.symlinkSync(outside, spool.directory, 'junction')
    else {
      fs.mkdirSync(spool.directory)
      fs.linkSync(canary, spool.eventsPath)
    }
    try {
      spool.enqueue({ marker: 'probe' })
      spool.flushSync()
      assert.equal(spool.health().unavailable, true)
      assert.equal(spool.queueDepth, 1)
      assert.equal(fs.readFileSync(canary, 'utf8'), 'canary\n')
      assert.deepEqual(fs.readdirSync(outside), ['canary.txt'])
      spool.close()
    } finally {
      if (kind === 'junction') fs.unlinkSync(spool.directory)
    }
  })
}

test('the direct writer normalizes traversal IDs and preserves foreign temp files', (t) => {
  const { spool, outside } = setup(t, { sourceInstanceId: '../../outside', rotatedTtlMs: 1000 })
  fs.mkdirSync(spool.directory, { recursive: true })
  const foreign = join(spool.directory, 'unrelated.tmp')
  fs.writeFileSync(foreign, 'keep')
  fs.utimesSync(foreign, 1, 1)
  spool.enqueue({ marker: 'probe' })
  spool.flushSync()
  assert.deepEqual(fs.readdirSync(outside), [])
  assert.equal(fs.readFileSync(foreign, 'utf8'), 'keep')
  assert.equal(spool.health().counters.written, 1)
})

test('a failed reopen after rotation leaves the queue head for the next flush', (t) => {
  let opens = 0
  const disk = { ...fs, openSync(file, ...args) {
    if (String(file).endsWith('events.jsonl') && ++opens === 2) throw new Error('fixture reopen denied')
    return fs.openSync(file, ...args)
  } }
  const { spool } = setup(t, { fs: disk, eventFileBytes: 1024 })
  spool.enqueue({ marker: 'first', pad: 'x'.repeat(1100) })
  spool.flushSync()
  spool.enqueue({ marker: 'second' })
  spool.flushSync()
  assert.equal(spool.queueDepth, 1)
  assert.equal(spool.health().dropped, 0)
  spool.flushSync()
  assert.equal(spool.queueDepth, 0)
  assert.equal(JSON.parse(fs.readFileSync(spool.eventsPath, 'utf8')).marker, 'second')
})
