# THE OVERSEER PROJECT

**OVERSEER** is a private Windows desktop companion for Codex sessions.
Repository: `the-overseer`. Solution: `TheOverseer.sln`.

This repository currently contains only the bootstrap scaffold. Monitoring,
task state, HUD behavior, history, and voice are awaiting the four-plan
Astra planning → DeepSeek implementation → Astra review workflow.

## Naming and frozen sources

The application/product name is **OVERSEER**. **THE OVERSEER PROJECT** is the
project-level name only. The requested `TheOverseer.*` project and namespace
identifiers remain unchanged. Internal components retain descriptive names.

`AGENTS.md` and `docs/V1_2_IMPLEMENTATION_SPEC.md` are preserved byte-for-byte
from the supplied documents. Their older product naming is superseded by the
user's naming direction above; the V1 scope is unchanged.

The original light/dark PNG references are in `design/reference/`.
Detailed visual refinement is deferred in `docs/UI_SPEC_v0.2.md`.

## Projects

- `TheOverseer.App`: Windows x64 WPF application entry point; references Core and UI.
- `TheOverseer.Core`: plain .NET library, with Models, Monitoring, History, and Voice
  boundaries reserved as empty folders; no WPF dependency.
- `TheOverseer.UI`: WPF presentation library; references Core; minimal window only.
- `TheOverseer.Tests`: xUnit tests; references Core.

The stack is C# / .NET 10 LTS / WPF / xUnit. Future JSON, HTTP, and audio work
uses System.Text.Json, HttpClient, and WAV playback as specified. No runtime
NuGet dependencies have been added.

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
```

The window is a bootstrap placeholder, not the Plan 2 HUD. The initial test checks
that the Core assembly remains independent of application and presentation code.

## Next action

On `impl/p1-monitoring`, ask Astra:

> Act as Astra planning for the whole Plan 1 / Goal 1. Read AGENTS.md,
> README.md, docs/V1_2_IMPLEMENTATION_SPEC.md, docs/IMPLEMENTATION_STATE.md,
> the repository tree, git status, recent commits, relevant source/tests, and
> relevant upstream monitoring files. Write .agent/HANDOFF.md with the required
> sufficient statistics and a directly executable DeepSeek prompt. Account for
> the completed bootstrap and use OVERSEER as the product name. Do not implement
> Plan 1 or split it into human-managed micro-goals.

`.agent/HANDOFF.md` is ignored and currently only marks planning as pending.
The persistent plan ledger intentionally leaves all Plan 1 gates pending.
