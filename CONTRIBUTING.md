# Contributing to OVERSEER

Use .NET 10 SDK (global.json selects a supported feature band), Node.js 24 and
PowerShell 7 on Windows x64. The app has no third-party runtime NuGet dependencies;
the bridges use only Node standard modules. Test packages are restored from NuGet.

```powershell
dotnet restore TheOverseer.sln
dotnet build TheOverseer.sln -c Release --no-restore --disable-build-servers -p:UseSharedCompilation=false -nodeReuse:false
dotnet test TheOverseer.sln -c Release --no-build --no-restore
Push-Location integrations/deepseek-harness
node --test
Pop-Location
Push-Location integrations/claude-code
node --test
Pop-Location
./scripts/Test-PreUserReport.ps1
./scripts/Test-ReleaseUpgradeComparison.ps1
./scripts/Test-ReleaseUpgradePlacement.ps1
node scripts/Build-Site.mjs
node scripts/Check-Site.mjs
```

Run bridge suites sequentially. Packaged display tests use a secondary display;
intentional activation, primary-display tests and display changes belong in an idle
qualification session. See the release runbook for the full package/upgrade suite.
Never test by replacing a live installation or editing personal harness profiles.

Read AGENTS.md and the implementation state before changing architecture. Keep
monitoring, normalized state, UI, history and voice separate. Preserve bounded
reads/storage, truthful source failure, privacy, voice deduplication and shared theme
layout. Codex approvals remain Unsupported. Do not copy the upstream HUD presentation.
The V1 specification is frozen; discuss a feature's scope before implementing it.

Use one branch per change. Describe the trigger, behavior change, meaningful tests
and remaining limits in your pull request. Use synthetic fixtures. Retain upstream
notices and license new original contributions under this repository's MIT license.
Do not commit artifacts, logs, credentials or private session data. Run the required
build/tests and leave the branch ready for review; main requires review before merge.

Commit with the human contributor's verified GitHub identity. Codex, Claude,
DeepSeek and other AI tools are assistance, not separate commit authors or
co-authors. Do not guess who contributed from their choice of AI tool. Winston's
work uses WinChurchill; another person must use their own identity. Preserve
third-party notices and genuine human attribution.
