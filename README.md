# THE OVERSEER PROJECT

**OVERSEER** is a private Windows desktop companion for Codex sessions.
Repository: `the-overseer`. Solution: `TheOverseer.sln`.
User-facing changes are recorded in [CHANGELOG.md](CHANGELOG.md).

Plan 1 (Foundation and Monitoring) is merged into `main`: a bounded, read-only
local monitor that turns Codex rollout records into normalized task state,
current-action labels, attention transitions and explicit monitoring health.

Plan 2 (Complete Usable HUD) is reviewed and merged into `main`: normal startup
runs one small persistent WPF HUD backed by that live monitor, with honest
collapsed counts, a bounded expanded task list, shared Light/Dark website
themes, drag placement with recovery, a tray menu, opt-in current-user startup
and safe task navigation.

Plan 3 (Activity and Voice) is reviewed and merged into `main`: a bounded
60-minute working-count sparkline, and meaningful attention notifications that
speak once per condition through a validated local WAV pack or one configured
generic TTS endpoint, with completion aggregation, cooldown, instant persistent
mute and local fallback.

Plan 4 (Hardening and Release) passed Astra Review 4 with nonblocking notes on
`impl/p4-release`: suspend/resume and window-DPI handling, failure-matrix
regressions, an original opt-in example voice pack, and a self-contained
`OVERSEER-v1-win-x64.zip` built and smoke-tested from a fresh extraction by the
scripts in `scripts/`. The reviewed candidate is under `artifacts/review4-final/`;
398 tests and 28 packaged smoke checks passed. Physical sleep/wake and multi-monitor
qualification remain unverified. Merging and tagging `v1.0.0` are separate steps.

The subsequent user-directed first-use update on `ux/first-use` adds taskbar/Alt+Tab
visibility, three task views and clearer visual status. Its candidate is under
`artifacts/first-use-release/`: 405 tests and all 28 packaged checks passed.

## Naming and frozen sources

The application/product name is **OVERSEER**. **THE OVERSEER PROJECT** is the
project-level name only. The requested `TheOverseer.*` project and namespace
identifiers remain unchanged. Internal components retain descriptive names.

`AGENTS.md` and `docs/V1_2_IMPLEMENTATION_SPEC.md` are preserved byte-for-byte
from the supplied documents. Their older product naming is superseded by the
user's naming direction above; the V1 scope is unchanged. Runtime data lives under
`%LOCALAPPDATA%\OVERSEER` (config and logs), not under the older spec path.

The original light/dark PNG references are in `design/reference/`.
Detailed visual refinement is deferred in `docs/UI_SPEC_v0.2.md`.

## Projects

- `TheOverseer.App`: Windows x64 WPF application entry point; references Core and UI.
  Owns platform services (tray, placement, startup registry, shell navigation),
  the HUD lifetime controller, the voice I/O adapters (pack reader, generic TTS
  client, WAV playback) and the diagnostics fixtures; also hosts the finite
  `--monitor-debug` console harness and the optional read-only Windows
  state-store activity source.
- `TheOverseer.Core`: plain .NET library with `Models/`, `Monitoring/`,
  `Configuration/`, `Diagnostics/`, `History/` (rolling activity trace) and
  `Voice/` (policy, manager, pack validation, notification text); no WPF or
  WinForms dependency and no network or audio code.
- `TheOverseer.UI`: WPF presentation library; references Core. Contains the single
  `HudWindow`, the `HudViewModel` projection and the WebsiteLight/WebsiteDark
  resource dictionaries.
- `TheOverseer.Tests`: xUnit tests; references Core, UI and App. Tests cover the bounded
  readers, discovery, identity, titles, parser, reducer, classifier, attention,
  health, monitor composition, configuration, logging and privacy rules.

The stack is C# / .NET 10 LTS / WPF / xUnit. No runtime NuGet package, database or
new project was added.

## Build and test

On Windows x64 with the .NET 10 SDK on PATH, from this repository root:

