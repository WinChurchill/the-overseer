# OVERSEER Implementation State

## Current
Plan: P1 — Foundation and Monitoring
Goal: G1 — Trustworthy normalized live monitor
Branch: impl/p1-monitoring

## Plan Status
- Astra handoff: ready
- DeepSeek implementation: complete on this branch (8 logical commits, see Completed)
- Build: passed (0 warnings, 0 errors)
- Tests: passed (117 passed, 0 failed, 0 skipped)
- Smoke: passed (finite live `--monitor-debug` run, exit 0)
- Astra review: pending

## Completed

Bootstrap baseline at `1fc5f75fc74f412be645702cfb4afc046c1b11c5` is preserved; the
four projects, TheOverseer.* names, frozen `AGENTS.md`, frozen
`docs/V1_2_IMPLEMENTATION_SPEC.md` and the original PNGs are untouched. No new
project, runtime NuGet package, database or framework was added. Astra's earlier
ledger edit is included in this file's history.

Commits on `impl/p1-monitoring` (base `main` = 1fc5f75):

| Commit | Scope |
| --- | --- |
| `e6b6f8d` | Upstream MIT license and exact adapted-path provenance (`third_party/codex-monitor-hud/LICENSE`, `THIRD_PARTY_NOTICES.md`) |
| `dd7f991` | Immutable models, monitor options/limits, profiles, bounded incremental + tail readers |
| `ea32e6f` | Bounded resumable discovery, byte-bounded identity, bounded title index, watcher with bounded hints |
| `a296afe` | Structural parser + narrow command classifier, state reducer, current-action classifier, attention detector |
| `00eaaea` | `CodexMonitor` composition, single-owner loop, config store, bounded rolling log |
| `b4b0196` | App `--monitor-debug` harness, console interop, optional read-only state-store enrichment |
| `a1e805a` | 117-test synthetic matrix |
| docs commit | README and this ledger |

### Implemented behaviour

- Pipeline: bounded readers → structural events → `AgentStateReducer` → immutable
  `MonitoringSnapshot` → attention/health → debug consumer. Core has no UI,
  history or voice dependency (`ArchitectureTests` still passes).
- Identity/dedupe: normalized profile id + conversation id. Continuation and
  rollover files of one conversation collapse into one numbered task with one
  stable number (verified live: 8 conversations each merged 2–5 rollout files).
  Explicit internal rollouts (`thread_source` subagent/guardian_review/
  agent_created_thread/chatgpt_handoff, or `source.subagent`) are excluded; a
  `parent_thread_id` alone is not treated as internal.
- States are exactly WORKING/NEEDS_INPUT/NEEDS_APPROVAL/ERROR/DONE/IDLE; health is
  independently HEALTHY/DEGRADED/UNAVAILABLE with reason codes and freshness.
  Counts are `known`, `partial` or `unknown` — never an invented zero.
- Starts and completions require no token or assistant-text evidence. Duplicate
  replay, evidence for an older turn and evidence for a terminal turn are
  rejected. Cancellation/turn_aborted becomes IDLE with a cancellation marker
  (not DONE); a failed tool/command is not a task failure; explicit failure-coded
  records become ERROR. An explicit error object on a completion record is
  treated as failure, not success.
- Attention: actions/state from structured records only. Non-blocking input keeps
  WORKING and raises attention plus the needs-human flag independently; an async
  tool return does not resolve a request; approval has display precedence while
  both requests stay tracked. Hydration is baseline: the first read of a file
  emits no attention. ALL_TASKS_COMPLETED requires known previously running work
  reaching DONE — never disappearance, stale, error or cancellation.
- Privacy: only structural fields become models; message/reasoning/question/
  answer/command/output text is never retained. Command text is transiently
  inspected for a narrow category (test runner / search / read / generic) and
  dropped. Snapshot serialization and log files are asserted sentinel-free.
- Bounds enforced and tested: 64 tracked files, 256 watcher hints, 256 KiB
  appended per file per pass, 4 MiB global per pass, 1 MiB pending record, 64 KiB
  identity header, ≤8 MiB hydration scan per file with fair per-cycle sharing,
  8,192 hydration records, 128 directories / 2,048 entries per discovery slice
  with resumable position, 512 title entries, 64 turns / 64 requests / 512 record
  markers per task, 512 tracked conversations, 1 MiB rolling log plus one backup.
  Debounce 250 ms, known-file poll 1 s, reconcile 10 s, stale lease 15 min,
  failure backoff 2 s → 60 s.
- Recovery: truncation, same-size replacement, malformed records, locked files,
  watcher creation failure, root appearing later and process restart are all
  covered by typed reasons and tests; restart rebuilds state from bounded
  hydration rather than persisted snapshots.

### Observability checkpoint (read-only probes, installed CLI 0.155.1)

- Installed rollout schema persists `session_meta`, `turn_context`,
  `event_msg/{task_started,task_complete,turn_aborted,item_completed,token_count,
  user_message,agent_message,thread_settings_applied}`, `response_item/{message,
  reasoning,function_call[_output],custom_tool_call[_output],compaction}`,
  `token_usage_record` and `compacted`.
- Approval and `request_user_input` events are **not** persisted as structured
  records. Keyword hits in real rollouts were prose inside messages, the embedded
  system prompt and tool inputs/outputs — never a record type. The parser only
  accepts structural record types, and a test proves prose mentions produce no
  attention evidence.
