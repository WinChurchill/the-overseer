/** Fixed host timelines through the shipped mappers, observer and spool writers.
 * Expectations below are authored contracts, never inferred from emitted state.
 * --check regenerates in isolation and compares bytes without updating fixtures.
 */
import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { buildRecord, PRODUCER_VERSION } from '../../claude-code/lib/mapping.mjs';
import { Deadline, publishRecord } from '../../claude-code/lib/spool.mjs';
import { createObserver } from '../../deepseek-harness/src/index.js';

const target = path.dirname(fileURLToPath(import.meta.url));
const temporary = fs.mkdtempSync(path.join(os.tmpdir(), 'overseer-behavior-'));
const start = Date.parse('2026-01-01T00:00:00Z');
const iso = seconds => new Date(start + seconds * 1000).toISOString();
const privateField = () => { throw new Error('PRIVATE_CONTENT_OPENED'); };
const secret = value => Object.defineProperty(value, 'content', { get: privateField });
const catalog = [];
let serial = 0;

function producer(harness, clock, epoch = 'E1', sessionId = 'session') {
  const root = path.join(temporary, String(serial++));
  fs.mkdirSync(root, { recursive: true });
  let offset = 0;
  let observer;
  let registration;
  let session;
  let listeners;
  let active = [];
  let mode = true;
  let events = [];
  let directory = root;
  if (harness === 'dsh') {
    const cells = new WeakMap();
    registration = { cells, def: { stateVersion: 2 } };
    listeners = new Map();
    const live = [];
    session = { id: sessionId, header: { cwd: 'C:/synthetic', createdAt: start }, seq: 0, eventAt: i => events[i] };
    const ctx = {
      on: (name, handler) => listeners.set(name, handler),
      get: name => name === 'sessions' ? { list: () => live }
        : name === 'sessionProjections' ? { registrations: new Map([['userQuestions', registration]]) } : undefined,
    };
    observer = createObserver({ spoolRoot: root, sourceInstanceId: 'behavior', sourceEpoch: epoch,
      now: () => start + clock.seconds * 1000, currentMinIntervalMs: 0 });
    observer.attach(ctx);
    observer.spool.flushSync();
    directory = observer.spool.directory;
    offset = fs.readFileSync(path.join(directory, 'events.jsonl'), 'utf8').length; // source-only attach metadata
    live.push(session);
    cache();
    listeners.get('session/created')(session);
  } else {
    claude('SessionStart');
  }
  function cache(shape = 'valid') {
    if (shape === 'missing') { registration.cells.delete(session); return; }
    registration.def.stateVersion = shape === 'unsupported' ? 999 : 2;
    registration.cells.set(session, { observedSeq: session.seq - (shape === 'lag' ? 2 : 1),
      state: { timed: mode, questions: Object.defineProperty({ active: active.map(callId =>
        Object.defineProperty({ callId, state: 'continued' }, 'questions', { get: privateField })) },
      'settled', { get: privateField }) } });
  }
  function claude(event, fields = {}) {
    const payload = secret({ session_id: sessionId, cwd: 'C:/synthetic', hook_event_name: event,
      agent_id: 'same-agent', ...fields });
    for (const name of ['tool_input', 'tool_response', 'prompt', 'message'])
      Object.defineProperty(payload, name, { get: privateField });
    const record = buildRecord(payload, { sourceInstanceId: 'behavior', sourceEpoch: epoch,
      observedAtUtc: iso(clock.seconds) });
    const result = publishRecord({ dir: directory, record, deadline: new Deadline(250) });
    assert.equal(result.ok, true);
  }
  async function dsh(type, data = {}, ids = active, options = {}) {
    active = ids;
    mode = options.timed ?? mode;
    const event = { type, data: secret(data), seq: session.seq++, time: start + clock.seconds * 1000 };
    events.push(event);
    listeners.get('session/event')(session, event);
    cache(options.shape);
    await Promise.resolve();
  }
  return {
    claude, dsh,
    async begin(turn = 1) { if (harness === 'claude') claude('UserPromptSubmit', { prompt_id: 'parent-' + turn });
      else await dsh('turn/start', { turn }); },
    async ask(id, ids = [id], options = {}) { if (harness === 'claude') claude('PreToolUse', { tool_name: 'AskUserQuestion', tool_use_id: id });
      else await dsh('tool/call', { turn: options.turn ?? 1, name: 'ask_user_question', callId: id }, ids, options); },
    async answer(id, ids = []) { if (harness === 'claude') claude('PostToolUse', { tool_name: 'AskUserQuestion', tool_use_id: id });
      else await dsh('user/message', {}, ids); },
    async heartbeat(shape = 'valid') { if (harness === 'claude') claude('SubagentStop');
      else { cache(shape); observer.publishHeartbeat(); } },
    take() {
      observer?.spool.flushSync();
      const raw = fs.readFileSync(path.join(directory, 'events.jsonl'), 'utf8');
      const records = raw.slice(offset).trim().split('\n').filter(Boolean).map(line => JSON.parse(line));
      offset = raw.length;
      const current = observer ? JSON.parse(fs.readFileSync(path.join(directory, 'current.json'), 'utf8')) : null;
      assert.ok(!JSON.stringify({ records, current }).includes('SECRET_'));
      return { records, current };
    },
    close() { observer?.dispose(); },
  };
}

