# overseer-dsh-observer

A small, host-native observer plugin for the official **DeepSeek Harness** (`dsh`). It runs
inside the user's existing `dsh` process and copies **allowlisted status metadata** — never
message text — into an OVERSEER-owned local spool. OVERSEER reads the spool as plain files and
never imports dsh code.

**Status: experimental; question detection needs an owner retest.** The original
contract was pinned to 0.1.7-rc.1 below. The target installation is
**DSH Desktop 0.2.0-rc.2**, using its dedicated `desktop` profile.
The installed session module still exposes `session/created`, `session/event`,
`session/disposed`, `Session.header`, `Session.seq`, `eventAt()` and
`ctx.sessions.list()`. The observer was installed into that profile on 2026-10-05;
the owner confirmed live permission escalation and resume. Producer **1.1.0** adds
blocking and timed question tracking; actual host question behavior is still pending.
Installation is once per host profile. A linked install loads updated code on host
restart; it does not need reinstalling with every OVERSEER download. A copied install
must retain its permanent directory and be deliberately updated while the host is quit.

---

## 1. Pinned source of truth

Everything this plugin assumes about the host was read from the read-only reference checkout at
commit `46a7f68b0922371ce7144b668b90e377d8e799f4` of <https://github.com/deepseek-ai/deepseek-harness>:

