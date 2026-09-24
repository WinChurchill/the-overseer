# OVERSEER Claude Code hook bridge

Opt-in, reversible integration that converts documented Claude Code hook events into
bounded, metadata-only records appended to an OVERSEER-owned local spool.

* Node.js >= 18, ESM, standard library only, zero dependencies, no network access at hook time.
* The hook helper never returns a permission decision, never writes to stdout, and always exits 0.
* Nothing is written to a Claude settings file unless `--apply` is passed.

## Qualification status (read this first)

**Not live-qualified here.** On the machine where this asset was built and tested
(2026-09-24, Windows, Node v24.15.0) Claude Code is not installed:

* no `claude` executable on `PATH`;
* no `~/.claude` directory (`C:\Users\tester\.claude` does not exist);
* no npm-installed Claude Code shim.

No real Claude Code process has ever invoked `bridge.mjs` in this environment, so every
statement below is derived from the upstream documentation plus contract tests, not from a
live session.

Verified here:

* the payload mapping is pinned to fields documented on the pages listed below, and driven by
  sanitized synthetic fixtures in `fixtures/`;
* the spool contract (bounds, atomic append, per-session ordering, stable identity/epoch,
  failure counters, fail-open behaviour) is covered by `node --test` with real child processes
  and real temporary directories;
* install/uninstall round-trip a settings file that already contains unrelated user hooks and
  preserve those entries.

Not verified here (remains open):

* that a real Claude Code build accepts the generated handler entries and launches them with
  these arguments;
* real payload variance across Claude Code versions (extra, renamed or removed fields);
* live interaction with the documented hook `timeout` / matcher evaluation;
* end-to-end latency of the hook inside a live session;
* that documented behaviour has not changed since the retrieval date below.

## Upstream documentation

Retrieval date: **2026-09-24** (UTC).