async function scenario(harness, id, run) {
  const clock = { seconds: 0 };
  let p = producer(harness, clock);
  const steps = [];
  function checkpoint(state, attention = [], options = {}) {
    const output = p.take();
    const needsHuman = state === 'NeedsInput' || state === 'NeedsApproval';
    const uncertain = options.uncertain ?? false;
    const stale = options.stale ?? false;
    const current = !uncertain && !stale;
    const step = {
      at: iso(clock.seconds), records: output.records,
      expected: { state, needsHuman, uncertain, stale, current,
        complete: options.complete ?? current, attention, watermark: iso(options.watermark ?? clock.seconds) },
    };
    if (options.snapshot) step.current = output.current;
    if (options.restart) step.restart = true;
    if (options.operation) step.operation = options.operation;
    if (options.records) step.records = options.records;
    if (options.current) step.current = options.current;
    steps.push(step);
    return step;
  }
  await Promise.resolve();
  checkpoint('Idle', [], { snapshot: harness === 'dsh' });
  async function tick(action, state, attention = [], options = {}) {
    clock.seconds++;
    await action();
    return checkpoint(state, attention, options);
  }
  await run({ tick, checkpoint, clock, get p() { return p; },
    replace(epoch) { p.close(); p = producer(harness, clock, epoch); return p; }, steps });
  p.close();
  const document = { schemaVersion: 1, harness, id, steps };
  if (id.includes('S1')) {
    document.identitySessions = [];
    for (const sessionId of ['s'.repeat(127), 's'.repeat(128), 's'.repeat(128) + 'A', 's'.repeat(128) + 'B'.repeat(128)]) {
      const probe = producer(harness, { seconds: 0 }, 'E1', sessionId);
      await probe.begin();
      await probe.ask('question');
      const output = probe.take();
      document.identitySessions.push(...output.records);
      assert.ok(output.records.every(r => r.sessionId.length <= 128 && r.eventId.length <= 128 && (r.turnId?.length ?? 0) <= 128));
      if (output.current) assert.ok(output.current.sessions.every(s => s.sessionId.length <= 128 && (s.openTurnId?.length ?? 0) <= 128));
      probe.close();
    }
  }
  if (id.includes('S4')) {
    const peer = producer(harness === 'claude' ? 'dsh' : 'claude', { seconds: 0 });
    await peer.begin();
    await peer.ask('same-id');
    document.peerRecords = peer.take().records;
    peer.close();
  }
  const bytes = JSON.stringify(document, null, 2) + '\n';
  const file = path.join(target, `${id}.json`);
  if (process.argv.includes('--check')) assert.equal(fs.readFileSync(file, 'utf8'), bytes, `${id}: fixture drift`);
  else fs.writeFileSync(file, bytes);
  catalog.push(id);
}

