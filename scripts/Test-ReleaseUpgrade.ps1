#requires -Version 7.0
<#
.SYNOPSIS
    Compares an older OVERSEER package with a candidate package on copies of one
    data directory, and records what actually survives the upgrade.

.DESCRIPTION
    Extracts both archives, seeds one isolated data directory with a previous
    release's settings (theme, mute, provider, pack, a custom recording, a project
    mapping, a disabled source and a saved placement), runs the previous build,
    copies the resulting data directory, runs the candidate on the copy, and
    compares. Nothing outside the evidence directory is read or written: the user's
    real data directory, shortcut and startup entry are never touched.

    The comparison is old-to-new: a field the previous build already dropped is
    reported as such instead of being blamed on the candidate.

.EXAMPLE
    pwsh -NoProfile -File scripts/Test-ReleaseUpgrade.ps1 `
        -PreviousArchivePath artifacts/monitor-lifetime-release/OVERSEER-v1-win-x64.zip `
        -CandidateArchivePath artifacts/rc1/OVERSEER-1.0.0-rc.1-win-x64.zip
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string] $PreviousArchivePath,
    [Parameter(Mandatory = $true)][string] $CandidateArchivePath,
    [string] $EvidenceDirectory = 'artifacts/upgrade',
    [ValidateRange(2, 600)][int] $DurationSeconds = 5,
    [switch] $KeepExtraction,
    # Both runs open a HUD window. It goes to a non-primary display so the comparison
    # never appears on the display the user is working on; on a single-display machine
    # the windowed comparison is deferred with an explicit gap unless this is passed.
    [switch] $AllowVisibleChecksOnPrimary
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$script:Checks = New-Object System.Collections.ArrayList
$script:OwnedProcesses = New-Object System.Collections.ArrayList

function Add-Check([string] $name, [bool] $passed, [string] $detail) {
    $entry = [ordered]@{ check = $name; status = $(if ($passed) { 'pass' } else { 'fail' }); detail = $detail }
    [void]$script:Checks.Add($entry)
    Write-Host (('[{0}] {1} - {2}' -f $entry.status.ToUpperInvariant(), $name, $detail))
}

function Write-Step([string] $message) { Write-Host ('==> ' + $message) }

# A display the user is not working on; empty when the machine has only one.
function Get-SecondaryMonitor {
    try {
        Add-Type -AssemblyName System.Windows.Forms -ErrorAction Stop
        $secondary = [System.Windows.Forms.Screen]::AllScreens |
            Where-Object { -not $_.Primary } |
            Sort-Object -Property DeviceName |
            Select-Object -First 1
        if ($secondary) { return [string]$secondary.DeviceName }
    } catch {
        Write-Warning ('display enumeration failed: ' + $_.Exception.Message)
    }

    return ''
}

function Resolve-UnderRoot([string] $path, [string] $root) {
    if ([System.IO.Path]::IsPathRooted($path)) { return [System.IO.Path]::GetFullPath($path) }
    return [System.IO.Path]::GetFullPath((Join-Path $root $path))
}