| Source | Used for |
| :--- | :--- |
| [Hooks reference](https://code.claude.com/docs/en/hooks) (markdown: [hooks.md](https://code.claude.com/docs/en/hooks.md)) | hook event names, matcher/handler configuration shape, stdin JSON payload fields, exit-code semantics |
| [Tools reference](https://code.claude.com/docs/en/tools-reference.md) | the tool names `EnterPlanMode`, `ExitPlanMode`, `AskUserQuestion` used for classification |

The bridge reads only fields documented on those pages. If upstream changes a payload shape,
this bridge degrades to `evidence: "unknown"` rather than guessing.

## Files

| Path | Purpose |
| :--- | :--- |
| `bridge.mjs` | the hook helper: stdin JSON -> normalized record -> spool append |
| `lib/mapping.mjs` | documented payload -> normalized kind mapping and the record contract |
| `lib/spool.mjs` | bounded spool writer: identity, source epoch, locking, sequence, health |
| `lib/settings.mjs` | settings file load / plan / diff / backup / atomic write |
| `install.mjs` | preview-by-default opt-in installer |
| `uninstall.mjs` | preview-by-default remover, scoped to OVERSEER-owned entries |
| `lib/testkit.mjs` | test-only helpers; never imported by the hook path |
| `test/` | `node --test` suite (real child processes, temp directories) |
| `fixtures/` | sanitized synthetic payloads, one per supported event |
| `README.md`, `NOTICE.md` | this document and attribution |

## Record schema

One JSON object per line with **exactly** these keys, in this order. No other key is ever added.

| Key | Type | Provenance |
| :--- | :--- | :--- |
| `schemaVersion` | number, `1` | constant |
| `harnessId` | string, `"claude"` | constant |
| `sourceInstanceId` | string | `<spool>/claude/instance.json`, created once per install |
| `sourceEpoch` | string | `<spool>/claude/<sourceInstanceId>/source-epoch.json`, created once per install |
| `sessionId` | string | documented `session_id`; `"unknown"` when absent |
| `sequence` | number | per-session ingestion order allocated under the spool lock, starts at 1 |
| `eventId` | string | `ccev-` + first 32 hex chars of SHA-256 over (harness, source instance, session, kind, event name, documented correlation ids) |
| `observedAtUtc` | ISO-8601 string | bridge receipt time |
| `eventAtUtc` | ISO-8601 string | equal to `observedAtUtc` (no timestamp is documented on any hook payload - see Known limits) |
| `kind` | enum | see the mapping table |
| `turnId` | string or null | documented `prompt_id`, else `turn_id` (MessageDisplay) |
| `callId` | string or null | documented `tool_use_id` |
| `requestId` | string or null | documented `agent_id`, else `task_id`, else `elicitation_id` |
| `projectRoot` | string or null | documented `cwd`, control characters removed, bounded to 512 characters |
| `category` | enum `thinking\|planning\|working\|waiting\|error\|complete\|idle\|unknown` | see the mapping table |
| `evidence` | enum `explicit\|inferred\|unknown` | see the mapping table; forced to `unknown` when `session_id` is missing |
| `outcome` | enum `completed\|aborted\|error\|ok\|failed` or null | see the mapping table |
| `hydration` | boolean | always `false`: a live one-shot publication, never a replay/backfill |
| `producerVersion` | string | `overseer-claude-bridge/1.0.0` |

Deliberately **never** stored, even though the payloads contain them: prompt and message text,
tool names, tool arguments, tool results and errors, transcript and scratchpad paths,
notification text and titles, model names, session titles, plan text, streamed deltas,
environment values and credentials. Fields such as `tool_name`, `notification_type`, `source`,
`reason` and `trigger` are read only to choose a documented enum value and are never persisted;
where they contribute to `eventId` they contribute only as SHA-256 input.

## Event mapping

| Claude Code event | Condition | kind | category | outcome | evidence |
| :--- | :--- | :--- | :--- | :--- | :--- |
| `SessionStart` | always | `session/start` | idle | null | explicit |
| `SessionEnd` | always | `session/end` | idle | `completed` | explicit |
| `UserPromptSubmit` | always | `turn/start` | thinking | null | explicit |
| `Stop` | always | `turn/end` | complete | `completed` | explicit |
| `StopFailure` | always | `turn/end` | error | `error` | explicit |
| `PreToolUse` | any other tool | `tool/start` | working | null | explicit |
| `PreToolUse` | `tool_name` is AskUserQuestion | `input/asked` | waiting | null | explicit |
| `PreToolUse` | `tool_name` is EnterPlanMode / ExitPlanMode | `plan/mode` | planning | null | explicit |
| `PostToolUse` | any other tool | `tool/end` | working | `ok` | explicit |
| `PostToolUse` | `tool_name` is AskUserQuestion | `input/answered` | working | `ok` | explicit |
| `PostToolUse` | `tool_name` is EnterPlanMode / ExitPlanMode | `plan/mode` | planning | `ok` | explicit |
| `PostToolUseFailure` | always | `tool/end` | error | `failed` | explicit |
| `PermissionRequest` | always | `approval/asked` | waiting | null | inferred |
| `PermissionDenied` | always | `approval/decided` | working | `aborted` | explicit |
| `Notification` | `notification_type` = `permission_prompt` | `approval/asked` | waiting | null | inferred |
| `Notification` | `notification_type` = `agent_needs_input` | `input/asked` | waiting | null | inferred |
| `Notification` | `notification_type` = `elicitation_dialog` | `input/asked` | waiting | null | inferred |
| `Notification` | `notification_type` = `elicitation_url_dialog` | `input/asked` | waiting | null | inferred |
| `Notification` | `notification_type` = `idle_prompt` | `heartbeat` | idle | null | inferred |
| `Notification` | `notification_type` = `agent_completed` | `heartbeat` | complete | `completed` | inferred |
| `Notification` | `notification_type` = `auth_success` | `heartbeat` | idle | null | inferred |
| `Notification` | `notification_type` = `elicitation_complete` | `heartbeat` | idle | `completed` | inferred |
| `Notification` | `notification_type` = `elicitation_response` | `heartbeat` | working | null | inferred |
| `Notification` | `notification_type` = `quota_auto_resume_fired` | `heartbeat` | working | null | inferred |
| `Notification` | `notification_type` = `quota_auto_resume_stale` | `heartbeat` | idle | null | inferred |
| `Notification` | `notification_type` = `quota_auto_resume_disabled` | `heartbeat` | idle | null | inferred |
| `Notification` | `notification_type` = anything else | `heartbeat` | unknown | null | unknown |
| `SubagentStart` | always | `turn/start` | working | null | explicit |
| `SubagentStop` | always | `turn/end` | complete | `completed` | explicit |
| `PreCompact` | always | `snapshot` | working | null | explicit |
| `PostCompact` | always | `snapshot` | complete | `completed` | explicit |
| `TaskCreated` | always | `heartbeat` | working | null | explicit |
| `TaskCompleted` | always | `heartbeat` | complete | `completed` | explicit |
| `TeammateIdle` | always | `heartbeat` | idle | null | explicit |
| `Elicitation` | always | `input/asked` | waiting | null | explicit |
| `ElicitationResult` | always | `input/answered` | working | `completed` | explicit |
| `MessageDisplay` | always | `heartbeat` | thinking | null | explicit |
| `PostToolBatch` | always | `heartbeat` | working | null | explicit |

Installed by default (15 events): SessionStart, SessionEnd, UserPromptSubmit, Stop,
StopFailure, PreToolUse, PostToolUse, PostToolUseFailure, PermissionRequest,
PermissionDenied, Notification, SubagentStart, SubagentStop, PreCompact, PostCompact.

Mapped but **not** installed by default (wire with `--events=` when you want them):
TaskCreated, TaskCompleted, TeammateIdle, Elicitation, ElicitationResult, MessageDisplay,
PostToolBatch. MessageDisplay fires per streamed batch and the task/elicitation events are
narrower than the default monitoring need.

Documented events with no mapping (Setup, InstructionsLoaded, ConfigChange, CwdChanged,
DirectoryAdded, FileChanged, WorktreeCreate, WorktreeRemove, PreModelSwitch,
PostModelSwitch, ...): if you wire one anyway, the bridge records `kind: "heartbeat"`,
`category: "unknown"`, `evidence: "unknown"` and increments the `unknownEvents` counter. It
never guesses a kind it cannot document.

**Correlation honesty.** `callId` comes only from a documented `tool_use_id`. PermissionRequest
is documented without a correlation id, so its `callId`/`requestId` stay `null` and its
`evidence` is `inferred`. No id is ever synthesised.

## Semantics that must not be misread

* **Stop is the end of a response, not the end of the project.** `Stop` documents "Claude
  finishes responding"; the bridge records `turn/end` with outcome `completed`. Use it as
  "this turn ended", never as "the user's goal is done".
* **A recoverable tool failure is not task failure.** `PostToolUseFailure` becomes
  `tool/end` + outcome `failed` on that one call. It says nothing about the session or the task.
* **`sequence` is ingestion order, not causal order.** It is allocated inside the spool lock
  and proves only the order in which the bridge accepted records. Concurrent hooks, retries and
  OS scheduling mean the file order must not be read as the upstream order of the events
  themselves.
* **`eventAtUtc` is receipt time.** No Claude Code hook payload documents a timestamp field, so
  `eventAtUtc` equals `observedAtUtc` rather than pretending to be an upstream event time.
* **`evidence` is about the mapping, not the event.** `explicit` = the kind and correlation come
  straight from documented fields of that event; `inferred` = the kind depends on a documented
  sub-type or an absent correlation id; `unknown` = the event name is not in the mapping table or
  `session_id` was missing.
* **`hydration` is always `false`.** Hook records are live publications; the bridge never
  back-fills or replays history.
* **The bridge is not a permission hook.** It always exits 0 with empty stdout, so it can never
  allow, deny or block a tool call, prompt, compaction or model switch.

## Spool

Default location:

* Windows: `%LOCALAPPDATA%\OVERSEER\spool`
* other platforms: `$XDG_STATE_HOME/OVERSEER/spool`, else `~/.local/state/OVERSEER/spool`
* override with `OVERSEER_SPOOL_ROOT` (the tests use this so they never touch the real spool)

Layout:

```text
<spool>/claude/instance.json                        stable sourceInstanceId for this install
<spool>/claude/<sourceInstanceId>/source-epoch.json stable sourceEpoch for this install
<spool>/claude/<sourceInstanceId>/events.jsonl      append-only records (one JSON object per line)
<spool>/claude/<sourceInstanceId>/events.lock       writer lock (exclusive create, stale takeover)
<spool>/claude/<sourceInstanceId>/health.json       bounded counters, no payload data
<spool>/claude/<sourceInstanceId>/sessions/<key>.seq per-session ingestion counter
```

Caps and budgets:

| Bound | Value | Behaviour when exceeded |
| :--- | :--- | :--- |
| stdin size | 1 MiB | stdin is destroyed, nothing is recorded, `droppedOversizeInput` |
| stdin deadline | 2 s from process start | stdin is destroyed, nothing is recorded, `droppedStdinDeadline`; empty stdout, exit 0 |
| acquisition + write budget | 250 ms | the record is dropped, `droppedLockTimeout`, `droppedOversizeRecord` or `droppedWriteFailed` |
| failed append | - | `droppedWriteFailed`; the per-session sequence counter is only advanced after a successful append, so a failed write leaves no gap |
| record size | 64 KiB per line | the record is dropped and counted, never truncated into invalid JSON |
| `projectRoot` | 512 characters | truncated at the bound |
| health counters | fixed key set | `health.json` cannot grow with event volume |
| hard exit watchdog | 2.25 s after the 2 s + 250 ms budgets | process exits 0 regardless |

Identity and epoch stability: `instance.json` and `source-epoch.json` are created once with an
exclusive-create write and then only read, so every hook invocation of an install publishes the
same `sourceInstanceId` and `sourceEpoch`. A file that exists but cannot be parsed is moved aside
as `<file>.corrupt-<utcstamp>-<hex>` and recreated once (counted as `recoveredCorruptFile`) -
that is the only path that can change an epoch, and it is visible in the spool. If you need a
fresh epoch on purpose, stop publishing and delete `source-epoch.json`.

Failure counters in `health.json`: `recordsWritten`, `droppedOversizeInput`, `droppedEmptyInput`,
`droppedMalformedInput`, `droppedStdinDeadline`, `droppedStdinError`, `droppedLockTimeout`,
`droppedOversizeRecord`, `droppedWriteFailed`, `droppedCorruptSourceEpoch`,
`droppedUnsupportedPayload`, `droppedInternalError`, `recoveredCorruptFile`, `unknownEvents`.
Every name the bridge can emit is asserted to be in this allowlist by
`test/bridge.records.test.mjs`, because an unlisted name is silently discarded and
would turn a lost record into an invisible one.

Environment variables read by `bridge.mjs`:

| Variable | Effect |
| :--- | :--- |
| `OVERSEER_SPOOL_ROOT` | spool root override |
| `OVERSEER_CLAUDE_SOURCE_INSTANCE_ID` | pin the source instance id (bypasses `instance.json`) |
| `OVERSEER_CLAUDE_BRIDGE_DEBUG=1` | sanitized diagnostics on stderr (event kind, decision codes); never payload content |

## Privacy

Metadata only. The bridge reads the hook JSON, extracts the allowlisted identity/classification
fields listed in the schema table, and appends one bounded record. It does not read
`transcript_path` or `agent_transcript_path` files, does not read the environment beyond the
three variables above, makes no network calls, has no dependencies, and writes nothing to
stdout. Diagnostics are off by default; when enabled they contain only event names, kinds,
sequence numbers and reason codes.

The spool can still reveal operational metadata: session ids, per-session event counts and
timing, project root paths, and how often tools fail or approvals are requested. Treat
`<spool>/claude/` as private local data and delete it (and the settings entries) when you no
longer want the bridge.

## Install

Preview (writes nothing):

```text
node integrations/claude-code/install.mjs "C:\Users\you\.claude\settings.json"
```

Apply:

```text
node integrations/claude-code/install.mjs "C:\Users\you\.claude\settings.json" --apply
```

Options:

| Option | Meaning |
| :--- | :--- |
| `[settingsPath]` | settings file to edit; default `~/.claude/settings.json`; a missing file is created on `--apply` |
| `--apply` | actually write; without it the run is a preview |
| `--events=A,B,...` | wire only the listed events (default: the 15 documented events above) |
| `--bridge=<path>` | override the recorded hook script path (default: this checkout's `bridge.mjs`) |
| `--json` | machine-readable plan on stdout (tests use this) |
| `--dry-run` | explicit preview (same as omitting `--apply`) |

Guarantees:

* the existing file is parsed first and the run is **refused** (exit 2, nothing written) if it is
  not valid JSON, not a JSON object, or if `hooks` / `hooks.<Event>` has a shape this tool does
  not own;
* a backup is written to `<file>.overseer-backup-<utcstamp>` before the first write of a run;
* only OVERSEER-owned handler entries are added; every other key and value is preserved
  semantically (the file is re-serialised, so formatting may normalise while content is kept);
* the run is idempotent: a second `--apply` reports "already installed" and writes nothing and no
  second backup;
* ownership is identified by the marker string `overseer-claude-bridge` inside the handler
  `args`, so nothing else in your settings can be mistaken for an OVERSEER entry.

Each installed event gets one matcher-less group (which the docs define as "every occurrence"):

```json
{
  "hooks": {
    "PreToolUse": [
      {
        "hooks": [
          {
            "type": "command",
            "command": "node",
            "args": ["D:\\OVERSEER\\the-overseer\\integrations\\claude-code\\bridge.mjs", "overseer-claude-bridge"],
            "timeout": 10
          }
        ]
      }
    ]
  }
}
```

`timeout` is set to 10 s so a stuck hook is cancelled long before Claude Code's 600 s default;
the bridge itself gives up after roughly 2.25 s.

## Uninstall and rollback

```text
node integrations/claude-code/uninstall.mjs "C:\Users\you\.claude\settings.json"          # preview
node integrations/claude-code/uninstall.mjs "C:\Users\you\.claude\settings.json" --apply  # remove
```

* removes only handlers carrying the `overseer-claude-bridge` marker, drops the groups and event
  keys left empty, and leaves every other entry semantically unchanged;
* backs up to `<file>.overseer-backup-<utcstamp>` before writing;
* prints every removed entry and every entry it **could not** remove; a partially-completed
  uninstall exits 3, a clean one exits 0, a refusal exits 2;
* rollback: restore any `<file>.overseer-backup-<utcstamp>` over the settings file, or run the
  uninstaller. Removing the integration directory without running the uninstaller leaves
  dangling handler entries (and a "hook error" notice in Claude Code) - uninstall first.
* moving this directory invalidates the recorded absolute `bridge.mjs` path; re-run
  `install.mjs ... --apply` afterwards to update it.

## Tests

Run from this directory:

```text
cd integrations/claude-code
node --test
```

Observed on 2026-09-24 (Windows, Node v24.15.0): **19 tests, 19 pass, 0 fail, 0 skipped**,
~2.8 s wall clock.

Coverage map:

| Test file | Cases |
| :--- | :--- |
| `test/bridge.stdin.test.mjs` | (a) oversized stdin fails open with exit 0 and empty stdout; (b) stalled stdin hits the 2 s deadline; (c) malformed, non-object and empty stdin fail open; a documented payload produces exactly one record with empty stdout even with debug enabled; health counters stay bounded and payload-free |
| `test/bridge.records.test.mjs` | fixture -> mapping contract (kind/category/outcome/evidence); exact key set and order; 64 KiB bound; correlation ids from documented fields only; privacy (no fixture content, transcript or scratchpad text in any record); (e) duplicate publication keeps `eventId` while distinct calls differ; (h) `sourceEpoch` stable across invocations |
| `test/bridge.concurrency.test.mjs` | (d) 10 concurrent writers produce whole, non-interleaved lines; (i) per-session `sequence` is unique and monotonic under concurrency; per-session counters start at 1; a held writer lock is abandoned inside the 250 ms budget and counted as `droppedLockTimeout` |
| `test/installer.test.mjs` | (f) installer refuses malformed settings and never writes without `--apply`; (g) install preserves unrelated hooks and unrelated top-level keys, is byte-stable when re-applied, and uninstall removes only owned entries; uninstall reports entries it cannot remove (exit 3); install refuses a `hooks` value it does not own |

The tests use `fs.mkdtemp` directories and real child processes; they never read or write
`~/.claude` or the production spool.

## Known limits

* Not live-qualified (see the top of this document).
* `eventAtUtc` is not an upstream event time: no hook payload documents one.
* Tool names, notification sub-types and permission-decision details are not persisted; the
  record schema is fixed at the 19 keys above, by contract.
* `MessageDisplay` is mapped but not installed by default because it fires per streamed batch.
* `SubagentStop` is `turn/end` for the subagent, not for the main conversation.
* Ingestion `sequence` is per session and starts at 1 for each session id.
* A hook that cannot acquire the spool lock within its budget drops its record (counted). Under
  sustained contention, prefer `--events=` to narrow the installed set rather than assuming
  every event is captured.
* Only settings files are managed. Managed policy settings, plugin hooks and project
  `.claude/settings.json` are out of scope.