try {
  // Highest-risk shared contracts are deliberately generated first.
  for (const harness of ['claude', 'dsh']) {
    await scenario(harness, `${harness}-S1-identities`, async s => {
      await s.tick(() => s.p.begin(), 'Working');
      const ids = ['x'.repeat(127), 'x'.repeat(128), 'x'.repeat(128) + 'A', 'x'.repeat(128) + 'B'.repeat(128)];
      for (let i = 0; i < ids.length; i++) await s.tick(() => s.p.ask(ids[i], ids.slice(0, i + 1)), 'NeedsInput', ['InputRequired']);
      const published = s.steps.flatMap(step => step.records).filter(r => r.kind === 'input/asked');
      assert.equal(new Set(published.map(r => r.requestId)).size, 4, 'distinct host IDs must remain distinct');
      assert.ok(published.every(r => r.requestId.length <= 128));
      for (let i = ids.length - 1; i >= 0; i--) await s.tick(() => s.p.answer(ids[i], ids.slice(0, i)), i === 0 ? 'Working' : 'NeedsInput');
    });
    await scenario(harness, `${harness}-S2-epochs`, async s => {
      await s.tick(() => s.p.begin(), 'Working');
      await s.tick(() => s.p.ask('old'), 'NeedsInput', ['InputRequired']);
      const oldStart = s.steps[0].records;
      // Produce the old epoch's matching-ID answer before replacing its producer,
      // but withhold these bytes from the consumer until E2 is waiting on that ID.
      await s.p.ask('new', ['old', 'new']);
      await s.p.answer('new', ['old']);
      const delayedAnswer = s.p.take().records.filter(r => r.kind === 'input/answered');
      assert.equal(delayedAnswer.length, 1);
      await s.tick(() => { s.replace('E2'); }, 'Idle', [], { snapshot: harness === 'dsh' });
      await s.tick(() => s.p.begin(2), 'Working');
      await s.tick(() => s.p.ask('new'), 'NeedsInput', ['InputRequired']);
      const prior = s.clock.seconds;
      await s.tick(() => {}, 'NeedsInput', [], { records: oldStart.concat(delayedAnswer), watermark: prior });
      await s.tick(() => s.p.answer('new'), 'Working');
    });
    await scenario(harness, `${harness}-S3-recovery`, async s => {
      await s.tick(() => s.p.begin(), 'Working');
      await s.tick(() => s.p.ask('q'), 'NeedsInput', ['InputRequired']);
      const pending = s.p.take().current;
      const lost = await s.tick(() => s.p.answer('q'), 'NeedsInput', [], { records: [], watermark: 2 });
      await s.tick(() => s.p.heartbeat(), 'NeedsInput', [], { uncertain: true });
      // Claude publishes no current.json: this is explicit consumer protocol fault injection,
      // not a claim that the hook producer can authoritatively recover lost answers.
      const snapshot = (at, sequence, inputs) => ({ schemaVersion: 1, harnessId: harness,
        sourceInstanceId: 'behavior', sourceEpoch: 'E1', producerVersion: harness === 'dsh'
          ? 'dsh-observer/1.1.0' : PRODUCER_VERSION, updatedAtUtc: iso(at), evictedSessions: 0,
        sessions: [{ sessionId: 'session', openTurnId: 'parent-1', lastSequence: sequence,
          lastObservedUtc: iso(at), pendingInputRequestIds: inputs, inputStateComplete: true, category: 'working' }] });
      const watermark = s.steps.at(-1).records.at(-1).sequence;
      await s.tick(() => {}, 'NeedsInput', [], { uncertain: true, watermark: 4,
        current: pending ?? snapshot(2, watermark - 2, ['q']) });
      s.clock.seconds += 901;
      s.checkpoint('NeedsInput', [], { stale: true, uncertain: true, watermark: 4 });
      await s.tick(() => s.p.begin(2), 'NeedsInput', [], { uncertain: true });
      const fresh = s.p.take().current;
      const freshAt = s.clock.seconds;
      const freshSequence = s.steps.at(-1).records.at(-1).sequence;
      await s.tick(() => {}, 'Working', [], { current: fresh ?? snapshot(freshAt, freshSequence, []), watermark: freshAt });
      const recovery = s.steps.at(-1).current;
      await s.tick(() => {}, 'Working', [], { restart: true, current: recovery,
        records: [], watermark: freshAt });
      assert.equal(lost.records.length, 0);
    });
    await scenario(harness, `${harness}-S4-transport`, async s => {
      await s.tick(() => s.p.begin(), 'Working');
      const ask = await s.tick(() => s.p.ask('same-id'), 'Working', [], { operation: 'split-start', watermark: 1 });
      await s.tick(() => {}, 'NeedsInput', ['InputRequired'], { records: [], operation: 'split-finish', watermark: 2 });
      await s.tick(() => s.p.answer('same-id'), 'NeedsInput', [], { operation: 'rotate-split-start', watermark: 2 });
      await s.tick(() => {}, 'Working', [], { records: [], operation: 'split-finish', watermark: 4 });
      assert.ok(ask.records.length > 0);
    });
  }
  await scenario('claude', 'claude-C1-questions', async s => {
    await s.tick(() => s.p.begin(), 'Working');
    await s.tick(() => s.p.ask('q1'), 'NeedsInput', ['InputRequired']);
    await s.tick(() => s.p.ask('q2'), 'NeedsInput', ['InputRequired']);
    await s.tick(() => s.p.answer('other'), 'NeedsInput');
    await s.tick(() => s.p.answer('q2'), 'NeedsInput');
    await s.tick(() => s.p.answer('q2'), 'NeedsInput');
    await s.tick(() => s.p.answer('q1'), 'Working');
  });
  await scenario('claude', 'claude-C2-auxiliary', async s => {
    await s.tick(() => s.p.begin(), 'Working');
    await s.tick(() => s.p.ask('q'), 'NeedsInput', ['InputRequired']);
    for (const event of ['SubagentStart', 'SubagentStop', 'PreCompact', 'PostCompact'])
      await s.tick(() => s.p.claude(event), 'NeedsInput');
    await s.tick(() => s.p.claude('PreToolUse', { tool_name: 'EnterPlanMode', tool_use_id: 'plan' }), 'NeedsInput');
    await s.tick(() => s.p.claude('PostToolUseFailure', { tool_name: 'Bash', tool_use_id: 'other' }), 'NeedsInput');
    await s.tick(() => s.p.answer('q'), 'Working');
  });
  await scenario('claude', 'claude-C3-approval', async s => {
    await s.tick(() => s.p.begin(), 'Working');
    await s.tick(() => s.p.claude('PermissionRequest'), 'NeedsApproval', ['PermissionRequired']);
    await s.tick(() => s.p.ask('q'), 'NeedsApproval', ['InputRequired']);
    await s.tick(() => s.p.claude('PermissionDenied'), 'NeedsApproval');
    await s.tick(() => s.p.claude('PermissionDenied', { tool_use_id: 'other' }), 'NeedsApproval');
    await s.tick(() => s.p.answer('q'), 'NeedsApproval');
    await s.tick(() => s.p.claude('Stop', { prompt_id: 'parent-1' }), 'Done', ['TaskCompleted']);
  });
  await scenario('claude', 'claude-C4-failure', async s => {
    await s.tick(() => s.p.begin(), 'Working');
    await s.tick(() => s.p.ask('old'), 'NeedsInput', ['InputRequired']);
    await s.tick(() => s.p.claude('StopFailure', { prompt_id: 'parent-1' }), 'Error', ['TaskError']);
    await s.tick(() => s.p.begin(2), 'Working');
    await s.tick(() => s.p.ask('new'), 'NeedsInput', ['InputRequired']);
    await s.tick(() => s.p.answer('old'), 'NeedsInput');
    await s.tick(() => s.p.claude('SubagentStop'), 'NeedsInput');
    await s.tick(() => s.p.answer('new'), 'Working');
  });
  await scenario('dsh', 'dsh-D1-timed', async s => {
    await s.tick(() => s.p.begin(), 'Working');
    await s.tick(() => s.p.ask('q'), 'NeedsInput', ['InputRequired']);
    await s.tick(() => s.p.dsh('tool/result', { turn: 1, message: { toolCallId: 'q' } }, ['q']), 'NeedsInput');
    await s.tick(() => s.p.dsh('turn/end', { turn: 1, reason: { kind: 'completed' } }, ['q']), 'NeedsInput');
    await s.tick(() => s.p.begin(2), 'NeedsInput');
    await s.tick(() => s.p.answer('q'), 'Working');
  });
  await scenario('dsh', 'dsh-D2-blocking', async s => {
    await s.tick(() => s.p.begin(), 'Working');
    await s.tick(() => s.p.ask('q', [], { timed: false }), 'NeedsInput', ['InputRequired']);
    await s.tick(() => s.p.dsh('tool/result', { turn: 1, message: { toolCallId: 'other' } }, [], { timed: false }), 'NeedsInput');
    const answered = await s.tick(() => s.p.dsh('tool/result', { turn: 1, message: { toolCallId: 'q' } }, [], { timed: false }), 'Working');
    await s.tick(() => s.p.dsh('turn/end', { turn: 1, reason: { kind: 'completed' } }, []), 'Done', ['TaskCompleted']);
    await s.tick(() => {}, 'Done', [], { records: answered.records, watermark: 5 });
    await s.tick(() => s.p.dsh('tool/call', { turn: 1, callId: 'q', name: 'ask_user_question' }, [], { timed: false }), 'Done');
  });
  await scenario('dsh', 'dsh-D3-priority', async s => {
    await s.tick(() => s.p.begin(), 'Working');
    await s.tick(() => s.p.dsh('approval/asked', { turn: 1, id: 'permission' }), 'NeedsApproval', ['PermissionRequired']);
    await s.tick(() => s.p.ask('q1'), 'NeedsApproval', ['InputRequired']);
    await s.tick(() => s.p.ask('q2', ['q1', 'q2']), 'NeedsApproval', ['InputRequired']);
    await s.tick(() => s.p.dsh('approval/decided', { id: 'permission', outcome: 'allowed-once' }, ['q1', 'q2']), 'NeedsInput');
    await s.tick(() => s.p.answer('q1', ['q2']), 'NeedsInput');
    await s.tick(() => s.p.answer('q2'), 'Working');
  });
  await scenario('dsh', 'dsh-D4-coverage', async s => {
    await s.tick(() => s.p.begin(), 'Working');
    await s.tick(() => s.p.ask('q'), 'NeedsInput', ['InputRequired']);
    for (const shape of ['lag', 'missing', 'unsupported'])
      await s.tick(() => s.p.heartbeat(shape), 'NeedsInput', [], { snapshot: true, uncertain: true });
    await s.tick(() => s.p.heartbeat(), 'NeedsInput', [], { snapshot: true });
    await s.tick(() => s.p.heartbeat(), 'NeedsInput', [], { snapshot: true, restart: true });
    await s.tick(() => s.p.answer('q'), 'Working');
  });
  assert.equal(catalog.length, 16);
  console.log(`${process.argv.includes('--check') ? 'Verified' : 'Generated'} ${catalog.length} fixed producer timelines.`);
} finally {
  // Only this process's uniquely created, checked temporary root is removed.
  assert.ok(path.resolve(temporary).startsWith(path.resolve(os.tmpdir()) + path.sep));
  fs.rmSync(temporary, { recursive: true, force: true });
}