```powershell
dotnet restore
dotnet build
dotnet test
dotnet run --project src/TheOverseer.App
```

For this bootstrap workspace, the SDK is installed outside Git in `../.tools/dotnet`.
If it is not already on PATH, initialize the current PowerShell session with:

```powershell
$env:DOTNET_ROOT = (Resolve-Path '../.tools/dotnet').Path
$env:PATH = "$env:DOTNET_ROOT;$env:PATH"
$env:DOTNET_CLI_HOME = [IO.Path]::GetFullPath('../.tools/cli-home')
$env:NUGET_PACKAGES = [IO.Path]::GetFullPath('../.tools/nuget-packages')
```

## Running the HUD

```powershell
dotnet run --project src/TheOverseer.App                      # HUD with the real monitor
dotnet run --project src/TheOverseer.App -- --start-in-tray   # start hidden, tray only
```

Controls:

| Action | Result |
| --- | --- |
| Drag anywhere in the upper summary or expanded status area | Moves the HUD; the position is saved (debounced). Mute/expand remain clickable. |
| Click the chevron | Expands or collapses the task list. |
| Click the status line | Opens the expanded Now view. |
| Now / Recent / This session | Shows current work, recent activity, or tasks touched since OVERSEER launched. |
| Hour button in Recent | Cycles the recent window through 1, 2, 4, 8 and 24 hours. |
| `Escape` | Collapses the list. |
| Click a task row | Requests the validated `codex://threads/<id>` route for a resolved Desktop task. |
| Right-click a task row, then Copy task id | Copies only the selected task id. |
| `Enter`/`Space` on a focused row | Same as clicking it. |
| Right-click the HUD header | Display settings, expansion, startup, reset position, Monitoring details and Exit; available even without a tray. |
| Tray icon | Show/Hide, Light/Dark, Always on top, opacity presets, Start with Windows, Reset position, monitoring status, Exit. |
| Close (×/Alt+F4/system menu) | Hides to the tray and keeps the session running; use Exit to quit completely. |

The visible HUD appears in the Windows taskbar and Alt+Tab. Now is the default
task view: live work and attention, plus completions/errors from the last ten
minutes. Recent defaults to two hours; This session uses observed activity since
this app launch. Headline counts always cover all monitored work, independently
of the selected view. Older and stale rows remain accessible in Recent when
within its time window. The selected view and recent window are saved.

A prominent text and color status shows Working, Needs you, Task failed,
Idle, Limited visibility or Monitor offline. When approval/input
detection is unsupported, the attention count is `?` (or a known lower bound
such as `1+`), and a compact monitoring label discloses that limitation.
Working is green, confirmed attention/errors are red, and uncertain activity is
amber. An incomplete zero is labeled "Limited visibility". Full explanations of
unread history, stale activity and input detection are available in status tooltips
and the header's Monitoring details menu; missing data is never treated as idle.
Always on top remains enabled by default and can be toggled in the header/tray menu.
Active-row timers show elapsed time for the current turn, including waiting time;
action changes do not reset them. Hover for last-activity freshness. A missing
start timestamp displays a dash. Settled rows show time since their last activity.

The executable, taskbar/window and tray use the user-supplied OVERSEER artwork.
The original is preserved in `design/branding/overseer-source.png`; the embedded
Windows icon contains transparent 16, 24, 32, 48, 64, 128 and 256 pixel frames.

The HUD never activates itself: it is shown with `ShowActivated=false` and the
window is positioned without activation, so monitoring updates, theme changes and
completion events never steal focus. Closing the window (×, Alt+F4 or the system
menu) **hides it to the tray** while a working tray exists, so the HUD is never lost
by accident; **Exit** in the header or tray menu runs the full orderly shutdown,
Windows logoff/shutdown is never cancelled, and without a tray the close runs the
same orderly shutdown instead of hiding an unreachable process. A second normal
launch asks the running instance to restore its window and session, then exits
quietly without touching its monitor, tray or settings.

