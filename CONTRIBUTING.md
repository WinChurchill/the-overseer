# Contributing to OVERSEER

Start with [README → Clone and run](README.md#clone-and-run). For a first testing
session, use the [tester handoff](docs/TESTER_HANDOFF.md); the full contributor
suite below is not required just to try the app.

Use .NET 10 SDK (global.json selects a supported feature band),
[Node.js 24](https://nodejs.org/en/download) and PowerShell 7 on Windows x64.
Node is for bridge tests and site tooling, not the HUD itself.
The app has no third-party runtime NuGet dependencies;
the bridges use only Node standard modules. Test packages are restored from NuGet.

## Build and checks

Run in **the repository root in PowerShell 7**, checking each command's exit status
before continuing. These are also the checks in `.github/workflows/verify.yml`:

```powershell
dotnet restore TheOverseer.sln --disable-build-servers -maxcpucount:1 -nodeReuse:false
dotnet build TheOverseer.sln -c Release --no-restore --disable-build-servers -maxcpucount:1 -p:UseSharedCompilation=false -nodeReuse:false
dotnet test TheOverseer.sln -c Release --no-build --no-restore --disable-build-servers -maxcpucount:1 -nodeReuse:false
Push-Location integrations/deepseek-harness
try { node --test --test-concurrency=1 } finally { Pop-Location }
Push-Location integrations/claude-code
try { node --test --test-concurrency=1 } finally { Pop-Location }
./scripts/Test-PreUserReport.ps1
./scripts/Test-ReleaseUpgradeComparison.ps1
./scripts/Test-ReleaseUpgradePlacement.ps1
node scripts/Build-Site.mjs
node scripts/Check-Site.mjs
node scripts/Test-Site.mjs
```

Run bridge suites and test files sequentially. The concurrency test deliberately
creates its own pressure and verifies accounted loss; unrelated test files must
not compete with binding probes for the bridge's fixed 250 ms write budget.
Packaged display tests use a secondary display;
intentional activation, primary-display tests and display changes belong in an idle
qualification session. See the release runbook for the full package/upgrade suite.
For native .NET tests on an occupied workstation, set `OVERSEER_TEST_MONITOR` to
the actual secondary device name (for example `\\.\DISPLAY2`). Native test windows
then use that display without automatic activation; an unavailable or primary
device fails instead of falling back. Popup and drag-capture tests run serially.
Never test by replacing a live installation or editing personal harness profiles.

If VSTest aborts before tests with access denied in `SetParentProcessExitCallback`,
its existing parent-query switch can be disabled for this restricted run only:
`dotnet test TheOverseer.sln -c Release --no-restore -p:_MSTestEnableParentProcessQuery=false -- RunConfiguration.TestSessionTimeout=180000`.
This rebuilds the test runtime configuration; keep ordinary CI defaults. It disables
the testhost's parent-exit watcher, not tests or assertions. Use an owned temporary
directory and retain the original failure plus the actual TRX results.

## Making a change

Read [AGENTS.md](AGENTS.md) and [implementation state](docs/IMPLEMENTATION_STATE.md)
before changing architecture. Keep
monitoring, normalized state, UI, history and voice separate. Preserve bounded
reads/storage, truthful source failure, privacy, voice deduplication and shared theme
layout. Codex approvals remain Unsupported. Do not copy the upstream HUD presentation.
The V1 specification is frozen; discuss a feature's scope before implementing it.

Follow the [code of conduct](CODE_OF_CONDUCT.md). Fork the repository, or use a
branch if you have write access; `main` accepts changes through pull requests and
the Verify check must pass. Use one branch per change. Describe the trigger, behavior change, meaningful tests
and remaining limits in your pull request. Use synthetic fixtures. Retain upstream
notices and license new original contributions under this repository's MIT license.
Do not commit artifacts, logs, credentials or private session data. Run the required
build/tests and leave the branch ready for review; main requires review before merge.

Agents leave changes uncommitted: Winston handles commits and pushes. Human
contributors commit with their own verified GitHub identity. Codex, Claude,
DeepSeek and other AI tools are assistance, not separate commit authors or
co-authors. Do not guess who contributed from their choice of AI tool. Winston's
work uses WinChurchill; another person must use their own identity. Preserve
third-party notices and genuine human attribution.

## Code map and fixtures

`TheOverseer.Core` owns readers, normalized state, project attribution, bounded
history and voice policy. `TheOverseer.UI` projects that state into view models
and WPF views. `TheOverseer.App` composes Windows services and owns lifecycle.
`integrations/` contains independent metadata-only Node producers; their spools
feed the App adapters. Put a change in the layer that owns its behavior.

Use invented rollout/spool records and owned temporary directories. Follow
`Diagnostics/SyntheticProducers.cs`, `HudCompositionCheckRunner.cs` and the
tests in `tests/TheOverseer.Tests`; assert expected transitions, partial coverage,
recovery and bounded reads rather than mirroring implementation details.
Bridge installer tests must preserve unrelated settings in disposable profiles.

A new adapter should declare source identity and capabilities, normalize only
observed evidence, preserve failure isolation and aggregate resource budgets,
and document its exact supported host/engine versions. Add contract, correlation,
restart, malformed/oversized input and privacy checks. A fixture cannot establish
that a real host actually invokes an observer or hook.

For visual changes, render the real UI from synthetic data and inspect the output:

```powershell
dotnet run --project src/TheOverseer.App/TheOverseer.App.csproj -c Release --no-build --no-restore -- --hud-fixture --output-directory artifacts/tester-hud
```

The command exits after rendering both themes and layout/state cases. No live host,
startup registration or personal profile changes are needed. Use `dark-readme-window.png`
and `light-readme-window.png` for the README images; retain the synthetic caption and
update their provenance in the tester handoff when replacing them.

## Qualification and review

The private packaging workflow emits an unsigned RC archive, checksum and source
manifest; it creates no release or tag. It supports manual dispatch after it is
on the default branch and a narrowly scoped bootstrap push on the qualification
branch. Download that artifact and test those bytes instead of rebuilding them.
See [automated qualification](docs/AUTOMATED_QUALIFICATION.md) for orchestration and
clean-machine checks, and [site guidance](site/README.md) for local website previews.

Before requesting review, describe the trigger, final behavior, test evidence
and remaining limits using the PR template. Main requires review; same-chat
review must not be described as independent review. Keep private evidence under
ignored `artifacts/`, and include only redacted summaries in the pull request.
