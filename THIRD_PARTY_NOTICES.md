# Third-party notices

## Adapted monitoring source: codex-monitor-hud (MIT)

Upstream: https://github.com/LH-03/codex-monitor-hud
Pinned revision: `df1d5b9e3d4b701de979b5c84a006db7cca5112f`
License: MIT, copyright (c) 2026 Codex Monitor HUD Contributors.
The full license text is preserved at `third_party/codex-monitor-hud/LICENSE`.

The following upstream files were **adapted** (not copied wholesale) into
`TheOverseer.Core`. Each adapted file carries a header comment naming its
source path and revision.

| Upstream path (at the pinned revision) | Adapted into | Reused | Removed / added |
| --- | --- | --- | --- |
| `src-dotnet/CodexMonitorHud.Core/Sessions/IncrementalJsonlReader.cs` | `src/TheOverseer.Core/Monitoring/Sessions/IncrementalJsonlReader.cs` | Byte-offset reads, pooled buffers, partial UTF-8/JSON preservation, oversize discard, EOF/newline handling | Added a strict pending-record byte cap, generation tracking, typed read results, `SeekTo`; no raw line retention beyond the parser |
| `src-dotnet/CodexMonitorHud.Core/Sessions/BoundedTailReader.cs` | `src/TheOverseer.Core/Monitoring/Sessions/TailWindowReader.cs` | Backward chunked tail reading, boundary handling, per-line ceiling | Removed accounting/allowance projection and `ReadLatestSnapshot`; added explicit window/record caps and oversize diagnostics |
| `src-dotnet/CodexMonitorHud.Core/Sessions/SessionDiscovery.cs` | `src/TheOverseer.Core/Monitoring/Sessions/SessionDiscovery.cs` | Newest-file preference, lock probe, safe enumeration skeleton | Replaced unbounded recursion and per-directory `ToArray()` with directory/entry budgets, reparse-point refusal, and resumable continuation state |
| `src-dotnet/CodexMonitorHud.Core/Sessions/SessionChangeTracker.cs` | `src/TheOverseer.Core/Monitoring/Sessions/SessionChangeTracker.cs` | FileSystemWatcher wiring, dirty/structural/overflow flags | Added bounded hints, typed watcher-failure diagnostics, explicit waiting; polling and reconciliation remain independent of the watcher |
| `src-dotnet/CodexMonitorHud.Core/Sessions/SessionIdentityReader.cs` | `src/TheOverseer.Core/Monitoring/Sessions/SessionIdentityReader.cs` | `session_meta` parsing, surface mapping (VS Code originator before `source=vscode`), workspace leaf | Replaced unbounded `ReadLine` with a byte-capped read; added `thread_source`, `parent_thread_id`, `history_mode`, explicit-internal classification and filename-derived provisional identity |
| `src-dotnet/CodexMonitorHud.Core/Sessions/SessionTitleIndex.cs` | `src/TheOverseer.Core/Monitoring/Sessions/SessionTitleIndex.cs` | Official `session_index.jsonl` titles, replacement signature, incremental refresh | Bounded cache with eviction, per-pass byte budget, typed availability, no swallowed failures |
| `src-dotnet/CodexMonitorHud.Core/Sessions/SessionProfile.cs` | `src/TheOverseer.Core/Monitoring/SessionProfile.cs` | Root/index/profile identity | Removed HUD path coupling and the compulsory second provider-specific profile |
| `src-dotnet/CodexMonitorHud.Core/Sessions/SessionActivitySource.cs` | `src/TheOverseer.Core/Monitoring/IRuntimeActivitySource.cs` | Small injectable activity interface | Empty fallback returns an explicit availability result instead of looking like zero rows |
| `src-dotnet/CodexMonitorHud.Core/Parsing/HudRecordParser.cs` | `src/TheOverseer.Core/Monitoring/Parsing/CodexEventParser.cs` | Structural validation guards, lifecycle tags, malformed-data handling | Removed accounting/allowance and silent-completion semantics; added lifecycle/attention/tool-category normalization and a narrow bounded command classifier |
| `src-dotnet/CodexMonitorHud.Core/State/TaskNumberPool.cs` | `src/TheOverseer.Core/Monitoring/State/TaskNumberPool.cs` | Recycle released numbers | Injected clock, explicit release cooldown, allocation by conversation instead of file |
| `src-dotnet/CodexMonitorHud.Core/State/SessionMonitorEngine.cs` (reference only) | `src/TheOverseer.Core/Monitoring/CodexMonitor.cs` | Lessons only: identity retries, path validation, dedupe and backlog ordering | The engine itself was **not** copied: UI/animation/usage/time-state coupling, surface-level grouping and DB-timestamp revival are replaced by normalized state semantics |
| `src-dotnet/CodexMonitorHud.Core/Models/HudRecord.cs`, `Models/HudSnapshot.cs`, `State/SessionState.cs` (reference only) | `src/TheOverseer.Core/Models/*` | Minimal identity/time/lifecycle field naming | No mutable animation/accounting graph |
| `src-dotnet/CodexMonitorHud.App/WindowsSessionActivitySource.cs` | `src/TheOverseer.App/Monitoring/WindowsSessionActivitySource.cs` | Read-only `winsqlite3` access, 75 ms busy timeout, bounded `threads` projection | Added a hard deadline through the SQLite progress handler, root-containment validation, explicit unavailable results; no product database, schema or NuGet package |

Not copied: `MainHudView`, `Presentation/`, `Pricing/`, `OfficialCodexAllowanceReader`,
`Configuration/HudConfigStore`/`HudSettings`, animation recipes, layout matrices,
MCP features, the upstream projects/solution, and the upstream executable test runner.

## Shipped example voice pack (original, no third-party assets)

`assets/voicepacks/example/` (shipped inside the release archive as
`examples/voicepacks/example/`) contains **original synthetic PCM tones**
generated for OVERSEER by `VoicePackValidator.CreateTone` (22050 Hz mono 16-bit
sine tones, 0.15–0.25 s). They are not speech, not recordings and not derived
from any third-party voice, model or asset library, so no additional license
applies. The cues are documented in `assets/voicepacks/example/README.md`, and
the pack is opt-in: voice starts muted and OVERSEER never installs a pack itself.

## Shipped .NET runtime

The self-contained release archive redistributes the .NET runtime and Windows
Desktop runtime files. `scripts/Publish-Release.ps1` copies the license and
third-party notices from the exact Microsoft.NETCore.App runtime package version
listed in the published runtime configuration, plus the license from the exact
Microsoft.WindowsDesktop.App runtime package, into the archive's `licenses/`
directory. No runtime package is directly referenced by the product projects.

## Build and test dependencies

- Microsoft .NET / WPF: platform SDK and runtime, governed by their distributed licenses.
- Microsoft.NET.Test.Sdk: test infrastructure, MIT license.
- xUnit.net (`xunit`): test framework, Apache-2.0 license.
- xUnit.net Visual Studio runner (`xunit.runner.visualstudio`): test adapter, Apache-2.0 license.

Exact package versions are pinned in `tests/TheOverseer.Tests/TheOverseer.Tests.csproj`.
No runtime NuGet package, database, or framework was added by Plan 1.

The supplied website PNGs in `design/reference/` are user-provided design references.
