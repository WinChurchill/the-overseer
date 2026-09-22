# THE OVERSEER PROJECT

**OVERSEER** is a private Windows desktop companion for Codex sessions.
Repository: `the-overseer`. Solution: `TheOverseer.sln`.

Plan 1 (Foundation and Monitoring) is merged into `main`: a bounded, read-only
local monitor that turns Codex rollout records into normalized task state,
current-action labels, attention transitions and explicit monitoring health.

Plan 2 (Complete Usable HUD) is implemented on `impl/p2-hud`: normal startup now
runs one small persistent WPF HUD backed by that live monitor, with honest
collapsed counts, a bounded expanded task list, shared Light/Dark website
themes, drag placement with recovery, a tray menu, opt-in current-user startup
and safe task navigation. Activity history, the sparkline, voice and release
packaging remain Plans 3 and 4.

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
  the HUD lifetime controller and the diagnostics fixtures; also hosts the finite
  `--monitor-debug` console harness and the optional read-only Windows
  state-store activity source.
- `TheOverseer.Core`: plain .NET library with `Models/`, `Monitoring/`,
  `Configuration/` and `Diagnostics/` (plus empty `History/`, `Voice/` boundaries);
  no WPF dependency.
- `TheOverseer.UI`: WPF presentation library; references Core. Contains the single
  `HudWindow`, the `HudViewModel` projection and the WebsiteLight/WebsiteDark
  resource dictionaries.
- `TheOverseer.Tests`: xUnit tests; references Core. Tests cover the bounded
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
| Drag the header row | Moves the HUD; the position is saved (debounced). |
| Click the summary or the chevron | Expands or collapses the task list. |
| `Escape` | Collapses the list. |
| Click a task row | Requests the validated `codex://threads/<id>` route for a resolved Desktop task. |
| Right-click a task row | Copies the task id (explicit action only). |
| `Enter`/`Space` on a focused row | Same as clicking it. |
| Tray icon | Show/Hide, Light/Dark, Always on top, opacity presets, Start with Windows, Reset position, monitoring status, Exit. |

The HUD never activates itself: it is shown with `ShowActivated=false` and the
window is positioned without activation, so monitoring updates, theme changes and
completion events never steal focus. A second normal launch exits quietly and
leaves the running instance's monitor and tray untouched.

### Settings and state

`config.json` gains an additive `hud` section; every existing monitor field is
preserved when the HUD saves:

```json
"hud": {
  "theme": "dark",
  "alwaysOnTop": true,
  "opacity": 0.96,
  "startWithWindows": false
}
```

Opacity is clamped to 0.65–1.0 and only `light`/`dark` themes are accepted.
`state.json` stores placement as display independent pixels relative to the saved
monitor's working area plus the DPI scale in effect, so a DPI change keeps the
same physical spot and a removed monitor is clamped back onto a reachable one.
Corrupt or oversized files fall back to defaults without deleting the original.
Writes are serialized through a same-directory temporary file.

Start with Windows is an opt-in current-user entry (`HKCU\...\Run\OVERSEER`) with a
quoted real executable path and `--start-in-tray`. It is never enabled
automatically, needs no elevation, and disabling removes only that value.

### Task navigation

Only a resolved Desktop task with a UUID-shaped conversation id produces a route.
CLI, VS Code, provisional-identity and non-default-profile tasks keep an honest
"open it manually" fallback, and the id can be copied instead. If the `codex:
` protocol has no registered handler — the case on the machine used for Plan 2 —
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
# Render synthetic previews (light/dark x collapsed/expanded + empty/unavailable)
dotnet run --project src/TheOverseer.App -- --hud-fixture --output-directory artifacts/p2-ui

# Full production composition against a synthetic monitor, then a clean exit
dotnet run --project src/TheOverseer.App -- --hud-smoke --duration 15 --data-directory artifacts/p2-hud-fixture

# Reversible live check of OVERSEER's own Run entry (restores the original state)
dotnet run --project src/TheOverseer.App -- --hud-startup-check
```

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

Astra Review 1 passed with nonblocking notes. Applied fixes, validation evidence
and observability limits are recorded in `docs/IMPLEMENTATION_STATE.md`.
After integrating the reviewed Plan 1 branch, prepare Astra planning for
Plan 2 / Goal 2. HUD, history/voice and release work remain in Plans 2–4.