#requires -Version 7.0
<#
.SYNOPSIS
    Runs the real upgrade script with display enumeration and apphost launches mocked.
    No display settings or application windows are touched; no Pester dependency.
#>
[CmdletBinding()]
param([string] $EvidenceDirectory = 'artifacts/upgrade-placement-tests')

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repo = Split-Path -Parent $PSScriptRoot
$root = Join-Path $repo (Join-Path $EvidenceDirectory ([Guid]::NewGuid().ToString('N')))
$payload = Join-Path $root 'payload'
New-Item -ItemType Directory -Path $payload -Force | Out-Null
$releaseDirectory = Join-Path $repo 'src/TheOverseer.App/bin/Release/net10.0-windows/win-x64'
$apphost = Join-Path $releaseDirectory 'TheOverseer.App.exe'
if (-not (Test-Path -LiteralPath $apphost)) { throw 'Build Release before running this check.' }
# A package-shaped payload: the apphost, the assemblies the startup-helper probe
# references, and the shipped integration the fixture package is expected to carry.
foreach ($file in @('TheOverseer.App.exe', 'TheOverseer.App.dll', 'TheOverseer.Core.dll', 'TheOverseer.UI.dll')) {
    Copy-Item -LiteralPath (Join-Path $releaseDirectory $file) -Destination $payload
}
New-Item -ItemType Directory -Path (Join-Path $payload 'integrations/claude-code') -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $repo 'integrations/claude-code/bridge.mjs') -Destination (Join-Path $payload 'integrations/claude-code')
@{version='fixture';sourceCommit='fixture';sourceShortCommit=''} | ConvertTo-Json |
    Set-Content -LiteralPath (Join-Path $payload 'release.json') -Encoding utf8
$archive = Join-Path $root 'fixture.zip'
Compress-Archive -Path (Join-Path $payload '*') -DestinationPath $archive
$original = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'Test-ReleaseUpgrade.ps1') -Raw

# Replace only the two external boundaries, leaving all production branches, early
# exits, placement seeding, evidence generation and cleanup unchanged.
$mockLaunch = @'
function Invoke-PackagedRun([string] $executable, [string] $name, [string[]] $arguments, [System.Collections.IDictionary] $environment, [string] $workingDirectory, [string] $logDirectory, [int] $timeoutSeconds) {
    $dataIndex = [Array]::IndexOf($arguments, '--data-directory')
    $state = Get-Content -LiteralPath (Join-Path $arguments[$dataIndex + 1] 'state.json') -Raw | ConvertFrom-Json
    @{name=$name;arguments=$arguments;monitor=$state.monitorDevice;executable=$executable} | ConvertTo-Json -Compress |
        Add-Content -LiteralPath (Join-Path (Split-Path -Parent $workingDirectory) 'mock-launches.jsonl')
    return @{ExitCode=0;TimedOut=$false;Stdout='mocked'}
}
'@

