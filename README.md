# THE OVERSEER PROJECT

**OVERSEER** is a private Windows desktop companion for Codex sessions.
Repository: `the-overseer`. Solution: `TheOverseer.sln`.

Plan 1 (Foundation and Monitoring) is implemented on `impl/p1-monitoring`: a
bounded, read-only local monitor that turns Codex rollout records into
normalized task state, current-action labels, attention transitions and explicit
monitoring health. The HUD, activity history, voice and release plans are not
implemented yet; normal application startup still shows the bootstrap scaffold
window.

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
  Hosts the finite `--monitor-debug` console harness and the optional read-only
  Windows state-store activity source.
- `TheOverseer.Core`: plain .NET library with `Models/`, `Monitoring/`,
  `Configuration/` and `Diagnostics/` (plus empty `History/`, `Voice/` boundaries);
  no WPF dependency.
- `TheOverseer.UI`: WPF presentation library; references Core; minimal window only.
- `TheOverseer.Tests`: xUnit tests; references Core. 117 tests cover the bounded
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

Astra Review 1 reviews `impl/p1-monitoring` against `main` using
`docs/IMPLEMENTATION_STATE.md` for the commit list, evidence and limitations.
HUD, history/voice and release work remain in Plans 2–4.