### Settings and state

`config.json` gains an additive `hud` section; every existing monitor field is
preserved when the HUD saves:

```json
"hud": {
  "theme": "dark",
  "alwaysOnTop": true,
  "opacity": 0.96,
  "startWithWindows": false,
  "taskView": "now",
  "recentHours": 2
}
```

Opacity is clamped to 0.65–1.0 and only `light`/`dark` themes are accepted.
Task views accept `now`, `recent`, or `session`; recent hours are clamped to 1–24.
`state.json` stores placement as display independent pixels relative to the saved
monitor's working area plus the DPI scale in effect, so a DPI change keeps the
same physical spot and a removed monitor is clamped back onto a reachable one.
Corrupt or oversized files fall back to defaults without deleting the original.
Writes are debounced and serialized off the UI thread through a same-directory temporary file. Exit waits a bounded time for persistence; a stalled disk can leave the most recent changes unsaved.

Start with Windows is an opt-in current-user entry (`HKCU\...\Run\OVERSEER`) with a
quoted real executable path and `--start-in-tray`. It is never enabled
automatically, needs no elevation, and disabling removes only that value.

### Task navigation

Only a resolved Desktop task with a UUID-shaped conversation id produces a route.
CLI, VS Code, provisional-identity and non-default-profile tasks keep an honest
"open it manually" fallback, and the id can be copied instead. If the `codex:` protocol has no registered handler — the case on the machine used for Plan 2 —
the HUD reports that instead of claiming the task opened.

## Monitor debug harness

The existing App project hosts the finite Plan 1 harness; no extra project exists
for it. It never opens a WPF window and exits cleanly.

```powershell
dotnet run --project src/TheOverseer.App -- --monitor-debug --duration 60 --redact-labels --data-directory artifacts/smoke-data
```

Flags:

| Flag | Meaning |
| --- | --- |
| `--monitor-debug` | Run the finite console monitor instead of the scaffold window. |
| `--duration SECONDS` | Run length, default 60, maximum 3600. |
| `--codex-home PATH` | Override `CODEX_HOME` / `%USERPROFILE%\.codex`. |
| `--data-directory PATH` | Override `%LOCALAPPDATA%\OVERSEER` for config and logs. |
| `--redact-labels` | Redact task labels and paths in console output. |

Exit codes: `0` monitoring was readable for at least one cycle (unsupported
optional capabilities are still disclosed), `2` essential monitoring was
unavailable for the whole run, `1` bad arguments or an unhandled failure.

The harness prints capabilities, health with reason codes and freshness, the
actual `N working · N needs you` summary, one row per task
(`#number label / STATE / action / surface / evidence`), the spec-shaped
per-task blocks, the record/byte counters observed in that run, and the honest
limitations of the installed Codex version.

## HUD diagnostics fixtures

These explicit, isolated, finite modes never register startup, never read real
rollout files and never replace production input:

```powershell
# Render synthetic previews (light/dark x collapsed/Now/Recent/Session + empty/unavailable)
dotnet run --project src/TheOverseer.App -- --hud-fixture --output-directory artifacts/p2-ui

# Full production composition against a synthetic monitor, then a clean exit
dotnet run --project src/TheOverseer.App -- --hud-smoke --duration 15 --data-directory artifacts/p2-hud-fixture

# Reversible live check of OVERSEER's own Run entry (restores the original state)
dotnet run --project src/TheOverseer.App -- --hud-startup-check

# Finite voice smoke: synthetic tones plus an owned loopback TTS endpoint
dotnet run --project src/TheOverseer.App -- --voice-smoke --data-directory artifacts/p3-voice
dotnet run --project src/TheOverseer.App -- --voice-smoke --audible --data-directory artifacts/p3-voice
```

```powershell
# Silent check of the pack selected in the isolated data directory's config.json
dotnet run --project src/TheOverseer.App -- --voice-pack-check --data-directory artifacts/p4-pack-check
```