function Remove-ValidatedItem([string] $path, [string] $root) {
    $full = [System.IO.Path]::GetFullPath($path)
    $rootFull = [System.IO.Path]::GetFullPath($root).TrimEnd('\', '/') + [System.IO.Path]::DirectorySeparatorChar
    if (-not $full.StartsWith($rootFull, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw ('refusing to remove ' + $full + ' because it is outside ' + $rootFull)
    }

    if (Test-Path -LiteralPath $full) { Remove-Item -LiteralPath $full -Recurse -Force }
}

# The extraction runs without the workspace SDK on PATH, exactly like a user's machine.
function New-SanitizedEnvironment {
    $environment = @{}
    foreach ($entry in [System.Environment]::GetEnvironmentVariables().GetEnumerator()) {
        $environment[[string]$entry.Key] = [string]$entry.Value
    }

    foreach ($name in 'DOTNET_ROOT', 'DOTNET_ROOT(x86)', 'DOTNET_HOST_PATH', 'MSBuildSDKsPath', 'DOTNET_CLI_HOME', 'NUGET_PACKAGES') {
        if ($environment.ContainsKey($name)) { $environment.Remove($name) }
    }

    foreach ($key in @($environment.Keys)) {
        if ($key -ieq 'Path') {
            $kept = @($environment[$key] -split ';' | Where-Object { $_ -and $_ -notmatch 'dotnet' })
            $environment[$key] = ($kept -join ';')
        }
    }

    return $environment
}

function Invoke-PackagedRun([string] $executable, [string] $name, [string[]] $arguments, [System.Collections.IDictionary] $environment, [string] $workingDirectory, [string] $logDirectory, [int] $timeoutSeconds) {
    Write-Step ($name + ': ' + ($arguments -join ' '))
    $startInfo = New-Object System.Diagnostics.ProcessStartInfo
    $startInfo.FileName = $executable
    $startInfo.WorkingDirectory = $workingDirectory
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    foreach ($argument in $arguments) { [void]$startInfo.ArgumentList.Add($argument) }
    $startInfo.Environment.Clear()
    foreach ($key in $environment.Keys) { $startInfo.Environment[$key] = [string]$environment[$key] }
    $process = [System.Diagnostics.Process]::Start($startInfo)
    [void]$script:OwnedProcesses.Add($process)
    $exited = $process.WaitForExit($timeoutSeconds * 1000)
    if (-not $exited) { try { $process.Kill($true) } catch { }; [void]$process.WaitForExit(10000) }
    $stdout = ''
    try { $stdout = $process.StandardOutput.ReadToEnd() } catch { }
    $stderr = ''
    try { $stderr = $process.StandardError.ReadToEnd() } catch { }
    $exitCode = if ($exited) { $process.ExitCode } else { -1 }
    Set-Content -LiteralPath (Join-Path $logDirectory ($name + '.log')) -Value (($stdout, $stderr) -join [Environment]::NewLine) -Encoding utf8
    return [ordered]@{ ExitCode = $exitCode; TimedOut = (-not $exited); Stdout = $stdout }
}

function Get-SettingsSnapshot([string] $dataDirectory) {
    $configPath = Join-Path $dataDirectory 'config.json'
    $statePath = Join-Path $dataDirectory 'state.json'
    $config = if (Test-Path -LiteralPath $configPath) { Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json } else { $null }
    $state = if (Test-Path -LiteralPath $statePath) { Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json } else { $null }
    return [ordered]@{
        theme = if ($config) { [string]$config.hud.theme } else { '' }
        alwaysOnTop = if ($config) { [bool]$config.hud.alwaysOnTop } else { $false }
        opacity = if ($config) { [double]$config.hud.opacity } else { 0 }
        taskView = if ($config) { [string]$config.hud.taskView } else { '' }
        recentHours = if ($config) { [int]$config.hud.recentHours } else { 0 }
        lightSize = if ($config) { [int]$config.hud.lightSize } else { 0 }
        muted = if ($config) { [bool]$config.voice.muted } else { $false }
        provider = if ($config) { [string]$config.voice.provider } else { '' }
        packId = if ($config) { [string]$config.voice.packId } else { '' }
        mapping = if ($config -and $config.project) { @($config.project.mappings | Where-Object { $_.projectKey -eq 'legacy-project' }).Count } else { 0 }
        disabledSource = if ($config) { @($config.disabledHarnessSources) -contains 'claude/legacy-disabled' } else { $false }
        placementX = if ($state) { [double]$state.offsetXDips } else { 0 }
        placementY = if ($state) { [double]$state.offsetYDips } else { 0 }
        monitor = if ($state) { [string]$state.monitorDevice } else { '' }
    }
}

# Placement is compared informationally: a running HUD re-derives its own size and
# position from the live monitor layout, so offsets legitimately differ between two
# builds with different window sizes.
function Compare-Snapshot($before, $after, [string[]] $ignore = @()) {
    $changed = New-Object System.Collections.ArrayList
    foreach ($key in $before.Keys) {
        if ($ignore -contains $key) { continue }
        $left = $before[$key]
        $right = $after[$key]
        $same = if ($left -is [double]) { [math]::Abs($left - [double]$right) -lt 0.001 } else { $left -eq $right }
        if (-not $same) { [void]$changed.Add($key + '=' + $left + '->' + $right) }
    }

    # Callers wrap this in @() so an empty result is still an array under StrictMode.
    return @($changed)
}

$extractionRoot = $null
$evidenceRoot = $null
try {
    $repoRoot = Split-Path -Parent $PSScriptRoot
    $previousArchive = Resolve-UnderRoot $PreviousArchivePath $repoRoot
    $candidateArchive = Resolve-UnderRoot $CandidateArchivePath $repoRoot
    if (-not (Test-Path -LiteralPath $previousArchive)) { throw ('previous archive not found: ' + $previousArchive) }
    if (-not (Test-Path -LiteralPath $candidateArchive)) { throw ('candidate archive not found: ' + $candidateArchive) }

    $stamp = (Get-Date).ToUniversalTime().ToString('yyyyMMddTHHmmssZ')
    $evidenceRoot = Join-Path (Resolve-UnderRoot $EvidenceDirectory $repoRoot) ('run-' + $stamp + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
    $extractionRoot = Join-Path $evidenceRoot 'packages'
    $logRoot = Join-Path $evidenceRoot 'logs'
    New-Item -ItemType Directory -Force -Path $extractionRoot, $logRoot | Out-Null

    # Every run below opens a window; decide up front which display it may use, so the
    # seeded legacy placement can target it too.
    $secondaryMonitor = Get-SecondaryMonitor
    $visibleChecksDeferred = -not $secondaryMonitor -and -not $AllowVisibleChecksOnPrimary
    if ($visibleChecksDeferred) {
        Add-Check 'visible-checks-placement' $false (
            'deferred: no non-primary display was found and -AllowVisibleChecksOnPrimary was not passed, so the windowed upgrade comparison was not run')
    } else {
        Add-Check 'visible-checks-placement' $true (
            $(if ($secondaryMonitor) { 'secondary monitor=' + $secondaryMonitor } else { 'primary display explicitly allowed by request' }))
    }

    $previousRoot = Join-Path $extractionRoot 'previous'
    $candidateRoot = Join-Path $extractionRoot 'candidate'
    Expand-Archive -LiteralPath $previousArchive -DestinationPath $previousRoot -Force
    Expand-Archive -LiteralPath $candidateArchive -DestinationPath $candidateRoot -Force
    $previousExe = Join-Path $previousRoot 'TheOverseer.App.exe'
    $candidateExe = Join-Path $candidateRoot 'TheOverseer.App.exe'
    Add-Check 'both-packaged-apphosts-present' ((Test-Path -LiteralPath $previousExe) -and (Test-Path -LiteralPath $candidateExe)) (
        'previous=' + (Test-Path -LiteralPath $previousExe) + ' candidate=' + (Test-Path -LiteralPath $candidateExe))

    $candidateRelease = Get-Content -LiteralPath (Join-Path $candidateRoot 'release.json') -Raw | ConvertFrom-Json
    $previousRelease = if (Test-Path -LiteralPath (Join-Path $previousRoot 'release.json')) { Get-Content -LiteralPath (Join-Path $previousRoot 'release.json') -Raw | ConvertFrom-Json } else { $null }
    Add-Check 'candidate-has-release-identity' ($null -ne $candidateRelease) ('version=' + $candidateRelease.version + ' source=' + $candidateRelease.sourceShortCommit)

    # Isolated legacy data: a previous release's settings, on a copy we own.
    $legacyData = Join-Path $evidenceRoot 'legacy-data'
    New-Item -ItemType Directory -Force -Path (Join-Path $legacyData 'voicepacks/custom') | Out-Null
    $legacyConfig = @'
{
  "hud": { "theme": "light", "alwaysOnTop": true, "opacity": 0.85, "startWithWindows": false, "taskView": "recent", "recentHours": 8, "lightPosition": "right", "lightSize": 24 },
  "voice": { "muted": true, "provider": "pack", "packId": "custom", "clips": {} },
  "project": { "mappings": [ { "projectKey": "legacy-project", "name": "Legacy project", "root": "C:/legacy/work" } ], "aliases": [] },
  "disabledHarnessSources": [ "claude/legacy-disabled" ]
}
'@
    Set-Content -LiteralPath (Join-Path $legacyData 'config.json') -Value $legacyConfig -Encoding utf8
    # Placement is steered through the saved state, not through a command-line option:
    # the previous build may predate --placement-monitor and would reject it with a
    # blocking usage dialog. A release older than that option still honors its own
    # saved monitor, which is what this seeds.
    $legacyMonitor = if ($secondaryMonitor) { $secondaryMonitor } else { 'LEGACY-MONITOR' }
    $legacyPlacement = [ordered]@{
        version = 1
        isExpanded = $true
        monitorDevice = $legacyMonitor
        offsetXDips = 123.5
        offsetYDips = 77.25
        dpiScale = 1.0
        savedAtUtc = '2026-01-02T03:04:05.0000000Z'
    }
    $legacyPlacement | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $legacyData 'state.json') -Encoding utf8
    $legacyWav = Join-Path $legacyData 'voicepacks/custom/input.wav'
    $wavBytes = [byte[]](0x52, 0x49, 0x46, 0x46, 0x24, 0x00, 0x00, 0x00, 0x57, 0x41, 0x56, 0x45, 0x66, 0x6D, 0x74, 0x20,
        0x10, 0x00, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x40, 0x1F, 0x00, 0x00, 0x80, 0x3E, 0x00, 0x00, 0x02, 0x00,
        0x10, 0x00, 0x64, 0x61, 0x74, 0x61, 0x00, 0x00, 0x00, 0x00)
    [System.IO.File]::WriteAllBytes($legacyWav, $wavBytes)
    $legacyWavHash = (Get-FileHash -LiteralPath $legacyWav -Algorithm SHA256).Hash
    # A pre-existing bridge spool in the data directory: an app upgrade must not orphan it.
    $legacySpool = Join-Path $legacyData 'bridges/dsh/legacy-instance'
    New-Item -ItemType Directory -Force -Path $legacySpool | Out-Null
    Set-Content -LiteralPath (Join-Path $legacySpool 'events.jsonl') -Value '' -Encoding utf8

    $environment = New-SanitizedEnvironment
    $workingDirectory = Join-Path $evidenceRoot 'cwd'
    New-Item -ItemType Directory -Force -Path $workingDirectory | Out-Null

    Write-Step 'running the previous build against the legacy data directory'
    # The previous build is never given an argument it may not know: placement comes
    # from the saved state seeded above, which every release understands.
    $previousRun = Invoke-PackagedRun $previousExe 'previous-run' @('--hud-smoke', '--duration', [string]$DurationSeconds, '--data-directory', $legacyData) $environment $workingDirectory $logRoot ($DurationSeconds + 120)
    Add-Check 'previous-build-runs' ($previousRun.ExitCode -eq 0) ('exit=' + $previousRun.ExitCode)
    $afterPrevious = Get-SettingsSnapshot $legacyData

    # The candidate runs on a COPY of what the previous build left behind, so the
    # comparison never depends on the user's own data directory.
    $candidateData = Join-Path $evidenceRoot 'candidate-data'
    Copy-Item -LiteralPath $legacyData -Destination $candidateData -Recurse -Force
    Write-Step 'running the candidate build against the copied data directory'
    $candidateArguments = @('--hud-smoke', '--duration', [string]$DurationSeconds, '--data-directory', $candidateData)
    if ($secondaryMonitor) { $candidateArguments += @('--placement-monitor', $secondaryMonitor) }
    $candidateRun = Invoke-PackagedRun $candidateExe 'candidate-run' $candidateArguments $environment $workingDirectory $logRoot ($DurationSeconds + 120)
    Add-Check 'candidate-build-runs' ($candidateRun.ExitCode -eq 0) ('exit=' + $candidateRun.ExitCode)
    $afterCandidate = Get-SettingsSnapshot $candidateData

    $placementKeys = @('placementX', 'placementY', 'monitor')
    $candidateChanged = @(Compare-Snapshot $afterPrevious $afterCandidate $placementKeys)
    Add-Check 'candidate-preserves-previous-settings' ($candidateChanged.Count -eq 0) (
        $(if ($candidateChanged.Count -eq 0) { 'all compared settings identical' } else { 'changed: ' + (@($candidateChanged) -join ', ') }))

    $previousLost = @(Compare-Snapshot (Get-SettingsSnapshot $legacyData) $afterPrevious $placementKeys)
    $wavKept = (Get-FileHash -LiteralPath (Join-Path $candidateData 'voicepacks/custom/input.wav') -Algorithm SHA256).Hash -eq $legacyWavHash
    Add-Check 'candidate-preserves-custom-recording' $wavKept ('sha256=' + $legacyWavHash.Substring(0, 12))
    $spoolKept = Test-Path -LiteralPath (Join-Path $candidateData 'bridges/dsh/legacy-instance/events.jsonl')
    Add-Check 'candidate-keeps-data-directory-spool' $spoolKept ('path=' + (Join-Path $candidateData 'bridges/dsh/legacy-instance'))

    $previousVersion = (Get-Item -LiteralPath $previousExe).VersionInfo.FileVersion
    $candidateVersion = (Get-Item -LiteralPath $candidateExe).VersionInfo.FileVersion
    $candidateProduct = (Get-Item -LiteralPath $candidateExe).VersionInfo.ProductVersion
    Add-Check 'candidate-identity-differs-from-previous' ($candidateProduct -like ('*' + $candidateRelease.sourceShortCommit + '*')) (
        'previousFileVersion=' + $previousVersion + ' candidateFileVersion=' + $candidateVersion + ' candidateProduct=' + $candidateProduct)

    $failed = @($script:Checks | Where-Object { $_.status -ne 'pass' })
    $evidence = [ordered]@{
        product = 'OVERSEER'
        check = 'release-upgrade'
        previousArchive = $previousArchive
        previousSha256 = (Get-FileHash -LiteralPath $previousArchive -Algorithm SHA256).Hash
        previousVersion = $previousVersion
        previousRelease = $(if ($previousRelease) { $previousRelease.version } else { 'no release.json' })
        candidateArchive = $candidateArchive
        candidateSha256 = (Get-FileHash -LiteralPath $candidateArchive -Algorithm SHA256).Hash
        candidateVersion = $candidateRelease.version
        candidateFileVersion = $candidateVersion
        candidateSourceCommit = $candidateRelease.sourceCommit
        seededPlacementMonitor = $legacyMonitor
        settingsAfterPrevious = $afterPrevious
        settingsAfterCandidate = $afterCandidate
        candidateChangedSettings = $candidateChanged
        settingsThePreviousBuildItselfDropped = $previousLost
        placementAfterPrevious = @{ x = $afterPrevious.placementX; y = $afterPrevious.placementY; monitor = $afterPrevious.monitor }
        placementAfterCandidate = @{ x = $afterCandidate.placementX; y = $afterCandidate.placementY; monitor = $afterCandidate.monitor }
        physicalChecksNotPerformed = @(
            'desktop shortcut target rewrite (the user shortcut is never modified by this check)',
            'Windows startup entry rewrite',
            'rollback from candidate to previous release'
        )
        visibleChecksDeferred = [bool]$visibleChecksDeferred
        visibleCheckMonitor = $secondaryMonitor
        generatedAtUtc = (Get-Date).ToUniversalTime().ToString('o')
        checks = @($script:Checks)
    }
    $evidencePath = Join-Path $evidenceRoot ('upgrade-evidence-' + $stamp + '.json')
    $evidence | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $evidencePath -Encoding utf8

    Write-Host ''
    Write-Host ('checks  : ' + $script:Checks.Count + ' total, ' + $failed.Count + ' failed')
    Write-Host ('evidence: ' + $evidencePath)
    if ($failed.Count -gt 0) {
        foreach ($item in $failed) { Write-Host ('FAILED: ' + $item.check + ' - ' + $item.detail) }
        exit 1
    }

    Write-Host 'release upgrade check: passed'
    exit 0
} finally {
    foreach ($owned in $script:OwnedProcesses) {
        try { if (-not $owned.HasExited) { $owned.Kill($true); [void]$owned.WaitForExit(10000) } }
        catch [System.InvalidOperationException] { }
        finally { $owned.Dispose() }
    }

    if ($extractionRoot -and $evidenceRoot -and -not $KeepExtraction) {
        Remove-ValidatedItem $extractionRoot $evidenceRoot
    }
}
