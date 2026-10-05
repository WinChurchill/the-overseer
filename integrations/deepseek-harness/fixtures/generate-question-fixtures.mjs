/** Synthetic production-observer/spool output for consumer restart tests. */
import { mkdirSync, copyFileSync, writeFileSync, mkdtempSync, readdirSync, readFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { dirname, join } from 'node:path'
import { fileURLToPath } from 'node:url'
import { createObserver } from '../src/index.js'

const outRoot = join(dirname(fileURLToPath(import.meta.url)), 'spool')
const root = mkdtempSync(join(tmpdir(), 'overseer-question-fixture-'))
const session = { id: 'question-fixture', header: { cwd: 'C:/synthetic', createdAt: 1767225600000 }, seq: 0, eventAt: () => undefined }
const cells = new WeakMap()
const listeners = new Map()
function cache(ids) {
  cells.set(session, { observedSeq: session.seq - 1, state: { timed: true, questions: {
    active: ids.map((callId) => ({ callId, state: 'continued', questions: 'SECRET_QUESTIONS' })),
    settled: 'SECRET_ANSWERS',
  } } })
}
cache([])
const ctx = {
  on(name, handler) { listeners.set(name, handler) },
  get(name) { return name === 'sessions' ? { list: () => [session] } : name === 'sessionProjections' ? { registrations: new Map([['userQuestions', { cells, def: { stateVersion: 2 } }]]) } : undefined },
}
const observer = createObserver({ spoolRoot: root, sourceInstanceId: 'dsh-fixture', sourceEpoch: 'question-fixture-epoch', now: () => 1767225600000, currentMinIntervalMs: 0 })
observer.attach(ctx)
async function event(type, data, ids) {
  const next = { type, seq: session.seq++, time: 1767225600000, data }
  listeners.get('session/event')(session, next)
  cache(ids)
  await Promise.resolve()
}
function save(name) {
  observer.publishHeartbeat()
  observer.spool.flushSync()
  const target = join(outRoot, name, 'root', 'dsh', 'dsh-fixture')
  mkdirSync(target, { recursive: true })
  for (const file of readdirSync(observer.spool.directory)) copyFileSync(join(observer.spool.directory, file), join(target, file))
  // Preserve all producer protocol bytes; replace only its diagnostic directory
  // so the committed fixture is deterministic and contains no machine account.
  const healthPath = join(target, 'health.json')
  const health = JSON.parse(readFileSync(healthPath, 'utf8'))
  health.directory = 'C:/synthetic/bridges/dsh/dsh-fixture'
  writeFileSync(healthPath, JSON.stringify(health) + '\n')
  writeFileSync(join(outRoot, name, 'expected.json'), JSON.stringify({ sessions: [session.id], producer: 'dsh-observer/1.1.0', synthetic: true }, null, 2) + '\n')
}
await event('turn/start', { turn: 1 }, [])
await event('tool/call', { turn: 1, callId: 'q1', name: 'ask_user_question', arguments: 'SECRET_ARGUMENTS' }, ['q1'])
await event('tool/result', { turn: 1, message: { toolCallId: 'q1', content: 'SECRET_PENDING_RESULT' } }, ['q1'])
await event('turn/end', { turn: 1, reason: 'completed' }, ['q1'])
save('question-pending')
cells.delete(session)
save('question-unavailable')
await event('user/message', { content: 'SECRET_REPLY' }, [])
save('question-answered')
observer.dispose()
console.log('Generated three synthetic question spools from the shipped observer.')