The voice smoke is silent by default and proves the pack, policy, aggregation,
mute and TTS contracts with generated PCM tones and a loopback HTTP server it
owns; `--audible` additionally sends the clips to the real sound device and
reports that observation separately from API success. No remote account,
credential or spoken sample is required or used.

`--voice-pack-check` plays nothing and is the packaged-asset check:
`--voice-smoke` generates its own pack, so it cannot prove that the pack shipped
inside a release archive loads. This mode loads `<data-directory>/voicepacks/<packId>`
through the same provider the HUD uses, resolves all five event keys, validates
every WAV and exits `0`/`1`.

## Activity history and voice

### Rolling history and sparkline

The HUD keeps an in-memory rolling trace of the last 60 minutes: one bucket per
second (maximum 3,601), each holding the working and needs-human `CountValue`
from the newest snapshot in that second. Nothing is persisted and no labels or
conversation data are stored. Unknown samples and gaps longer than 10 seconds
break the line, partial counts keep a distinct dashed style, and the x-axis is a
fixed 60 minutes with a zero baseline and a `max(1, peak)` y-range. The expanded
HUD labels the graph **Working tasks · past hour**, with a task-count scale and
**60 min ago → Now** axis. Dashed segments show minimum known counts and gaps
are unobserved time, not zero. The collapsed HUD focuses on the current status.
The tooltip retains detailed sample, peak and coverage information.

### Voice notifications

Open **Light & voice…** from the HUD header's right-click menu or the tray menu.
The HUD header has a visible **LIGHT ON / LIGHT OFF** toggle beside mute.
The header menu also exposes **Status light**, with position and width presets.
Choose Off/Top/Bottom/Left/Right and a 6–128 DIP size for the steady status strip.
It uses the HUD's status palette and adds space around the HUD without covering
the controls. The default is Right at 32 DIPs; placement and size are saved.
Drag the colored light itself to resize it; its cursor shows the resize direction.

The same panel lists all nine notification situations, suggested recording text,
trigger descriptions, enable switches and clip availability. **Assign WAV…** copies
a validated recording into `%LOCALAPPDATA%\OVERSEER\voiceclips` and takes precedence
over the configured pack/TTS for that event. **Use pack / TTS** removes the assignment
without deleting your recordings. **Preview** explicitly plays even while muted;
Stop preview, closing the panel or muting stops it. Normal notifications do not
overlap a preview, and events occurring during preview do not replay afterward.
Opening the panel validates local clips but does not contact TTS or play audio.

The original five event codes remain supported, with four additive codes:

| Event code | Trigger | Default |
| --- | --- | --- |
| `monitoring_lost` | Monitoring unavailable for 10 seconds; once per outage | Enabled |
| `monitoring_restored` | Monitoring available again after the loss cue successfully played | Enabled |
| `still_waiting` | One reminder after five minutes of a fresh, observable unresolved request | Disabled |
| `system_online` | Once when monitoring becomes available at app launch | Disabled |

Routine UI actions, user cancellation and immediate work resumption do not speak.
Mute, preview, sleep and settings changes discard pending notifications; enabling
an event or unmuting does not replay old activity. Missing/invalid clips stay silent
and can be inspected in the panel. These controls do not improve upstream Codex
approval/input observability: those events still require available evidence.

Voice starts **muted**; nothing is ever spoken until the user unmutes from the
header button, the header context menu or the tray menu, except an explicit Preview.

Notifications come only from normalized attention transitions, and each active
condition speaks once:

| Condition | Speaks while | Re-arms when |
| --- | --- | --- |
| Approval needed | the task is `NEEDS_APPROVAL` | approval is resolved and the condition reappears, or a new turn starts |
| Input needed | `NEEDS_INPUT`, or an explicit non-blocking needs-human request | the request resolves and the condition reappears, or a new turn starts |
| Task error | the task is `ERROR` in that turn | a new turn or a fresh error episode |
| Task finished | `DONE` (aggregated for 2 seconds) | a new completion episode |

