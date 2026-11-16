# OVERSEER Claude Code hook bridge

Opt-in, reversible integration that converts documented Claude Code hook events into
bounded, metadata-only records appended to an OVERSEER-owned local spool.

* Node.js >= 18, ESM, standard library only, zero dependencies, no network access at hook time.
* The hook helper emits no permission decision. It exits 0 with empty stdout on handled failures.
* Nothing is written to a Claude settings file unless `--apply` is passed.
* Bridge 1.0.2 atomically publishes new install identity/epoch documents using
  same-directory exclusive hard links. Use a Windows-local spool on a filesystem
  supporting hard links (default NTFS); unsupported/custom filesystems are not
  qualified. Existing valid identities are preserved.
* Slow acquisition reserves health accounting from the remaining write budget.
  The 250 ms budget and fail-open behavior are preserved; filesystem/OS stalls
  can still prevent best-effort diagnostic writes and require environment testing.

## Qualification status (read this first)

**Experimental, with a limited live Windows check.** On November 11, 2026 the
exact unsigned OVERSEER `1.0.0-alpha.5` HUD observed a real Claude Desktop Code
question and a separate `ExitPlanMode` approval in a disposable chat using Haiku.
Each appeared under **Needs you** and cleared after the answer or owner approval.
The question remained confirmed beyond the ten-second work-action lease.

The kept candidate's installer verified bridge `1.0.7`, rebound 15 owned hook paths,
preserved unrelated settings/hooks, and produced a zero-change follow-up preview.
The original settings backup remains local. Claude's installed desktop package was
`5.1231.2.0`; its embedded Code engine version was not independently established.
The selected model is not an engine version. These results do not qualify every
desktop/editor/CLI version or every kind of permission prompt.

Verified here:

* the payload mapping is pinned to fields documented on the pages listed below, and driven by
  sanitized synthetic fixtures in `fixtures/`;
* the spool contract (bounds, atomic append, per-session ordering, stable identity/epoch,
  failure counters, fail-open behaviour) is covered by `node --test` with real child processes
  and real temporary directories;
* install/uninstall round-trip a settings file that already contains unrelated user hooks and
  preserve those entries.

Not verified here (remains open):

* shell, file and network permission prompts beyond the observed plan approval;
* real payload variance across Claude Code versions (extra, renamed or removed fields);
* live interaction with the documented hook `timeout` / matcher evaluation;
* end-to-end latency of the hook inside a live session. The cooperative budget
  covers the bridge's controlled work. Process startup, OS scheduling and a single
  blocking filesystem operation can add wall-clock time;
* the embedded engine version and any version-specific differences from the documented contract.

## Upstream documentation

Original mapping retrieval: **2026-09-24** (UTC). Desktop/editor and exec-form
configuration rechecked against official documentation on **2026-09-29**.

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
| `lib/logical-clock.mjs` | test-only `--import` preload: bridge deadlines count clock reads, not wall time, so accounting tests do not depend on CPU scheduling |
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
| `requestId` | string or null | `elicitation_id`, else `tool_use_id` for input/approval events; agent/task metadata only for non-request events |
| `projectRoot` | string or null | documented `cwd`, control characters removed, bounded to 512 characters |
| `category` | enum `thinking\|planning\|working\|waiting\|error\|complete\|idle\|unknown` | see the mapping table |
| `evidence` | enum `explicit\|inferred\|unknown` | see the mapping table; forced to `unknown` when `session_id` is missing |
| `outcome` | enum `completed\|aborted\|error\|ok\|failed` or null | see the mapping table |
| `hydration` | boolean | always `false`: a live one-shot publication, never a replay/backfill |
| `producerVersion` | string | `overseer-claude-bridge/1.0.8` |
| `hostSessionId` | string or null | the Claude desktop app's `local_…` id for the session, so a click in OVERSEER can open it; `null` outside the desktop app. Read only from `CLAUDE_CODE_HOST_SESSION_ID` when `CLAUDE_CODE_ENTRYPOINT` is `claude-desktop`; no other environment value is read |

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
| `PermissionRequest` | `tool_name` = `AskUserQuestion` | `heartbeat` | waiting | null | inferred |
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
| `SubagentStart` | always | `heartbeat` | working | null | explicit |
| `SubagentStop` | always | `heartbeat` | working | null | explicit |
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
The consumer also accepts `callId` as the request identity. Requests with no
correlation identity remain pending through unrelated tools; a turn/session end
clears them. An uncorrelated decision cannot safely select which request to clear.