# Three launches are expected once the windowed comparison runs: the previous build on
# the seeded data directory, the candidate on a copy of it, and the previous build
# again on a copy of what the candidate wrote (the rollback run).
foreach ($case in @(
    @{Name='no-secondary';Monitor='';OptIn=$false;Expected=0},
    @{Name='secondary';Monitor='\\.\DISPLAY2';OptIn=$false;Expected=3},
    @{Name='primary-opt-in';Monitor='';OptIn=$true;Expected=3}
)) {
    $tokens = $null; $errors = $null
    $ast = [System.Management.Automation.Language.Parser]::ParseInput($original, [ref]$tokens, [ref]$errors)
    if ($errors.Count) { throw 'Upgrade script does not parse.' }
    $boundaries = $ast.FindAll({param($node)
        $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
        $node.Name -in @('Get-SecondaryMonitor','Invoke-PackagedRun')
    }, $true) | Sort-Object { $_.Extent.StartOffset } -Descending
    if (@($boundaries).Count -ne 2) { throw 'Expected exactly two mock boundaries.' }
    $modified = $original
    foreach ($boundary in $boundaries) {
        $replacement = if ($boundary.Name -eq 'Invoke-PackagedRun') { $mockLaunch }
            else { "function Get-SecondaryMonitor { return '" + $case.Monitor + "' }" }
        $modified = $modified.Substring(0,$boundary.Extent.StartOffset) + $replacement +
            $modified.Substring($boundary.Extent.EndOffset)
    }
    $scriptFile = Join-Path $root ($case.Name + '.ps1')
    Set-Content -LiteralPath $scriptFile -Value $modified -Encoding utf8
    $evidence = Join-Path $root $case.Name
    $arguments = @('-NoProfile','-File',$scriptFile,'-PreviousArchivePath',$archive,
        '-CandidateArchivePath',$archive,'-EvidenceDirectory',$evidence)
    if ($case.OptIn) { $arguments += '-AllowVisibleChecksOnPrimary' }
    $output = & (Join-Path $PSHOME 'pwsh.exe') @arguments 2>&1
    $code = $LASTEXITCODE
    $output | Set-Content -LiteralPath (Join-Path $root ($case.Name + '.log'))
    $reports = @(Get-ChildItem -LiteralPath $evidence -Filter 'upgrade-evidence-*.json' -Recurse)
    if ($reports.Count -ne 1) { throw ($case.Name + ': missing evidence') }
    $report = Get-Content -LiteralPath $reports[0].FullName -Raw | ConvertFrom-Json
    $launchFiles = @(Get-ChildItem -LiteralPath $evidence -Filter 'mock-launches.jsonl' -Recurse)
    $launches = @($launchFiles | ForEach-Object { Get-Content -LiteralPath $_.FullName | ConvertFrom-Json })
    if ($launches.Count -ne $case.Expected) { throw ($case.Name + ': wrong launch count ' + $launches.Count) }
    if ($case.Expected -eq 0) {
        if ($code -eq 0 -or -not $report.visibleChecksDeferred) { throw 'Deferral must fail qualification and be recorded.' }
    } else {
        if ($code -ne 0 -or $report.visibleChecksDeferred) { throw ($case.Name + ': unexpected failure: ' + ($output -join "`n")) }
        if ($case.Monitor -and @($launches | Where-Object { $_.monitor -ne $case.Monitor }).Count) {
            throw 'Both packages must be seeded on the secondary monitor.'
        }
        if ($launches[0].arguments -contains '--placement-monitor') { throw 'Old package received unsupported argument.' }
        if ($case.Monitor -and $launches[1].arguments -notcontains '--placement-monitor') { throw 'Candidate placement argument missing.' }

        # Check names the qualification contract depends on, including the ones the
        # earlier version of the script already published.
        $names = @($report.checks | ForEach-Object { $_.check })
        foreach ($required in @(
            'visible-checks-placement', 'both-packaged-apphosts-present', 'candidate-has-release-identity',
            'previous-build-runs', 'candidate-build-runs', 'candidate-preserves-previous-settings',
            'candidate-preserves-custom-recording', 'candidate-keeps-data-directory-spool',
            'candidate-preserves-spool-event-records', 'candidate-preserves-spool-current-state',
            'integration-recorded-executable-reference-resolves', 'startup-command-uses-production-helper',
            'rollback-previous-build-runs', 'rollback-preserves-candidate-settings',
            'pre-update-backup-restores-previous-data', 'candidate-identity-differs-from-previous')) {
            if ($names -notcontains $required) { throw ($case.Name + ': missing check ' + $required) }
        }

        # The third launch is the rollback: the previous package, against a copy of the
        # candidate data directory, and never with the candidate-only argument.
        if ($launches.Count -eq 3) {
            if ([string]$launches[2].name -ne 'rollback-run') { throw 'The third launch must be the rollback run.' }
            if ([string]$launches[2].executable -ne [string]$launches[0].executable) { throw 'The rollback run must use the previous package.' }
            if ($launches[2].arguments -contains '--placement-monitor') { throw 'The rollback run received the candidate-only placement argument.' }
            $candidateDataIndex = [Array]::IndexOf($launches[1].arguments, '--data-directory') + 1
            $rollbackDataIndex = [Array]::IndexOf($launches[2].arguments, '--data-directory') + 1
            if ($launches[1].arguments[$candidateDataIndex] -eq $launches[2].arguments[$rollbackDataIndex]) {
                throw 'The rollback run must not reuse the candidate data directory in place.'
            }
            if (-not $report.rollback.performed) { throw 'The rollback run must be recorded in the evidence.' }
            if (-not $report.rollback.backupRestoreVerified) { throw 'The backup/restore boundary must be recorded as verified.' }
            if (@($report.failingDifferences).Count -ne 0) { throw 'Mocked runs must not report failing differences.' }
            if ([int]$report.comparedPathCount -lt 60) { throw ('The comparison covered only ' + $report.comparedPathCount + ' JSON paths.') }
            if (@($report.checksNotPerformed).Count -lt 5) { throw 'checksNotPerformed must be recorded in the evidence.' }
            if (@($report.normalizationJustifications).Count -lt 10) { throw 'Every normalization justification must be recorded.' }
            if (-not $report.startupTarget.helperExecuted) { throw 'The production StartupRegistration helper was not executed.' }
            if (-not $report.startupTarget.requestedByProbe.staleDetected) { throw 'The production helper must report the stale recorded path after the move.' }
            if (-not $report.startupTarget.requestedByProbe.optInPreserved) { throw 'The opt-in state must survive the move.' }
            if (-not $report.shortcut.created -or -not $report.shortcut.isEvidenceCopy) { throw 'The shortcut must be created inside the evidence directory only.' }
            if (-not $report.shortcut.followsCandidateDirectory) { throw 'The copied shortcut must follow the candidate directory.' }
            if ([string]$report.spool.recordsAfterCandidate -ne '6') { throw ('The seeded spool must keep its 6 records, saw ' + $report.spool.recordsAfterCandidate) }
            if (-not $report.integrationRecord.fixtureUnchangedByRuns) { throw 'The integration record must not be rewritten by the runs.' }
        }
    }
    Write-Host ('PASS ' + $case.Name + ': launches=' + $launches.Count + ', exit=' + $code)
}
Write-Host ('Evidence: ' + $root)