A pending approval announces itself once even though it is also a human need; the
input latch stays held (without a second cue) while approval owns the state.
Defaults: 10-second per-task/per-kind cooldown, 1-second minimum spacing between
utterances, 32-entry queue with permission/input/error ahead of completions,
30-second candidate lifetime, 2-second completion aggregation where a valid
all-complete notice replaces the individual completions. Muting stops current
audio and clears queued work immediately, fences late provider results, and never
replays a backlog on unmute.

### Packs and TTS

`<data-directory>/voicepacks/<packId>/manifest.json` follows the frozen §10
shape (id/name/language/version 1 plus the five event keys). Manifests are capped
at 64 KiB, 16 clips per event, 4 MiB and 15 seconds per clip, and every clip must
be a validated RIFF/WAVE PCM file inside the pack root — absolute, traversal,
UNC, alternate-stream and reparse-point paths are rejected. Variants are chosen
round-robin per event.

Close OVERSEER before editing provider, pack or endpoint settings in `config.json`,
then restart to load them. Mute changes take effect immediately through the HUD.

TTS is opt-in and only ever used when both the provider is set to `tts` and an
endpoint is configured (https anywhere, or http on loopback). The request body is
exactly `{"text":…,"event":…,"task":…}` with a short `Task N` alias — never a
title, id, path, prompt, question, answer or command — and the response must be
`audio/wav`, streamed with a 4 MiB hard cap, a linked timeout (1–15 s, default 5)
and no redirects. A failed TTS attempt may fall back once to the matching local
pack clip; with neither provider available the HUD stays silent and says so.

```json
"voice": {
  "muted": true,
  "provider": "pack",
  "packId": "example",
  "endpoint": null,
  "timeoutSeconds": 5
}
```

## Release package (V1)

Build the release using PowerShell 7 and the .NET 10 SDK from committed source (the script refuses a dirty tree unless
`-AllowDirtyTree` is passed):

```powershell
pwsh -NoProfile -File scripts/Publish-Release.ps1 -OutputDirectory artifacts/p4-release
pwsh -NoProfile -File scripts/Test-PackagedRelease.ps1 -ArchivePath artifacts/p4-release/OVERSEER-v1-win-x64.zip
```

`Publish-Release.ps1` runs the documented self-contained publish
(`Release`, `win-x64`, `PublishSingleFile=false`, `PublishTrimmed=false`,
`PublishAot=false`, `Version=1.0.0`), assembles an explicit payload, writes
`OVERSEER-v1-win-x64.zip`, and records `build-manifest.json` with the source
commit, SDK version, included frameworks, payload file list and archive SHA-256.
The process is repeatable and validated, not byte-identical: archive timestamps
differ between runs.

### Install and run

1. Unzip `OVERSEER-v1-win-x64.zip` anywhere (a per-user folder needs no
   elevation) and keep the extracted folder together.
2. Run `TheOverseer.App.exe`. It is a self-contained x64 folder build: it loads
   the .NET runtime from its own directory and does not need a system .NET
   installation, `dotnet run`, the SDK or this repository.
3. Runtime data stays in `%LOCALAPPDATA%\OVERSEER` (`config.json`,
   `state.json`, logs). Pass `--data-directory PATH` for an isolated run.

Archive contents: the complete publish output, `README.md`, `CHANGELOG.md`,
`THIRD_PARTY_NOTICES.md`, `licenses/` (the upstream codex-monitor-hud MIT license
and the licenses/notices from the exact .NET and Windows Desktop runtime packages
redistributed) and `examples/voicepacks/example/`. No repository files, tests, agent
documents, build caches, logs or private data are included.

### Example voice pack (opt-in)