- `codex agents` is an interactive TUI against the shared app-server daemon, not
  a machine-readable per-thread status source; no app-server was contacted, no
  Codex configuration was read or changed, no task was resumed or approved.
- Optional `winsqlite3` read of the existing `state_5.sqlite` `threads` table
  verified read-only (schema, `thread_source` values, 75 ms busy timeout, hard
  progress-handler deadline, root containment). It is used only to locate
  candidate rollouts; its timestamps are freshness hints and never execution
  state. `.codex\process_manager\chat_processes.json` was observed but is not
  used: its semantics are undocumented.

### Build / test / smoke evidence

```text
dotnet restore  -> exit 0 (4 projects restored)
dotnet build    -> Build succeeded, 0 Warning(s), 0 Error(s)
dotnet test     -> Passed! Failed: 0, Passed: 117, Skipped: 0, Total: 117
```

Sandbox note: child processes in this session could not write inside the
workspace and could not read the user-level `%APPDATA%\NuGet\NuGet.Config`, so
restore used an ignored offline config (`artifacts/nuget-offline.config`, no
sources, existing package cache) and MSBuild node reuse/build servers were
disabled to avoid orphaned nodes. No repository file depends on either measure.

Live smoke (exact required command, 60 s, real `CODEX_HOME`, exit code 0):

```powershell
dotnet run --project src/TheOverseer.App --no-build -- --monitor-debug --duration 60 --redact-labels --data-directory artifacts/smoke-data
```

```text
health   DEGRADED (discovery-backlog, hydration-budget-exhausted, stale-tasks, tracked-file-limit; coverage partial)
counts   1 working (partial) · 0 needs you (partial)
tasks    60 discovered conversations: 52 DONE (explicit), 3 WORKING (structured evidence, 2 stale),
         5 IDLE (3 cancelled turns with explicit abort evidence, 2 not hydrated within the budget)
records  session_meta=22 task_started=283 task_complete=321 turn_aborted=14
         item_completed=12371 tool_calls=7396 permission_requests=0 input_requests=0
         malformed=0 oversize=1
files    64 tracked, 76 hydrations, 109,600,023 bytes read (bounded hydration)
caps     rollout-lifecycle live-observed, structured-activity live-observed,
         session-titles live-observed, runtime-activity-source live-observed,
         surfaces: 60/60 desktop (no CLI or VS Code session was active in this run),
         permission-requests unsupported, input-requests unsupported,
         attention-live not-exercised
```

Additional verified paths: exit 2 with `working unknown · needs you unknown` for a
missing Codex home; exit 1 with usage for bad arguments; normal (argument-less)
startup still opens the OVERSEER scaffold window (verified window title, no
console output).

The smoke ran against real recorded activity but **no live attention event** was
available: attention contracts are covered by synthetic tests only, and the run
reports that truthfully. No task was resumed, approved or modified.

### Acceptance criteria A1–A8

- A1 pass: four projects restore/build/test; no new stack, runtime package,
  upstream UI or later-plan feature; `ArchitectureTests` keeps Core UI-independent.
- A2 pass: one row per user conversation with rollover/continuation merging and
  stable numbers; Desktop/CLI/VS Code surface mapping unit-tested; live claims
  limited to what the run actually read (all live rows were `desktop`).
- A3 pass: transition, parallel-request, duplicate/old-turn, terminal-turn,
  no-text completion, cancellation, hydration and unsupported-state cases tested.
- A4 pass: canonical action vocabulary only; privacy tests assert no raw text in
  snapshots or logs.
- A5 pass: healthy empty vs degraded partial vs unavailable are distinct; a
  missing root yields unknown counts, never a healthy zero.
- A6 pass: every cap above has a deterministic test; reconciliation and watcher
  loss recovery are tested; no I/O on a UI thread (the loop runs on the thread pool).
- A7 pass: the finite debug run prints health, capabilities, freshness, actual
  counts, per-task rows and the spec-shaped blocks, with no WPF window and clean
  finite exit.
- A8 pass: exact provenance, read-only sources, synthetic-only fixtures, real
  validation evidence and honest limitations are recorded here. Unsupported
  attention is not claimed as proven; nothing was merged or tagged.

## Known Limitations
- Approval/input attention is **unsupported** on this Codex version: those events
  are transient inside the owning runtime and are not rollout-persisted. The
  detector contract exists and is synthetically tested; it has never been
  live-verified.
- No live attention transition was observed in the smoke run, so
  `attention-live` remains *not exercised* rather than passed.
- Task-failure evidence is supported only for explicit failure-coded records; the
  installed version produced abort records (cancellation) but no failure-coded
  terminal record in this run.
- Counts are partial whenever discovery backlog, hydration budget, the 64-file
  tracking cap, stale tasks or unresolved identity apply; the smoke run exercised
  all of these.
- Hydration reads at most 8 MiB per file with a 4 MiB per-pass budget, so a cold
  60 s run hydrates a bounded subset of a large history; deeper history is left
  unread and reported as incomplete coverage instead of guessed.
- Additional Codex roots are supported through configuration, but only the
  default profile was exercised live.
- Optional state-store ordering is used for candidate location and freshness
  hints only; it never proves running, done or attention.

## Blocking Issues
- none

## Next Action
- Astra Review 1 reviews `impl/p1-monitoring` against `main` (do not merge/tag/push;
  review is pending and no plan status is marked as reviewed).