Claude Code never reports that a permission was granted. The consumer therefore
ties an identity-less approval to what it was observed to block, and only when
that is unambiguous: exactly one tool call was open (and no question pending) when
the prompt arrived, with complete call tracking since the latest observed turn
boundary. Lost records, missing call identities and capacity overflow disable
this inference. That call's own `tool/end`, or an explicit `approval/decided`
for it, clears the prompt. With several calls open, or a second prompt for a
different call, nothing is inferred and the request stays until the turn ends.

Observed on Claude Code 2.x for Windows (2026-11-10, this development machine):
`PermissionRequest` fires within about 0.2 s of `PreToolUse`, and the
`permission_prompt` notification follows about 6 s later for the same prompt. A
question fires both as well. Since 1.0.6 a `PermissionRequest` whose tool is
`AskUserQuestion` is a liveness-only `heartbeat` because the question is already
recorded as `input/asked`. Standalone `permission_prompt` notifications remain
approval signals: sandbox network prompts can emit them without `PermissionRequest`.
When one known question is the only possible blocker, its answer settles the
approval too. A delayed notification without enough context stays pending until
an observed turn/session end. The consumer cannot prove it duplicates an already
answered request. See the [PermissionRequest hook reference](https://code.claude.com/docs/en/hooks.md#permissionrequest).
Two questions from the same agent use their own call IDs, not the agent ID.

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

The spool root lives inside OVERSEER's **data directory**, not inside the extracted
application folder, so installing, moving or removing an OVERSEER version never moves
or orphans an installed bridge, and `--data-directory PATH` keeps an isolated run
separate from the real one. OVERSEER discovers spool directories when it composes its
monitor at **startup**: restart OVERSEER after installing or removing this bridge. Hook
changes need no host restart beyond the settings file being re-read by Claude Code.

Default location:

* Windows: `%LOCALAPPDATA%\OVERSEER\spool`
* other platforms: `$XDG_STATE_HOME/OVERSEER/spool`, else `~/.local/state/OVERSEER/spool`
* override with `OVERSEER_SPOOL_ROOT` (the tests use this so they never touch the real spool)

Layout:

```text
<spool>/claude/instance.json                        stable sourceInstanceId for this install
<spool>/claude/<sourceInstanceId>/source-epoch.json stable sourceEpoch for this install
<spool>/claude/<sourceInstanceId>/events.jsonl      append-only records (one JSON object per line)
<spool>/claude/<sourceInstanceId>/events-previous.jsonl previous event segment
<spool>/claude/<sourceInstanceId>/events.lock       writer lock (exclusive create, stale takeover)
<spool>/claude/<sourceInstanceId>/health.json       bounded counters, no payload data
<spool>/claude/<sourceInstanceId>/health-pending/   fixed slots for deferred accounting receipts
<spool>/claude/<sourceInstanceId>/health-incomplete empty marker if accounting capacity/write fails
<spool>/claude/<sourceInstanceId>/health-incomplete.folding that marker while it is being counted
<spool>/claude/<sourceInstanceId>/health-scan-incomplete temporary scan/read coverage limit, not a drop
<spool>/claude/<sourceInstanceId>/sessions/<key>.seq per-session ingestion counter
<spool>/claude/<sourceInstanceId>/sessions/<key>.seq.recovery independent recovery checkpoint
```

Caps and budgets:

| Bound | Value | Behaviour when exceeded |
| :--- | :--- | :--- |
| stdin size | 1 MiB | stdin is destroyed, nothing is recorded, `droppedOversizeInput` |
| stdin deadline | 2 s from process start | stdin is destroyed, nothing is recorded, `droppedStdinDeadline`; empty stdout, exit 0 |
| acquisition + write budget | 250 ms | the record is dropped, `droppedLockTimeout`, `droppedOversizeRecord` or `droppedWriteFailed` |
| failed append | - | `droppedWriteFailed`; the bridge rolls back a known failed append when storage allows it |
| record size | 64 KiB per line | the record is dropped and counted, never truncated into invalid JSON |
| event log | 4 MiB per segment, two segments | rotates under the existing writer lock; the next write to an oversized legacy log retains its recent complete records |
| startup replay | up to 256 KiB across the retained segments | attaches near the newest complete records and reports skipped history |
| `projectRoot` | 512 UTF-16 code units | truncates without splitting a surrogate pair |
| health counters | fixed key set | `health.json` cannot grow with event volume |
| deferred accounting | 64 receipt slots, 512 bytes each, plus 64 fixed intent names | a missing outcome is visible as incomplete accounting when its intent survives |
| fallback watchdog | 2.5 s timer after the helper starts | exits 0 when the event loop can run the timer |

These are cooperative timing limits. Synchronous filesystem calls and OS
scheduling can extend the observed wall time.

Sequence recovery uses an independent checkpoint for each session. Loss of the
primary counter resumes from the checkpoint without changing the source epoch or
other sessions. A bounded suffix of the event log can recover a recent legacy
counter. If an interruption leaves a reserved number, a subsequent record can
expose a gap. Recovery requires a surviving checkpoint or retained ordering evidence.

Identity and epoch stability: `instance.json` and `source-epoch.json` are created once with an
exclusive publication of a complete document and then only read, so every hook invocation of an install publishes the
same `sourceInstanceId` and `sourceEpoch`. Invalid identity JSON is moved aside
under a short repair lock and recreated. At most one `<file>.corrupt` backup is
retained. This recovery is counted as `recoveredCorruptFile`. If you need a
fresh epoch on purpose, stop publishing and delete `source-epoch.json`.

Failure counters in `health.json`: `recordsWritten`, `droppedOversizeInput`, `droppedEmptyInput`,
`droppedMalformedInput`, `droppedStdinDeadline`, `droppedStdinError`, `droppedLockTimeout`,
`droppedOversizeRecord`, `droppedWriteFailed`, `droppedCorruptSourceEpoch`,
`droppedUnsupportedPayload`, `droppedInternalError`, `recoveredCorruptFile`, `unknownEvents`.
Every name the bridge can emit is asserted to be in this allowlist by
`test/bridge.records.test.mjs`, because an unlisted name is silently discarded and
would turn a lost record into an invisible one.

Bridge 1.0.3 publishes a complete, metadata-only accounting receipt before attempting
the shared `health.lock`. If that lock is busy, the updated app reads the receipt
alongside `health.json`, so a lock-timeout drop remains visible. After publishing
a receipt, a writer folds receipts only if it can acquire the health lock immediately.
Receipt IDs and totals commit atomically before
cleanup, preventing duplicate counts if cleanup fails or a process exits mid-fold.
The original 250 ms acquisition/write budget and 60 ms accounting slice are unchanged.
Directory setup is acquired within the parent budget before the accounting slice,
so a cold directory does not consume that smaller allowance before publication.
Intent registration also uses the remaining parent acquisition allowance while
reserving the full 60 ms accounting slice; it does not borrow the 10 ms marker reserve.
No background worker or extra retry budget is added. If all receipt slots are occupied
or accounting storage fails, a bounded marker reports incomplete coverage when it can
be written within the remaining budget. Unwritable storage or an exhausted process
budget cannot guarantee a durable diagnostic; hooks still fail open.

An uncontended hook commits its counters straight into `health.json` under the
health lock; its intent is retired only after that atomic write, so an interrupted
hook stays visible. A hook that finds the lock busy publishes a lock-free receipt
instead, which the next fold commits.

Each new intent carries a bounded owner/nonce token. Its receipt reuses that nonce;
the summary keeps at most 64 intent proofs alongside the 64 receipt IDs. Both
readers recognize completed accounting even when the deadline prevents removing
the intent. A reused slot needs its new nonce, and legacy PID-only intents remain
conservative. Partial folds retain receipts until their proof can be preserved.
If accounting admission itself cannot be recorded, the hook declines metadata
publication and keeps the existing loss marker, while Claude still proceeds.

A scan that reaches its time/entry bound, or a temporary receipt read failure,
sets `health-scan-incomplete`. Both readers keep coverage limited while it exists.
A complete successful fold clears it without adding a drop: a delayed scan does
not prove any event was lost. A failed cleanup keeps that limitation visible.

The bridge reserves an intent before attempting the event write. The app treats
an unfinished intent as incomplete coverage. A later writer can reclaim an
abandoned intent after confirming its owner is gone and persisting the incomplete
marker. The marker is one episode, not a permanent state: once no other hook's
intent is unresolved and no unreadable accounting file remains, the next fold
renames it to `health-incomplete.folding`, adds one `droppedUnaccounted` to the
summary in the same atomic write, and removes it. The app reports that as a
producer drop and then recovers. A corrupt receipt is deleted and counted the same
way. If the process stops between the summary write and the removal, the episode
is counted again; a gap is over-reported, never hidden. Spool directories must be ordinary directories. Event append rejects
linked files, and stale-looking locks are retained while their owners are alive.

Build the app and bridge from the same revision for this fix. An older app reads
only folded `health.json` totals and cannot include pending receipts. The updated
reader still accepts older bridge summaries. Accounting files contain only random
receipt IDs and allowlisted counter names, never event payloads.

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

For the desktop **Code tab** or a local editor extension, select the Code settings
file used by that session (normally `~/.claude/settings.json`), not the desktop
Chat MCP file `claude_desktop_config.json`. See
[Desktop shared configuration](https://code.claude.com/docs/en/desktop#shared-configuration)
and [VS Code settings](https://code.claude.com/docs/en/vs-code#configure-settings).
Start a new session after changing hook settings.

The embedded engine must be able to resolve `node` and read the permanent bridge
path. On Windows Desktop, user/system environment variables are inherited;
PowerShell profile changes alone do not set its PATH. Verify from the session's
execution environment rather than from an unrelated terminal.

WSL, SSH, containers and cloud sessions run elsewhere. Their settings, Node binary
and paths belong to that environment; writing Windows settings does not configure
them. Routing a remote/Linux spool into the Windows OVERSEER monitor is not yet
qualified. These instructions target a Windows local session.

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
| `--bridge=<path>` | override the recorded hook script path (default: this directory's `bridge.mjs`) |
| `--json` | machine-readable plan on stdout (tests use this) |
| `--dry-run` | explicit preview (same as omitting `--apply`) |
| `--no-verify` | skip execution of the pre-write probe (advanced); the path must still be a regular file, and execution remains unproven |

### Keep the bridge somewhere permanent

Claude Code stores an **absolute path** to `bridge.mjs` in your settings file. If you install from a
release archive you later delete, that path dangles and Claude Code reports a hook error on every
event. So `--apply` **executes the hook command it is about to record** against a throwaway
spool, and refuses to write anything (exit 2, no backup, no change) unless that run succeeds:

- a missing or unreadable path is refused;
- a path that is a directory is refused;
- a command that exits non-zero is refused;
- a command that exits 0 but publishes no spool record is refused, because it would silently
  observe nothing.

The probe runs before every settings-changing apply, including added events at an already
recorded path. A clean no-record result gets one retry on the same isolated spool;
the report records one or two attempts. Nonzero exits and timeouts are not retried.
Earlier records cannot certify a later invocation. The hook's write budget is unchanged.
An unchanged install remains an idempotent no-op. Copy this integration to a
directory you intend to keep, then install from there:

```text
node install.mjs "C:\Users\you\.claude\settings.json" --bridge="C:\Tools\overseer-claude\bridge.mjs" --apply
```

Paths containing spaces and non-ASCII characters are supported: the recorded handler passes
`command` and `args` as a direct argument vector, so nothing is re-parsed by a shell.

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
  `args`, so nothing else in your settings can be mistaken for an OVERSEER entry;
* rebinding rewrites every owned handler to the new path and removes duplicates, rather than adding
  a second handler per event.

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
            "args": ["C:\\Tools\\overseer-claude\\bridge.mjs", "overseer-claude-bridge"],
            "timeout": 10
          }
        ]
      }
    ]
  }
}
```

The installed hook sets a 10-second host timeout. The bridge schedules its own
2.5-second fallback timer, which can run when the event loop regains control.

## Uninstall and rollback

To remove hooks for one installation while preserving another installed or portable
copy, pass its exact `bridge.mjs` path with `--bridge="<kept folder>/bridge.mjs"`.
The Windows app uninstaller uses this scope automatically. Without `--bridge`, the
commands below remove all OVERSEER-marked hooks. Preview first; unrelated hooks and
settings are preserved in both modes.

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
* moving this directory invalidates the recorded absolute `bridge.mjs` path. Rebind instead of
  reinstalling: `node install.mjs "<settings.json>" --bridge="<new path>\bridge.mjs" --apply`
  rewrites every owned handler and verifies the new path first. If the old location is already
  gone, `--apply` is still the right command - it never needs the previous path to exist.

## Tests

Run from this directory:

```text
cd integrations/claude-code
node --test --test-concurrency=1
```

Observed on 2026-09-24 (Windows, Node v24.15.0): **19 tests, 19 pass, 0 fail, 0 skipped**,
~2.8 s wall clock.

Coverage map:

| Test file | Cases |
| :--- | :--- |
| `test/bridge.stdin.test.mjs` | (a) oversized stdin fails open with exit 0 and empty stdout; (b) stalled stdin hits the 2 s deadline; (c) malformed, non-object and empty stdin fail open; a documented payload produces exactly one record with empty stdout even with debug enabled; health counters stay bounded and payload-free |
| `test/bridge.records.test.mjs` | fixture -> mapping contract (kind/category/outcome/evidence); exact key set and order; 64 KiB bound; correlation ids from documented fields only; privacy (no fixture content, transcript or scratchpad text in any record); (e) duplicate publication keeps `eventId` while distinct calls differ; (h) `sourceEpoch` stable across invocations |
| `test/bridge.concurrency.test.mjs` | (d) 10 concurrent writers produce whole, non-interleaved lines; (i) per-session `sequence` is unique and monotonic under concurrency; per-session counters start at 1; a held writer lock is abandoned inside the 250 ms budget and counted as `droppedLockTimeout` |
| `test/bridge.budget.test.mjs` | Parent-budget clamping, simultaneous event/health lock contention, nine concurrent drops, receipt cleanup/reuse deduplication, bounded accounting capacity and slow identity acquisition |
| `test/installer.test.mjs` | (f) installer refuses malformed settings and never writes without `--apply`; (g) install preserves unrelated hooks and unrelated top-level keys, is byte-stable when re-applied, and uninstall removes only owned entries; uninstall reports entries it cannot remove (exit 3); install refuses a `hooks` value it does not own |

The tests use `fs.mkdtemp` directories and real child processes; they never read or write
`~/.claude` or the production spool.

## Known limits

* Not live-qualified (see the top of this document).
* `eventAtUtc` is not an upstream event time: no hook payload documents one.
* Tool names, notification sub-types and permission-decision details are not persisted; the
  record schema is fixed at the 20 keys above, by contract.
* Sessions that were already running when OVERSEER started (or that lost records) show as
  **state unconfirmed** until their next prompt. While they visibly work they still appear
  in **Now** with that mark, but they do not count toward the Working and Needs you totals
  or trigger alerts until confirmed.
* Between tool calls a working turn is shown from the open turn alone (inferred evidence).
  If a turn's end is lost, the task can show as working until it goes stale (15 minutes).
* Click-to-open works only for chats running in the **Claude desktop app**. Chats run in a
  terminal, VS Code or another editor cannot be opened from OVERSEER; clicking copies the id.
* Click-to-open relies on two things the Claude desktop app does not document: the
  `CLAUDE_CODE_HOST_SESSION_ID` variable it passes to hooks and its
  `claude://claude.ai/epitaxy/<id>` session link. A Claude update can change either; the
  click then falls back to copying the id.
* Chat names come from the Claude desktop app's own per-session file
  (`%LOCALAPPDATA%\Claude-Data\claude_*\claude-code-sessions\…\local_<id>.json`); OVERSEER
  reads only its `title`. The format is undocumented, and chats outside the desktop app (or
  from before bridge 1.0.8) show as "Claude chat <id>".
* Hooks store an absolute path to `bridge.mjs`. Moving, replacing or deleting that folder
  (for example extracting a new ZIP somewhere else) leaves the hooks pointing at nothing
  until you re-run the installer from the new folder. The setup .exe keeps one folder across
  upgrades to avoid this.
* `MessageDisplay` is mapped but not installed by default because it fires per streamed batch.
* `SubagentStart`/`SubagentStop` are liveness records on the parent session. They
  never restart/complete its turn, answer its requests or refresh its action lease.
* Ingestion `sequence` is per session and starts at 1 for each session id.
* A hook that cannot acquire the spool lock within its budget drops its record (counted). Under
  sustained contention, prefer `--events=` to narrow the installed set rather than assuming
  every event is captured.
* Only settings files are managed. Managed policy settings, plugin hooks and project
  `.claude/settings.json` are out of scope.