| Pinned file | Contract used here |
|---|---|
| [packages/core/session/src/index.ts](https://github.com/deepseek-ai/deepseek-harness/blob/46a7f68b0922371ce7144b668b90e377d8e799f4/packages/core/session/src/index.ts) | `session/created` ("a synchronous throw vetoes and rolls back with a paired disposal"), `session/event` ("post-commit, fire-and-forget append feed … observer failures are logged and contained"), `session/disposed`; `Session.header`, `Session.seq`, `SessionStore.list()` |
| [packages/core/session/src/types.ts](https://github.com/deepseek-ai/deepseek-harness/blob/46a7f68b0922371ce7144b668b90e377d8e799f4/packages/core/session/src/types.ts) | `SESSION_FORMAT_VERSION = 4`; header `id`/`createdAt`/`cwd`/`parentSession`; `SessionEvent` envelope `{ type, seq, time, data }`; `turn/start` `{ turn }`, `turn/end` `{ turn, reason }`; `TurnEndReason` variants |
| [packages/core/session/src/known-event-types.ts](https://github.com/deepseek-ai/deepseek-harness/blob/46a7f68b0922371ce7144b668b90e377d8e799f4/packages/core/session/src/known-event-types.ts) | the emitted vocabulary: `tool/call`, `tool/result`, `approval/asked`, `approval/decided`, `plan/mode`, `turn/start`, `turn/end` |
| [packages/interaction/user-approval/src/types.ts](https://github.com/deepseek-ai/deepseek-harness/blob/46a7f68b0922371ce7144b668b90e377d8e799f4/packages/interaction/user-approval/src/types.ts) | `approval/asked` `{ id, toolName, callId?, reason? }`, `approval/decided` `{ id, outcome }` — correlation by `id`; `ApprovalOutcome` = `allowed-once` \| `rejected` \| `cancelled` \| `unavailable` |
| [packages/plan/plan-mode/src/index.ts](https://github.com/deepseek-ai/deepseek-harness/blob/46a7f68b0922371ce7144b668b90e377d8e799f4/packages/plan/plan-mode/src/index.ts) | `plan/mode` is `{ active }: boolean`, "log-only, non-surface, whole-value replace. The last `plan/mode` wins" |
| [packages/bundle/base/cordis.patch.yml](https://github.com/deepseek-ai/deepseek-harness/blob/46a7f68b0922371ce7144b668b90e377d8e799f4/packages/bundle/base/cordis.patch.yml) | profile bundle patch format (`- insert:` root row list) and `dsh.bundle.patch` package metadata |

## 2. What it observes, and what it must never touch

Subscribed seams — observe-only, all synchronous and all wrapped so they cannot throw into the host:

| Seam | Why it is safe |
|---|---|
| `session/created` | Post-construction announcement. A synchronous listener throw can roll back publication, so the observer's listener body is fully contained. |
| `session/event` | Post-commit, fire-and-forget append feed; observer failures are contained by the host and cannot fail the committed append. |
| `session/disposed` | Emitted once when an announced session leaves the store. |

Deliberately **not** subscribed:

- `session/flush` — an awaited parallel durability checkpoint. Subscribing would put this adapter
  inside the host's durability path and violate the "no awaited barrier" rule.
- `approval/request` and the user-question answerer waterfalls — waterfalls decide execution. The
  observer registers no waterfall listener, returns no decision, and never influences approval or
  input flow. Approval activity is observed only from the log-only `approval/asked` /
  `approval/decided` audit events.

## 3. Normalized record schema

One JSON object per line in `events.jsonl`. Every record carries **exactly** these 19 keys, in this
order; unknown keys are never added, and unset values are `null`.

| Key | Type | Meaning |
|---|---|---|
| `schemaVersion` | number | `1` |
| `harnessId` | string | always `"dsh"` |
| `sourceInstanceId` | string | configured source id (default `dsh-default`) |
| `sourceEpoch` | string | stable id of one producer process/attach (`dsh-<pid>-<base36 time>-<random>`) |
| `sessionId` | string \| null | upstream session id; `null` for a source-scoped record |
| `sequence` | number | per-session sequence (see §4) |
| `eventId` | string | `dsh:<sessionId>:<sequence>`, `dsh:*:<sequence>` when `sessionId` is null; stable across duplicate delivery |
| `observedAtUtc` | string | collector receipt time (ISO 8601) |
| `eventAtUtc` | string \| null | upstream event time (`SessionEvent.time`, ISO 8601) |
| `kind` | string | one of the kind allowlist below |
| `turnId` | string \| null | `dsh:<sessionId>:turn:<n>` |
| `callId` | string \| null | upstream tool-call id (`tool/call.callId`, `tool/result.message.toolCallId`, `approval/asked.callId`) |
| `requestId` | string \| null | upstream approval request id (`approval/asked.id` / `approval/decided.id`) |
| `projectRoot` | string \| null | session header `cwd`, bounded to 512 characters |
| `category` | string | one of the category allowlist below |
| `evidence` | string | `explicit` \| `inferred` \| `unknown` |
| `outcome` | string \| null | `completed` \| `aborted` \| `error` \| `ok` \| `failed` \| `null` |
| `hydration` | boolean | `true` marks replayed/current-state records so OVERSEER can suppress voice |
| `producerVersion` | string | `dsh-observer/1.1.0` |

**Kind allowlist:** `session/start`, `session/end`, `turn/start`, `turn/end`, `tool/start`,
`tool/end`, `approval/asked`, `approval/decided`, `input/asked`, `input/answered`, `plan/mode`,
`heartbeat`, `snapshot`.

**Category allowlist:** `thinking`, `planning`, `working`, `waiting`, `error`, `complete`,
`idle`, `unknown`.

Upstream event → record mapping (reads only the named scalar fields; every other field is never
opened):

| Upstream `SessionEvent` | `kind` | `category` | `outcome` | Fields read |
|---|---|---|---|---|
| `turn/start` | `turn/start` | `working` | `null` | `data.turn` |
| `turn/end` reason `completed` | `turn/end` | `complete` | `completed` | `data.turn`, `data.reason.kind` |
| `turn/end` reason `aborted` | `turn/end` | `idle` | `aborted` | idem |
| `turn/end` reason `error` | `turn/end` | `error` | `error` | idem |
| `turn/end` reason `max-tokens` / `blocked` / `forked` | `turn/end` | `unknown` | `aborted` | idem |
| `turn/end` reason `interrupted` | `turn/end` | `error` | `aborted` | idem |
| `turn/end` reason outside the known set | `turn/end` | `unknown` | `null` | `evidence = unknown` |
| `tool/call` | `tool/start` | `working` | `null` | `data.turn`, `data.callId`; exact `name` classifies questions, never exported; arguments never read |
| `tool/result` | `tool/end` | `working` | `ok` / `failed` from `message.isError` | `data.turn`, `data.message.toolCallId`, `data.message.isError` (never `content`, never `meta`) |
| `approval/asked` | `approval/asked` | `waiting` | `null` | `data.id`, `data.callId` (never `toolName`, never `reason`) |
| `approval/decided` | `approval/decided` | `working` \| `idle` \| `error` | `ok` \| `failed` \| `aborted` \| `error` | `data.id`, `data.outcome` |
| `plan/mode` `active: true` | `plan/mode` | `planning` | `null` | `data.active` |
| `plan/mode` `active: false` | `plan/mode` | `unknown` | `null` | `data.active` |
| observer timer | `heartbeat` | tracked category (`idle` \| `waiting` \| `working`) | `null` | none (`evidence = inferred`) |
| observer attach | `snapshot` | tracked category (`idle` \| `waiting` \| `working`) | `null` | none (`evidence = inferred`, `hydration = true`) |
| `session/created` | `session/start` | `unknown` | `null` | session header `id`/`cwd`/`createdAt`/`parentSession` |
| `session/disposed` | `session/end` | `unknown` | `ok`, or `aborted` when a turn was open | none |

A failed tool (`tool/end` + `outcome: failed`) is **not** a task failure: it stays local to that
record and leaves the turn open.

Producer 1.1.0 also emits `input/asked` and `input/answered` with both `callId` and
`requestId` equal to the real `ask_user_question` call ID. Blocking calls settle on
their own result/end. Timed calls remain pending across results and turn completion
until DSH's native active set removes them (a queued reply is insufficient).
Only `timed`, active IDs, `open`/`continued` states and a cursor are inspected.
The installed `dsh-session-projection` and `dsh-user-questions` 0.2.0-rc.2 sources
establish the guarded `registrations.get('userQuestions').cells.get(session)` cache
layout and question state version 2. This is an internal, version-specific seam:
there is no call to `stateOf()`, native wire validation, full-log fold or answerer.
Missing/stale/changed/oversized metadata preserves known waits and sets
`inputStateComplete: false`. The consumer exposes incomplete coverage.
Cold blocking recovery reads at most 256 trailing events; a missing turn boundary
leaves coverage incomplete. Timed recovery uses the already-materialized native cache.

## 4. Behaviour rules

- **Sequence.** `sequence` numbers **published** records contiguously: every exported record takes
  `lastSequence + 1`, whether it came from a host event or from the observer itself (`session/start`,
  `session/end`, `heartbeat`, `snapshot`). The host's `seq` is deliberately **not** the published
  sequence: most host events are never exported, so using it would leave holes, and a hole is exactly
  how a consumer detects a LOST record — a hole must mean a lost record, never "the host did something
  we do not export". `eventId` stays `dsh:<sessionId>:<sequence>`, sequence is strictly increasing per
  session, and downstream validation is per source epoch **and** per session. `current.json` still
  reports the highest host seq seen as `lastHostSeq`.
- **Duplicates and hydration.** Re-delivery of the same logical event (same host `seq`) reuses its
  original `sequence`, so the `eventId` is identical, and sets `hydration: true`. Reconnects and
  attach snapshots are therefore idempotent and voice-suppressible. The duplicate window is bounded
  (1024 host seqs per session).
- **Turn lifecycle.** The tracked open turn is set by `turn/start` and cleared by `turn/end`.
  A turn that already ended is never reopened: a late `tool/call` or `tool/result` for a closed
  turn is still published (the observation is real) with `category: unknown` and
  `evidence: inferred`, and the tracked turn and category stay unchanged. Attaching mid-turn adopts
  the observed turn without inventing a `turn/start`.
- **No invented activity.** Activity records exist only for observed events. Silence produces
  nothing except `heartbeat`. A heartbeat reports the tracked category (`idle` when no turn is
  open), never a fabricated turn or tool.
- **Plan mode.** `plan/mode` is published with `evidence: explicit` and `category: planning`
  **only while plan mode is actually on**; switching off publishes `category: unknown`. The
  `planning` category appears on that record alone — it is never smeared across later records,
  heartbeats, or snapshots, so plan mode can never turn into a sustained planning duration. Its
  state travels as the explicit `planMode` boolean in `current.json`.
- **Attach snapshot.** On attach/restart the observer publishes one `snapshot` record per known live
  session (or one source-scoped snapshot when no session is known), all with `hydration: true`, then
  a heartbeat every 30 s (configurable). At attach it reads only each session's header and the last
  256 trailing event types plus `turn`/`active` scalars to recover the current turn and plan state.
- **Forks and child sessions.** Child sessions are exported as first-class sessions, never
  discarded. Fork lineage (`header.parentSession`) is exported in `current.json` as
  `parentSessionId` — the normalized record schema in §3 has no lineage key, and adding one would
  break the frozen key list, so lineage lives in the current-state document instead.
- **Session end.** `session/disposed` publishes `session/end` with `outcome: ok`, or
  `outcome: aborted` ("silent cancellation") when a turn was still open.

## 5. Data locations and files

```text
<spoolRoot>/dsh/<sourceInstanceId>/
  events.jsonl                     append-only, one complete JSON record per line
  current.json                     atomically replaced current-state document
  health.json                      atomically replaced writer health
  events-<utcstamp>.jsonl          rotated event files (OVERSEER-owned, TTL-bounded)
  <name>.<pid>.<n>.tmp             transient atomic-replacement files (same directory)
```

Default `spoolRoot`: `%LOCALAPPDATA%\OVERSEER\bridges` on Windows, else
`$XDG_STATE_HOME/overseer/bridges` (falling back to `~/.local/state/overseer/bridges`).

The spool root lives inside OVERSEER's **data directory**, not inside the extracted
application folder. Installing, moving or removing an OVERSEER version therefore never
moves or orphans an installed bridge, and `--data-directory PATH` keeps an isolated run
separate from the real one. OVERSEER discovers spool directories when it composes its
monitor at startup, so **restart OVERSEER after installing or changing this bridge**;
hot attach is not implemented and is not claimed. Restart `dsh` after installing the
plugin so the profile bundle is loaded.

`current.json` (atomic, one document):

```json
{
  "schemaVersion": 1,
  "harnessId": "dsh",
  "sourceInstanceId": "dsh-default",
  "sourceEpoch": "dsh-12345-abc-def",
  "producerVersion": "dsh-observer/1.1.0",
  "updatedAtUtc": "2026-01-01T00:00:00.000Z",
  "evictedSessions": 0,
  "sessions": [
    {
      "sessionId": "…", "parentSessionId": null, "projectRoot": "C:\\work",
      "createdAtUtc": "…", "firstSeenUtc": "…", "lastObservedUtc": "…",
      "openTurnId": "dsh:…:turn:3", "planMode": false, "pendingApprovalRequestId": null,
      "pendingInputRequestIds": [], "inputStateComplete": true,
      "category": "working", "disposed": false,
      "lastSequence": 42, "lastHostSeq": 40, "lastKind": "tool/start",
      "lastEventId": "dsh:…:42", "records": 43
    }
  ]
}
```

`health.json`: `version`, `lastWrite`, `queueDepth`, `dropped`, `counters` (enqueued, written,
bytesWritten, droppedOldest, droppedOversize, droppedError, droppedClosed, writeErrors, unavailable,
rotations, pruned, expired, currentWrites, healthWrites), `lastError`, `unavailable`,
`sourceInstanceId`, `sourceEpoch`, `producerVersion`, `startedAtUtc`, `harnessId`, `directory`.

## 6. Bounds, caps and failure behaviour

| Bound | Value | Behaviour at the bound |
|---|---|---|
| In-process queue | 512 records (env-clamped 16..8192) | drops the **oldest** record, increments `dropped`/`droppedOldest`; the callback never blocks and never touches disk |
| Record size | 64 KiB serialized | oversized record refused, `droppedOversize` counted |
| Event file rotation | 4 MiB | `events.jsonl` renamed to `events-<utcstamp>.jsonl`, `rotations` counted; the record that triggered the rotation is written to the fresh file, never swallowed |
| Rotated-file TTL | 1 hour | expired OVERSEER-owned rotated files and stale `.tmp` files are deleted (`pruned`/`expired`) |
| Spool soft cap | 16 MiB | oldest rotated files pruned until under the cap |
| File cap | 4096 files | oldest rotated files pruned until under the cap |
| In-memory sessions | 512 | least-recently-observed session state evicted (`evictedSessions` counted); host-seq identity makes later records idempotent anyway |

Cleanup stays inside `<spoolRoot>/dsh/<sourceInstanceId>/` and only removes files matching this
adapter's rotated-event or temp naming. Unrelated files in the same directory are never touched.

Failure behaviour: every spool method is total. A failure to create/open the directory, a write
error, an unserializable record, or a full queue is recorded in `health.json` counters; the host
callback returns normally. Disk errors never crash, block, or throw into `dsh`.

Durability note: appends are single `write(2)` calls on a descriptor opened `'a'`; the observer
does not `fsync`. The spool is a status projection, not the host's system of record.

## 7. Configuration

| Knob | Default | Clamp |
|---|---|---|
| `OVERSEER_DSH_SPOOL` | `%LOCALAPPDATA%/OVERSEER/bridges` (Windows), else `$XDG_STATE_HOME/overseer/bridges` | — |
| `OVERSEER_DSH_SOURCE_ID` | `dsh-default` | restricted to `[A-Za-z0-9._-]`, 128 chars (it is one directory segment) |
| `OVERSEER_DSH_QUEUE` | `512` | 16..8192 |
| `OVERSEER_DSH_HEARTBEAT_SECONDS` | `30` | 5..300 |
| `OVERSEER_DSH_DISABLED` | unset | `1` (or `true`) disables the observer entirely: no subscription, no timer, no write |

Configuration is environment-only by design; the shipped bundle row carries no `config` block. The
plugin's `apply(ctx, config)` also accepts the same values programmatically (used by the tests), and
`dsh` passes a row `config` through unchanged if a deployment chooses to set one.

## 8. Install (opt-in, reversible)

This package is a profile bundle: `package.json` declares `dsh.bundle.patch`, and
`cordis.patch.yml` inserts one row, `overseer-dsh-observer`. Keep this directory in
a permanent location before installing it; do not install from a disposable test
extraction. Nothing is installed by reading these instructions.

### DSH Desktop (current target)

Desktop owns `$DSH_HOME/profiles/desktop`, separate from terminal profiles. Use its
Plugins page while it is running. The alternative is the **Desktop-bundled CLI**
after starting Desktop once and fully quitting it. A separately npm-installed
`dsh` must not manage the desktop profile. See the
[official Desktop runtime guide](https://github.com/deepseek-ai/deepseek-harness/blob/master/apps/desktop/README.md).

For the default per-user Windows installation, run by the user:

```powershell
$dshDesktopCli = Join-Path $env:LOCALAPPDATA 'Programs/DeepSeek Harness/resources/runtime/cli/bin/dsh.cmd'
& $dshDesktopCli plugin --profile desktop list
& $dshDesktopCli plugin --profile desktop add '<absolute permanent path to this directory>'
```

Use the installed app's matching bundled CLI if its location differs. Fully quit
Desktop before these CLI operations, then relaunch it. No manual edits to profile
YAML or package files are needed. Desktop includes its own Node runtime; a `dsh`
command on the normal PATH is not a prerequisite.

### Terminal profiles (legacy target)

For a separate terminal-hosted profile, use that host's own matching CLI:

```sh
dsh plugin --profile <profile-name> add "<absolute permanent path to this directory>"
```

The pinned 0.1.7 contract is retained in section 1. Do not substitute a terminal
profile for the Desktop profile when qualifying the user's actual workflow.

### Confirm the integration

After installation, verify that the selected profile lists the observer bundle.
Read `<spoolRoot>/dsh/<sourceInstanceId>/health.json`: `lastWrite` should advance
and `queueDepth` should return to zero. An empty host should publish a `snapshot`
with `sessions: []`.

Restart OVERSEER after installing the bridge. In a real Desktop session, check
working -> waiting for approval -> resumed -> completed/aborted, and verify
that OVERSEER reports those states without capturing message bodies or changing
any approval decision. Observer 1.1.0 also detects Desktop 0.2.0-rc.2 question
metadata: test question -> correlated reply -> resumed, timed continuation and
quiet restart. Missing or changed question metadata reduces coverage; other host
versions need qualification. Test detach and relaunch as well. Record the exact Desktop
version and source/spool identity. Until that run exists, the bridge is experimental.

### Remove

Use Desktop's Plugins page, or fully quit Desktop and run:

```powershell
& $dshDesktopCli plugin --profile desktop remove overseer-dsh-observer
```

For a terminal profile, use its CLI with that profile name instead. Restart the
host and OVERSEER, verify the bundle is absent, and confirm monitoring degrades
honestly. Existing OVERSEER spool files are left behind; they can be removed
separately when the producer is stopped. Keep unrelated profile entries intact.

Repository automation does not install the plugin, edit live `~/.dsh` configuration
or read the harness credential store. Local contract tests use isolated spools.

## 9. Privacy statement

- Exported: the 19 keys in §3 and nothing else. Message bodies, reasoning text, system/developer
  prompts, tool arguments, tool results, tool names, approval reasons, request headers, model
  configuration, credentials, and environment values are **never read**, not merely never written.
- The mapping switches on the event `type` first, so unmapped events — `user/message`,
  `assistant/message`, `assistant/attempt`, `system/message`, `developer/message`,
  `request/header`, `request/context`, `tool/ptc-dispatch`, and every other vocabulary member —
  are never opened at all. `tool/result` reads exactly two fields: `message.toolCallId` and
  `message.isError`.
- Paths and ids are bounded (project root 512 chars, ids 256 chars) before they reach a record.
- The spool is local files owned by OVERSEER. The plugin performs no network I/O, starts no
  subprocess, reads no credential store, and imports nothing from `dsh` (Node standard library
  only). `fixtures/` contains invented data only.

## 10. Tests

```sh
cd integrations/deepseek-harness
node --test --test-concurrency=1
```

Plain Node (`node --test`, Node >= 20). No dependencies, no package install, no TypeScript build
step: the plugin is plain ESM with JSDoc types. Tests use the real filesystem in `os.tmpdir()` and
a worker-thread reader; `fixtures/` holds sanitized synthetic event shapes.

| Test | Requirement covered |
|---|---|
| `(a) host callbacks never throw when the spool target is unwritable` | disk failure cannot reach the host (the `session/created` rollback hazard) |
| `(a) a disk failure degrades to counters without throwing` | error degradation to counters |
| `(b) queue overflow drops the oldest records …` | bounded queue, drop-oldest, drop counting, no disk I/O in the caller path |
| `(c) concurrent observation keeps a strictly increasing sequence per session` | ordering under interleaved sessions |
| `(d) duplicate publication keeps the same eventId and flags hydration` | idempotent re-delivery across epochs |
| `(e) attach publishes hydration-flagged snapshots, then heartbeats` | snapshot/hydration flags, plan-mode and turn recovery |
| `(f) a late tool-start after turn end never reopens the turn` | the documented late-event rule |
| `(g) the 64 KiB record cap is enforced at the exact boundary` | record cap |
| `(h) atomic current.json replacement leaves a complete document for a concurrent reader` | atomic replacement under a real concurrent reader |
| `the fixtures' message bodies, … never reach the spool` | privacy: canaries for bodies, arguments, outputs, reasons, credentials |
| `the observer subscribes to exactly the three observe-only seams` | no `session/flush`, no waterfall |
| rotation / TTL / file-cap / health tests | bounded cleanup of this adapter's own files only |

## 11. Known limitations

- **Question support is version-specific.** 1.0.0 does not emit question events and remains
  Unsupported. 1.1.0 targets the inspected Desktop 0.2.0-rc.2 cache. A host update that changes
  its shape/version can degrade question coverage until requalified. At most 32 pending IDs
  are accepted per session; an oversized native set is never treated as a complete empty set.
- **`thinking` is reserved, not emitted.** Reasoning text is never read, so the observer cannot
  distinguish "thinking" from "working" inside an open turn: an open turn reports `working`, or
  `waiting` while an approval or question is pending. `planning` is emitted only by an explicit `plan/mode`
  record (see §4).
- **Lineage is in `current.json`, not in a record.** The normalized key list is frozen at 19 keys;
  `parentSessionId` is exported in the current-state document.
- **No `fsync`.** A hard power loss can lose the newest lines; the spool is a status projection.
- **Bounded memory.** Session state (512 sessions) and the duplicate window (1024 host seqs per
  session) are bounded; a re-delivery older than the window is published as a new record.
- **No peer dependency on `@deepseek-ai/dsh`.** The plugin imports nothing from `dsh`, so the
  profile installer's DSH peer-compatibility check has nothing to evaluate; compatibility is with
  the documented event seam at the pinned commit, and live qualification is OVERSEER's step.

## 12. Layout

```text
src/normalize.js   allowlisted mapping from dsh session events to record fields (no state)
src/state.js       per-session state, sequence allocation, duplicate/hydration, snapshots
src/spool.js       bounded queue, atomic files, rotation, caps, health
src/index.js       Cordis plugin: seams, attach snapshot, heartbeat, env configuration
test/              node --test suites for the behaviours above
fixtures/          sanitized synthetic event shapes + the concurrent-reader worker
cordis.patch.yml   the single inserted profile row
NOTICE.md          origin and licensing statement
```