`examples/voicepacks/example/` contains original synthetic PCM tones (not
speech, no third-party assets) for the five known events. Copy it to
`%LOCALAPPDATA%\OVERSEER\voicepacks\example`, set `"provider": "pack"` and
`"packId": "example"` under `voice` in `config.json`, restart and unmute. The
manifest cannot escape the pack directory, and OVERSEER never installs or
replaces a pack by itself. See `examples/voicepacks/example/README.md`.

### Smoke evidence and known limits

`Test-PackagedRelease.ps1` extracts the ZIP into a fresh directory, verifies the
payload and version metadata, removes workspace SDK paths from the child
environment, and runs the extracted apphost from an unrelated working directory
with isolated data directories: ten HUD fixture renders, `--hud-smoke`,
`--voice-smoke` (owned loopback endpoint), `--voice-pack-check` against the
shipped pack, deterministic `--monitor-debug` against a synthetic Codex home, one
read-only real-monitor run, a 60-second normal HUD run (no foreground change, no
window while started in the tray, second instance exits politely, graceful close
and persisted state) and the `--start-in-tray --no-tray` visible fallback. It
writes `smoke-evidence-<timestamp>.json` and fails on any failed check.

Not covered by that script and still listed as unverified in
`docs/IMPLEMENTATION_STATE.md`: physical sleep/wake, monitor disconnect and
movement across monitors with different DPI, human tray interaction, acoustic
playback by ear, and a clean machine that has no global .NET runtime installed.
Approval and input attention remain unsupported in the installed Codex version.

## Project identity and observed activity (foundation, in progress)

A project-centered round is in progress on `ux/first-use`. The foundation is committed
and tested; the HUD does not display it yet.

- **Project identity.** A task can be attributed to a project by explicit assignment,
  the longest configured root, a locally verified Git repository root, or the
  normalized workspace root, otherwise *Unassigned*. Comparison uses complete path
  segments, so a sibling folder is never merged into a project, two folders with the
  same leaf name stay distinct, and relative or device paths never resolve against
  the process working directory. Linked worktrees of one repository group through
  verified Git metadata while independent clones stay separate, and WSL/POSIX roots
  join Windows projects only through an explicit alias. Roots stay local: summaries
  emit a name and a stable hash, never a path.
- **Harness-qualified identity.** A task key carries the harness (`codex`, `dsh`,
  `claude`) plus a stable source-instance id, so identical conversation ids from
  different harnesses can never collapse into one task.
- **Observed activity.** A bounded in-memory history records what a source actually
  reported: confirmed action intervals, a bounded ten-second belief window marked as
  inferred, and explicit **unknown coverage** where nothing was observed. Idle is
  shown only when inactivity was confirmed, the last action is never extended
  indefinitely, completion/error markers do not erase the underlying interval,
  project activity is the union of overlapping intervals rather than a sum, and
  capacity is bounded (512 histories, 2,048 intervals per task, 100,000 total).

### Harness integrations (external assets)

Two metadata-only bridges live outside the desktop app and are installed only by an
explicit, reversible opt-in step:

- `integrations/claude-code/` — a Node hook bridge plus `install.mjs`/`uninstall.mjs`
  for the documented Claude Code hook events. The helper reads at most 1 MiB of hook
  stdin with a hard two-second deadline, writes one bounded normalized record per
  event into `<spool>/claude/<sourceInstanceId>/events.jsonl` with a single locked
  append, always exits 0 with empty stdout, and never returns a permission decision.
  The installer previews by default, backs up the settings file before writing and
  removes only OVERSEER-owned entries. Claude Code is **not installed** on this
  machine, so the bridge is contract-tested only and has never received a live hook.
- `integrations/deepseek-harness/` — a small host-native observer plugin for the
  official DeepSeek Harness. It runs inside the user's existing `dsh` host, exports
  only allowlisted status metadata to `<spool>/dsh/<sourceInstanceId>/`, never
  throws into the host and never intercepts approval/request decisions. dsh keeps its
  own compressed session storage; OVERSEER never reads or migrates it.

