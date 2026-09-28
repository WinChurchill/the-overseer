#requires -Version 7.0
<#
.SYNOPSIS
    Runs the six pre-user-test areas using the existing source and package checks.
.DESCRIPTION
    Sequential runs avoid bridge contention and competing windows. Visible checks
    use a secondary display; intentional activation is deferred unless requested.
    Reports distinguish automated results from outstanding physical/live-host tests.
    Run against trusted local release archives only. No live configuration is changed.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $CandidateArchivePath,
    [Parameter(Mandatory)][string] $PreviousArchivePath,
    [string] $EvidenceDirectory = 'artifacts/pre-user',
    [switch] $IncludeActivationChecks
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
function FullPath([string] $path) {
    if ([IO.Path]::IsPathRooted($path)) { return [IO.Path]::GetFullPath($path) }
    return [IO.Path]::GetFullPath((Join-Path $repo $path))
}
$candidate = FullPath $CandidateArchivePath
$previous = FullPath $PreviousArchivePath
foreach ($archive in @($candidate, $previous)) {
    if (-not (Test-Path -LiteralPath $archive -PathType Leaf)) { throw "Archive missing: $archive" }
}
$root = Join-Path (FullPath $EvidenceDirectory) ('run-' + [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssZ') + '-' + [Guid]::NewGuid().ToString('N').Substring(0,8))
[void](New-Item -ItemType Directory -Path $root)
$pwsh = (Get-Process -Id $PID).Path
$dotnet = (Get-Command dotnet -ErrorAction Stop).Source
$steps = [Collections.Generic.List[object]]::new()

function Run([string] $name, [string] $file, [string[]] $arguments, [int] $timeoutSeconds = 1800) {
    Write-Host "Starting $name"
    $log = Join-Path $root ($name + '.log')
    $process = [Diagnostics.Process]::new()
    $start = [Diagnostics.ProcessStartInfo]::new($file)
    $start.WorkingDirectory = $repo
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($argument in $arguments) { [void]$start.ArgumentList.Add($argument) }
    $process.StartInfo = $start
    $exitCode = -1
    $errorText = ''
    $stdout = $null
    $stderr = $null
    $timer = [Diagnostics.Stopwatch]::StartNew()
    try {
        [void]$process.Start()
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit($timeoutSeconds * 1000)) {
            $process.Kill($true)
            [void]$process.WaitForExit(10000)
            throw "Timed out after $timeoutSeconds seconds"
        }
        $exitCode = $process.ExitCode
    } catch { $errorText = $_.Exception.Message }
    finally {
        $output = if ($null -ne $stdout) { $stdout.GetAwaiter().GetResult() } else { '' }
        $errors = if ($null -ne $stderr) { $stderr.GetAwaiter().GetResult() } else { '' }
        ($output + "`n" + $errors + "`n" + $errorText) | Set-Content -LiteralPath $log -Encoding utf8
        $process.Dispose()
    }
    $status = if ($exitCode -eq 0 -and -not $errorText) { 'pass' } else { 'fail' }
    $steps.Add([ordered]@{ name = $name; status = $status; exitCode = $exitCode; seconds = [Math]::Round($timer.Elapsed.TotalSeconds, 1); log = $log })
    Write-Host "$name : $status (log: $log)"
}

function RunScript([string] $name, [string] $script, [string[]] $arguments) {
    Run $name $pwsh (@('-NoProfile', '-File', (Join-Path $PSScriptRoot $script)) + $arguments)
}

# Source tests and package tests are identified separately: an older archive must
# never acquire credit for a test of today's source tree.
$sourceCommit = (& git -C $repo rev-parse HEAD).Trim()
$sourceDirty = [bool](& git -C $repo status --porcelain)
Run 'build' $dotnet @('build', 'TheOverseer.sln', '-c', 'Release', '--nologo')
Run 'unit-tests' $dotnet @('test', 'TheOverseer.sln', '-c', 'Release', '--no-build', '--nologo', '--logger', 'trx', '--results-directory', (Join-Path $root 'test-results'))
RunScript 'upgrade-comparison-controls' 'Test-ReleaseUpgradeComparison.ps1' @()
RunScript 'upgrade-placement-controls' 'Test-ReleaseUpgradePlacement.ps1' @()

$smokeArguments = @('-ArchivePath', $candidate, '-EvidenceDirectory', (Join-Path $root 'package'), '-KeepExtraction')
if (-not $IncludeActivationChecks) { $smokeArguments += '-SkipActivationChecks' }
RunScript 'package' 'Test-PackagedRelease.ps1' $smokeArguments
$smokeFiles = @(Get-ChildItem -LiteralPath (Join-Path $root 'package') -Filter 'smoke-evidence-*.json' -Recurse -ErrorAction SilentlyContinue)
$smoke = if ($smokeFiles.Count -eq 1) { Get-Content -LiteralPath $smokeFiles[0].FullName -Raw | ConvertFrom-Json } else { $null }
if ($null -ne $smoke -and (Test-Path -LiteralPath (Join-Path $smoke.extractedTo 'TheOverseer.App.exe'))) {
    RunScript 'scaling' 'Test-WindowScaling.ps1' @('-Executable', (Join-Path $smoke.extractedTo 'TheOverseer.App.exe'), '-EvidenceDirectory', (Join-Path $root 'scaling'))
} else {
    $steps.Add([ordered]@{ name = 'scaling'; status = 'fail'; exitCode = -1; seconds = 0; log = 'Package extraction/evidence unavailable; scaling could not run.' })
}
foreach ($mode in @('hud', 'window')) {
    $upgradeArguments = @('-PreviousArchivePath', $previous, '-CandidateArchivePath', $candidate, '-EvidenceDirectory', (Join-Path $root ('upgrade-' + $mode)))
    if ($mode -eq 'window') { $upgradeArguments += '-WindowView' }
    RunScript ('upgrade-' + $mode) 'Test-ReleaseUpgrade.ps1' $upgradeArguments
}

function Area([int] $number, [string] $name, [string] $pattern, [string[]] $sourceSteps, [string[]] $remaining) {
    $checks = if ($null -ne $smoke) { @($smoke.checks | Where-Object { $_.check -match $pattern }) } else { @() }
    $related = @($steps | Where-Object { $_.name -in $sourceSteps })
    $failed = @($checks | Where-Object status -eq 'fail').Count + @($related | Where-Object status -eq 'fail').Count
    $deferred = @($checks | Where-Object status -eq 'deferred')
    [ordered]@{
        number = $number; area = $name
        automatedStatus = $(if ($null -eq $smoke -or $failed -gt 0 -or $checks.Count -eq 0) { 'fail' } elseif ($deferred.Count -gt 0) { 'pass-with-deferrals' } else { 'pass' })
        packageChecks = $checks; supportingSteps = $related
        deferredPackageChecks = $deferred
        remainingQualification = $remaining
    }
}
$areas = @(
    Area 1 'Fresh-machine/install/download' 'archive|extraction|environment|runtime|version|manifest|private|framework|payload' @('build','upgrade-hud','upgrade-window') @('Clean Windows VM without SDK/runtime/Node/checkout; browser download and SmartScreen under standard protections; independent machine/account; signing/distribution review.')
    Area 2 'Coding-agent monitoring' 'monitor-debug|integration-check|composition-check' @('unit-tests') @('Real Claude Code and DeepSeek harness sessions, including concurrent input/approval and disconnect/restart. Codex desktop approvals remain unsupported by the current rollout source; synthetic tests do not qualify that integration.')
    Area 3 'Bridge installation/update/removal' 'packaged-bridge|archive-integrations|upgrade-bridge' @('upgrade-hud','upgrade-window','upgrade-comparison-controls','upgrade-placement-controls') @('Real supported host profiles: opt-in install, update and restore/uninstall. Automated fixture tests do not install into personal harness profiles.')
    Area 4 'Window/tray/lifecycle/startup' 'second-instance|hud-live|hud-tray|hud-no-tray|hud-exit|startup|shortcut|upgrade-' @('upgrade-hud','upgrade-window') @('Human tray clicks and Windows logon/startup. Activation checks require an idle desktop and -IncludeActivationChecks; local tests use isolated data and a fake startup store.')
    Area 5 'Scaling/monitors/recovery' 'hud-fixture|hud-smoke|hud-live-focus' @('scaling') @('Physical pointer drag, menus, Snap keys, 100/125/150/200% display settings, unplug/replug and sleep/wake. Existing display topology only is automated.')
    Area 6 'Voice' 'voice' @('unit-tests') @('Hear baseline and custom clips, check volume/quality and device absence/change. Automated smoke intentionally does not produce audible alerts.')
)
$failedSteps = @($steps | Where-Object status -eq 'fail').Count
$failedAreas = @($areas | Where-Object automatedStatus -eq 'fail').Count
$report = [ordered]@{
    product = 'OVERSEER'; check = 'six-area-pre-user'; generatedAtUtc = [DateTime]::UtcNow.ToString('o')
    sourceTests = @{ commit = $sourceCommit; dirty = $sourceDirty }
    candidate = @{ archive = $candidate; sha256 = (Get-FileHash -LiteralPath $candidate -Algorithm SHA256).Hash; commit = $(if ($null -ne $smoke) { $smoke.sourceCommit } else { $null }) }
    previous = @{ archive = $previous; sha256 = (Get-FileHash -LiteralPath $previous -Algorithm SHA256).Hash }
    automatedStatus = $(if ($failedSteps -gt 0 -or $failedAreas -gt 0) { 'fail' } elseif (@($areas | Where-Object automatedStatus -eq 'pass-with-deferrals').Count -gt 0) { 'pass-with-deferrals' } else { 'pass' })
    releaseQualification = 'pending manual and external checks'
    intentionalActivationEnabled = [bool]$IncludeActivationChecks
    steps = @($steps.ToArray()); areas = $areas
}
$reportPath = Join-Path $root 'pre-user-report.json'
$report | ConvertTo-Json -Depth 15 | Set-Content -LiteralPath $reportPath -Encoding utf8
Write-Host "Six-area automated result: $($report.automatedStatus); qualification remains pending. Evidence: $reportPath"
if ($failedSteps -gt 0 -or $failedAreas -gt 0) { exit 1 }
exit 0