Not yet implemented: the project-first list and activity strips, the composite
multi-source monitor that would consume these spools, the dsh/Claude source adapters
inside the app, and the offline baseline voice. `docs/IMPLEMENTATION_STATE.md` lists
the exact gap and next steps.

## Configuration and logs

`%LOCALAPPDATA%\OVERSEER\config.json` (System.Text.Json) is optional:

```json
{
  "codexHome": null,
  "additionalRoots": [],
  "limits": { "maxTrackedFiles": 64, "globalBytesPerPass": 4194304, "staleLeaseMinutes": 15 },
  "logging": { "enabled": true, "maxBytes": 1048576, "level": "info" },
  "runtimeActivitySource": true
}
```

Every limit is clamped into a bounded range. A malformed file is preserved as
`config.json.corrupt-<timestamp>` and the defaults are used. Logs are written to
`logs\overseer.log` with one rolling backup and contain only stable codes,
counters and numbers — never titles, paths, prompts, assistant text, questions,
answers, command lines or credentials.

## Monitoring rules that matter for review

- Identity is the normalized profile root plus conversation id; several rollout
  files (rollover, continuation, another client surface) collapse into one
  numbered task. A `parent_thread_id` alone is not internal-session evidence;
  explicit `thread_source`/`source.subagent` markers are.
- Canonical states are exactly WORKING, NEEDS_INPUT, NEEDS_APPROVAL, ERROR, DONE,
  IDLE. Monitoring health (HEALTHY/DEGRADED/UNAVAILABLE) is separate, so a broken
  monitor can never be displayed as zero tasks.
- Starts and completions do not require tokens or assistant text. Duplicate
  replay, old-turn evidence and evidence for a terminal turn are rejected.
  Cancellation becomes IDLE with a cancellation marker, not DONE. A failed tool
  is not a failed task.
- A non-blocking input request does not turn WORKING into NEEDS_INPUT; it raises
  attention and sets the needs-human flag independently. An immediate async tool
  return is not a user answer.
- Nothing is inferred from silence, locks or database timestamps. Tasks without
  recent evidence keep their last-known state, are flagged stale and are excluded
  from authoritative counts, which are then reported as partial.
- Approval and input events are transient inside Codex and are not persisted in
  rollouts. The monitor reports those capabilities as `unsupported` rather than
  guessing, and their normalized contracts are covered by synthetic tests.

## Upstream reuse

Monitoring plumbing adapted from `https://github.com/LH-03/codex-monitor-hud` at
revision `df1d5b9e3d4b701de979b5c84a006db7cca5112f` (MIT). Exact source paths,
adaptations and the full license are recorded in `THIRD_PARTY_NOTICES.md` and
`third_party/codex-monitor-hud/LICENSE`. The upstream presentation layer,
accounting, allowance reader, settings host, animation machinery and MCP display
were not imported.

## Next action

The HUD keeps its own header at every size. Use the triangle to expand/collapse,
the square to maximize/restore, and the ellipsis for the menu. Hover over the
square for Windows Snap layouts, or use Win+Arrow / Win+Z. Drag the outer edges
to resize and the plain header to move; double-click the header to maximize.
Compact and larger layouts remember separate geometry and pin preferences.
A narrow window temporarily limits the light to keep content readable without
changing its saved width. Short layouts and Light & voice settings scroll.
See `docs/WINDOW_SCALING_PLAN.md` and the latest implementation ledger entry for
the scope and verification qualifications of this newer work.

Plan 4 passed **Astra Review 4 with nonblocking notes** after fixes on
`impl/p4-release`. Build, 398 tests and 28 final packaged smoke checks passed.
Use `artifacts/review4-final/OVERSEER-v1-win-x64.zip`; earlier candidates are
superseded. Integration and tagging `v1.0.0` remain separate steps. Physical
sleep/wake, mixed-DPI/monitor-removal, acoustic and clean-machine checks remain
explicit qualifications in `docs/IMPLEMENTATION_STATE.md`.
