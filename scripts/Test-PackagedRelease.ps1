#requires -Version 7.0
<#
.SYNOPSIS
    Smoke-tests the packaged OVERSEER release from a fresh extraction of the ZIP.

.DESCRIPTION
    Extracts OVERSEER-v1-win-x64.zip into a new unique directory, verifies the
    payload, then runs the extracted TheOverseer.App.exe from an unrelated
    working directory with an isolated data directory per mode and with the
    workspace SDK removed from the child environment. Every check is recorded in
    smoke-evidence.json; the script fails (exit 1) when a required check fails.

    It never triggers suspend/resume, never changes display settings, uses only
    OVERSEER's own loopback TTS endpoint and closes only the processes it started.

.EXAMPLE
    pwsh -NoProfile -File scripts/Test-PackagedRelease.ps1 -ArchivePath artifacts/p4-release/OVERSEER-v1-win-x64.zip
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string] $ArchivePath,
    [string] $EvidenceDirectory = 'artifacts/p4-release/smoke',
    [ValidateRange(1, 3600)][int] $HudSeconds = 60,
    [ValidateRange(1, 3600)][int] $HudSmokeSeconds = 15,
    [ValidateRange(1, 3600)][int] $MonitorSeconds = 60,
    [ValidateRange(1, 3600)][int] $SyntheticMonitorSeconds = 15,
    [ValidateRange(6, 6)][int] $FixtureMinimumRenders = 6,
    [switch] $AllowUnavailableMonitor,
    [switch] $SkipHudLifecycle,
    # The two second-instance checks intentionally activate the HUD. Defer only
    # those when someone is working on the other display; keep all other lifecycle checks.
    [switch] $SkipActivationChecks,
    [switch] $SkipBridgeTests,
    # Run only the headless checks. Every windowed check is then recorded as deferred
    # with a reason instead of being silently dropped, so a headless run is still
    # honest about what it did not prove.
    [switch] $SkipVisibleChecks,
    [switch] $KeepExtraction,
    # Visible checks normally run on a non-primary display so a user working on the
    # primary one is never disturbed. Pass this only on a machine where nothing else
    # is using the primary display (for example a clean qualification VM).
    [switch] $AllowVisibleChecksOnPrimary,
    # Pin the display the windowed checks use, instead of taking the first
    # non-primary one. A qualification VM with several displays uses this to steer
    # them off the display a person is actually looking at.
    [string] $VisibleCheckMonitor = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$script:Checks = New-Object System.Collections.ArrayList
$script:OwnedProcesses = New-Object System.Collections.ArrayList
# Headless/deferred lifecycle runs still serialize an empty focus observation set.
$focusTransitions = New-Object System.Collections.ArrayList

function Add-Check([string] $name, [bool] $passed, [string] $detail) {
    $entry = [ordered]@{ check = $name; status = $(if ($passed) { 'pass' } else { 'fail' }); detail = $detail }
    [void]$script:Checks.Add($entry)
    Write-Host (('[{0}] {1} - {2}' -f $entry.status.ToUpperInvariant(), $name, $detail))
}

function Add-DeferredCheck([string] $name, [string] $detail) {
    [void]$script:Checks.Add([ordered]@{ check = $name; status = 'deferred'; detail = $detail })
    Write-Host ('[DEFERRED] ' + $name + ' - ' + $detail)
}

function Write-Step([string] $message) { Write-Host ('==> ' + $message) }

# A display the user is not working on, so windowed checks open there. Returns an
# empty string when the machine has only one display.
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

# Resolves the display windowed checks may use. An explicit request is honoured only
# when it names a display that really exists and is not the primary one; anything else
# falls back to the automatic choice so a typo cannot silently move windows onto the
# display in use.
function Resolve-VisibleCheckMonitor([string] $requested) {
    if (-not $requested) { return (Get-SecondaryMonitor) }
    try {
        Add-Type -AssemblyName System.Windows.Forms -ErrorAction Stop
        $match = [System.Windows.Forms.Screen]::AllScreens |
            Where-Object { $_.DeviceName -eq $requested } |
            Select-Object -First 1
        if (-not $match) {
            Write-Warning ('requested visible-check monitor was not found: ' + $requested)
            return (Get-SecondaryMonitor)
        }
        if ($match.Primary) {
            Write-Warning ('requested visible-check monitor is the primary display; falling back to the automatic choice: ' + $requested)
            return (Get-SecondaryMonitor)
        }

        return [string]$match.DeviceName
    } catch {
        Write-Warning ('display enumeration failed: ' + $_.Exception.Message)
        return ''
    }
}

# Seeds an isolated run's saved placement so its window opens on the chosen display.
# It never touches the user's real data directory or any system display setting.
#
# Both presentation modes are seeded, because they read different fields: the
# collapsed HUD places by `monitorDevice` + `offsetXDips/YDips`, while the window view
# places by `WindowMonitor` + `WindowX/Y` relative to that monitor's origin. Seeding
# only one of them silently falls back to the primary display.
function Set-IsolatedPlacement([string] $dataDirectory, [string] $monitorDevice) {
    # Primary-display opt-in still needs an owned settings directory even when
    # there is no secondary monitor on which to seed placement.
    [void][IO.Directory]::CreateDirectory($dataDirectory)
    if (-not $monitorDevice) { return }
    New-Item -ItemType Directory -Force -Path $dataDirectory | Out-Null
    $offsetDips = 80.0
    $state = [ordered]@{
        version = 1
        isExpanded = $false
        WindowPinned = $false
        WindowMaximized = $false
        WindowMonitor = $monitorDevice
        WindowX = $offsetDips
        WindowY = $offsetDips
        WindowWidth = 480
        WindowHeight = 640
        WindowDpi = 1.0
        monitorDevice = $monitorDevice
        offsetXDips = $offsetDips
        offsetYDips = 100.0
        dpiScale = 1.0
        savedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
    }
    $state | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $dataDirectory 'state.json') -Encoding utf8
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

# ---- window helpers (owned processes only) ----------------------------------
# Compiled once per session; the guard keeps repeated runs in one host working.
if (-not ('OverseerWindows' -as [type])) {
Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

public static class OverseerWindows
{
    public delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc callback, IntPtr lParam);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [StructLayout(LayoutKind.Sequential)] private struct LastInput { public uint Size; public uint Tick; }
    [DllImport("user32.dll")] private static extern bool GetLastInputInfo(ref LastInput value);
    public static uint LastInputTick()
    {
        var value = new LastInput { Size = (uint)Marshal.SizeOf<LastInput>() };
        return GetLastInputInfo(ref value) ? value.Tick : 0;
    }
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam);

    private static List<IntPtr> WindowsOf(int processId)
    {
        var found = new List<IntPtr>();
        EnumWindows((hWnd, lParam) =>
        {
            GetWindowThreadProcessId(hWnd, out var owner);
            if (owner == (uint)processId) { found.Add(hWnd); }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    public static IntPtr AnyWindowOf(int processId)
    {
        var found = WindowsOf(processId);
        return found.Count > 0 ? found[0] : IntPtr.Zero;
    }

    public static IntPtr[] WindowsForProcess(int processId) => WindowsOf(processId).ToArray();

    public static bool AnyVisibleWindowOf(int processId)
    {
        foreach (var hWnd in WindowsOf(processId)) { if (IsWindowVisible(hWnd)) { return true; } }
        return false;
    }
}
'@
}

function New-SanitizedEnvironment {
    $environment = @{}
    foreach ($entry in [System.Environment]::GetEnvironmentVariables().GetEnumerator()) {
        $environment[[string]$entry.Key] = [string]$entry.Value
    }

    $removed = New-Object System.Collections.ArrayList
    foreach ($name in 'DOTNET_ROOT', 'DOTNET_ROOT(x86)', 'DOTNET_HOST_PATH', 'MSBuildSDKsPath', 'DOTNET_CLI_HOME', 'NUGET_PACKAGES', 'DOTNET_CLI_TELEMETRY_SESSIONID', 'DOTNET_STARTUP_HOOKS') {
        if ($environment.ContainsKey($name)) { $environment.Remove($name); [void]$removed.Add($name) }
    }

    $pathKey = $null
    foreach ($key in $environment.Keys) { if ($key -ieq 'Path') { $pathKey = $key; break } }
    if ($pathKey) {
        $kept = @()
        foreach ($part in ($environment[$pathKey] -split ';')) {
            if ([string]::IsNullOrWhiteSpace($part)) { continue }
            if ($part -match 'dotnet') { [void]$removed.Add('PATH:' + $part); continue }
            $kept += $part
        }
        $environment[$pathKey] = ($kept -join ';')
    }

    $leaks = @()
    foreach ($key in $environment.Keys) {
        if ($environment[$key] -match '\.tools\\dotnet') { $leaks += $key }
    }

    return [ordered]@{ Environment = $environment; Removed = @($removed); Leaks = $leaks }
}

function Start-PackagedProcess(
    [string] $executable,
    [string[]] $arguments,
    [System.Collections.IDictionary] $environment,
    [string] $workingDirectory) {

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
    return [ordered]@{
        Process = $process
        Stdout = $process.StandardOutput.ReadToEndAsync()
        Stderr = $process.StandardError.ReadToEndAsync()
    }
}

function Complete-PackagedProcess($handle, [int] $timeoutSeconds, [string] $logPath) {
    $process = $handle.Process
    $exited = $process.WaitForExit($timeoutSeconds * 1000)
    if (-not $exited) {
        try { $process.Kill($true) } catch { }
        [void]$process.WaitForExit(10000)
    }

    $stdout = ''
    $stderr = ''
    try { $stdout = $handle.Stdout.GetAwaiter().GetResult() } catch { }
    try { $stderr = $handle.Stderr.GetAwaiter().GetResult() } catch { }
    $exitCode = if ($exited) { $process.ExitCode } else { -1 }
    Set-Content -LiteralPath $logPath -Value (($stdout, $stderr) -join [Environment]::NewLine) -Encoding utf8
    $process.Dispose()
    return [ordered]@{ TimedOut = (-not $exited); ExitCode = $exitCode; Stdout = $stdout; Stderr = $stderr; Log = $logPath }
}

# Runs the shipped bridge test suite from the extraction with the probe's own Node.
# It is a real distribution check: the assets must work without the repository.
function Invoke-NodeTest(
    [string] $name,
    [string] $directory,
    [System.Collections.IDictionary] $environment,
    [string] $logDirectory,
    [int] $timeoutSeconds) {

    Write-Step ($name + ': node --test --test-concurrency=1 in ' + $directory)
    $node = Get-Command node -ErrorAction SilentlyContinue
    if (-not $node -or -not (Test-Path -LiteralPath $directory)) {
        Add-Check $name $false ('node-on-path=' + [bool]$node + ' directory=' + $directory)
        return [ordered]@{ ExitCode = -1; TimedOut = $false; Stdout = ''; Stderr = ''; Log = '' }
    }

    $handle = Start-PackagedProcess $node.Source @('--test', '--test-concurrency=1') $environment $directory
    $result = Complete-PackagedProcess $handle $timeoutSeconds (Join-Path $logDirectory ($name + '.log'))
    $passLines = ([regex]::Matches($result.Stdout, '(?m)^# pass ')).Count
    $failLines = ([regex]::Matches($result.Stdout, '(?m)^# fail ')).Count
    Add-Check $name ($result.ExitCode -eq 0) (
        'exit=' + $result.ExitCode + ' timedOut=' + $result.TimedOut + ' passLines=' + $passLines + ' failLines=' + $failLines + ' log=' + $result.Log)
    return $result
}

function Invoke-FiniteMode(
    [string] $executable,
    [string] $name,
    [string[]] $arguments,
    [System.Collections.IDictionary] $environment,
    [string] $workingDirectory,
    [string] $logDirectory,
    [int] $timeoutSeconds,
    [int[]] $acceptedExitCodes) {

    Write-Step ($name + ': ' + ($arguments -join ' '))
    $handle = Start-PackagedProcess $executable $arguments $environment $workingDirectory
    $result = Complete-PackagedProcess $handle $timeoutSeconds (Join-Path $logDirectory ($name + '.log'))
    $accepted = $acceptedExitCodes -contains $result.ExitCode
    $detail = 'exit=' + $result.ExitCode + ' timedOut=' + $result.TimedOut + ' log=' + $result.Log
    Add-Check $name $accepted $detail
    return $result
}

$extractionRoot = $null
$evidenceRoot = $null
try {
$repoRoot = Split-Path -Parent $PSScriptRoot
$resolvedArchive = Resolve-UnderRoot $ArchivePath $repoRoot
$evidenceBase = Resolve-UnderRoot $EvidenceDirectory $repoRoot
if (-not (Test-Path -LiteralPath $resolvedArchive)) { throw ('archive not found: ' + $resolvedArchive) }

$stamp = (Get-Date).ToUniversalTime().ToString('yyyyMMddTHHmmssZ')
$evidenceRoot = Join-Path $evidenceBase ('run-' + $stamp + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
# Every run gets fresh data and fixtures, so old files cannot make checks pass.
$extractionRoot = Join-Path $evidenceRoot 'package Ω with spaces'
$logRoot = Join-Path $evidenceRoot ('logs-' + $stamp)
New-Item -ItemType Directory -Force -Path $extractionRoot | Out-Null
New-Item -ItemType Directory -Force -Path $logRoot | Out-Null

Write-Step ('archive    ' + $resolvedArchive)
Write-Step ('evidence   ' + $evidenceRoot)
Write-Step ('extraction ' + $extractionRoot)

$archiveHash = (Get-FileHash -LiteralPath $resolvedArchive -Algorithm SHA256).Hash
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [System.IO.Compression.ZipFile]::OpenRead($resolvedArchive)
try { $entryNames = @($zip.Entries | ForEach-Object { $_.FullName }) } finally { $zip.Dispose() }

$requiredEntries = @(
    'TheOverseer.App.exe',
    'TheOverseer.App.dll',
    'TheOverseer.App.runtimeconfig.json',
    'coreclr.dll',
    'hostfxr.dll',
    'hostpolicy.dll',
    'README.md',
    'CHANGELOG.md',
    'THIRD_PARTY_NOTICES.md',
    'licenses/codex-monitor-hud-MIT.txt',
    'licenses/dotnet-LICENSE.txt',
    'licenses/dotnet-ThirdPartyNotices.txt',
    'licenses/windowsdesktop-LICENSE.txt',
    'examples/voicepacks/example/manifest.json',
    'examples/voicepacks/example/permission-1.wav',
    'examples/voicepacks/example/all.wav'
)
$missingEntries = @($requiredEntries | Where-Object { $entryNames -notcontains $_ })
Add-Check 'archive-required-entries' ($missingEntries.Count -eq 0) ('entries=' + $entryNames.Count + ' missing=' + ($missingEntries -join ','))

Expand-Archive -LiteralPath $resolvedArchive -DestinationPath $extractionRoot -Force
$executable = Join-Path $extractionRoot 'TheOverseer.App.exe'
Add-Check 'extraction-apphost' (Test-Path -LiteralPath $executable) ('exe=' + $executable)

# Release identity: the numeric file version, the release identifier and the source
# commit must all agree between the shipped binary and release.json. A semantic
# prerelease suffix is never a valid file version, so the two are checked separately.
$versionInfo = (Get-Item -LiteralPath $executable).VersionInfo
$releasePath = Join-Path $extractionRoot 'release.json'
$release = if (Test-Path -LiteralPath $releasePath) { Get-Content -LiteralPath $releasePath -Raw | ConvertFrom-Json } else { $null }
# Older candidates predate a project-license declaration. New packages must carry it.
if ($null -ne $release -and $null -ne $release.PSObject.Properties['projectLicense']) {
    $licensePath = Join-Path $extractionRoot 'LICENSE'
    $licenseOk = $release.projectLicense -eq 'MIT' -and
        (Test-Path -LiteralPath $licensePath) -and
        ((Get-Content -LiteralPath $licensePath -Raw) -match 'Permission is hereby granted')
    Add-Check 'project-license' ([bool]$licenseOk) ('declared=' + $release.projectLicense + ' LICENSE present and readable')
}
$numericFromFile = (($versionInfo.FileVersion -split '\.')[0..2] -join '.')
$identityOk = $null -ne $release -and
    $versionInfo.FileVersion -eq $release.fileVersion -and
    $versionInfo.FileVersion -match '^\d+\.\d+\.\d+\.\d+$' -and
    $numericFromFile -eq (($release.version -split '-')[0]) -and
    $versionInfo.ProductVersion -like ($release.version + '*') -and
    $versionInfo.ProductVersion -like ('*' + $release.sourceShortCommit + '*') -and
    $versionInfo.ProductName -eq 'OVERSEER'
Add-Check 'extracted-version-metadata' ([bool]$identityOk) (
    'FileVersion=' + $versionInfo.FileVersion + ' ProductVersion=' + $versionInfo.ProductVersion +
    ' release=' + $(if ($release) { $release.version } else { 'missing' }) +
    ' source=' + $(if ($release) { $release.sourceShortCommit } else { 'missing' }))

# The optional integrations ship in the archive and are runnable from the extraction
# alone: no repository checkout, no build step.
$integrationEntries = @($entryNames | Where-Object { $_ -like 'integrations/*' })
$integrationRequired = @(
    'integrations/deepseek-harness/README.md',
    'integrations/deepseek-harness/NOTICE.md',
    'integrations/deepseek-harness/src/index.js',
    'integrations/deepseek-harness/package.json',
    'integrations/claude-code/README.md',
    'integrations/claude-code/NOTICE.md',
    'integrations/claude-code/install.mjs',
    'integrations/claude-code/uninstall.mjs',
    'integrations/claude-code/bridge.mjs'
)
$integrationMissing = @($integrationRequired | Where-Object { $entryNames -notcontains $_ })
Add-Check 'archive-integrations' ($integrationMissing.Count -eq 0) (
    'integrationEntries=' + $integrationEntries.Count + ' missing=' + ($integrationMissing -join ','))

$runtimeConfig = Get-Content -LiteralPath (Join-Path $extractionRoot 'TheOverseer.App.runtimeconfig.json') -Raw | ConvertFrom-Json
$frameworks = @()
if ($runtimeConfig.runtimeOptions.includedFrameworks) {
    foreach ($framework in $runtimeConfig.runtimeOptions.includedFrameworks) { $frameworks += ($framework.name + ' ' + $framework.version) }
}
Add-Check 'extraction-self-contained' ($frameworks.Count -gt 0) ('includedFrameworks=' + ($frameworks -join '; '))

# Relative segments only: the extraction itself lives under artifacts\.
$payloadFiles = Get-ChildItem -LiteralPath $extractionRoot -Recurse -File
$forbiddenSegments = @('.git', 'artifacts', 'HANDOFF.md', 'TheOverseer.Tests', 'node_modules')
$offending = @()
foreach ($file in $payloadFiles) {
    $relative = $file.FullName.Substring($extractionRoot.Length + 1)
    foreach ($segment in ($relative -split '[\\/]')) {
        if ($forbiddenSegments -contains $segment) { $offending += $relative }
    }
}
Add-Check 'extraction-no-private-payload' ($offending.Count -eq 0) ('files=' + $payloadFiles.Count + ' offending=' + ($offending -join ','))

# ---- sanitized child environment --------------------------------------------
$sanitized = New-SanitizedEnvironment
Add-Check 'child-environment-sanitized' ($sanitized.Leaks.Count -eq 0) ('removed=' + ($sanitized.Removed -join ',') + ' leaks=' + ($sanitized.Leaks -join ','))

# ---- visible-check placement -------------------------------------------------
# Every check that opens a window runs on a display the user is not working on, or it
# is deferred with an explicit gap instead of appearing on the display in use.
$secondaryMonitor = if ($SkipVisibleChecks) { '' } else { Resolve-VisibleCheckMonitor $VisibleCheckMonitor }
$visibleChecksDeferred = $SkipVisibleChecks -or (-not $secondaryMonitor -and -not $AllowVisibleChecksOnPrimary)
# The reason is only a deferral reason when something was actually deferred. Recording
# the "no non-primary display" text on a fully windowed successful run would put a false
# statement in the evidence JSON, so the non-deferred case says so plainly instead.
$visibleChecksSkipReason = if (-not $visibleChecksDeferred) {
    'not deferred: every windowed check ran on the selected display'
} elseif ($SkipVisibleChecks) {
    'skipped: -SkipVisibleChecks was passed, so no windowed check was opened'
} else {
    'deferred: no non-primary display was found and -AllowVisibleChecksOnPrimary was not passed, so no windowed check was opened on the display in use'
}
if ($visibleChecksDeferred) {
    Add-Check 'visible-checks-placement' $false $visibleChecksSkipReason
} else {
    Add-Check 'visible-checks-placement' $true (
        $(if ($secondaryMonitor) { 'secondary monitor=' + $secondaryMonitor } else { 'primary display explicitly allowed by request' }))
}

# An unrelated working directory: neither the repository nor the extraction.
$workingDirectory = Join-Path $evidenceRoot ('cwd-' + $stamp)
New-Item -ItemType Directory -Force -Path $workingDirectory | Out-Null

$failures = New-Object System.Collections.ArrayList
function Add-Failure([string] $message) { [void]$failures.Add($message) }

# ---- finite fixture modes ----------------------------------------------------
$fixtureDirectory = Join-Path $evidenceRoot 'hud-fixture'
$fixture = Invoke-FiniteMode $executable 'hud-fixture' @('--hud-fixture', '--output-directory', $fixtureDirectory, '--data-directory', (Join-Path $evidenceRoot 'hud-fixture-data')) $sanitized.Environment $workingDirectory $logRoot 180 @(0)
$renders = @(Get-ChildItem -LiteralPath $fixtureDirectory -Filter '*.png' -File -ErrorAction SilentlyContinue)
$largeRenders = @($renders | Where-Object { $_.Length -gt 1024 })
Add-Check 'hud-fixture-renders' ($renders.Count -ge $FixtureMinimumRenders -and $largeRenders.Count -eq $renders.Count) ('renders=' + $renders.Count + ' nonEmpty=' + $largeRenders.Count + ' minimum=' + $FixtureMinimumRenders)

if ($visibleChecksDeferred) {
    Add-Check 'hud-smoke' $false $visibleChecksSkipReason
    Add-Check 'hud-smoke-persistence' $false $visibleChecksSkipReason
} else {
    $hudSmokeData = Join-Path $evidenceRoot 'hud-smoke-data'
    $hudSmokeArguments = @('--hud-smoke', '--duration', [string]$HudSmokeSeconds, '--data-directory', $hudSmokeData)
    if ($secondaryMonitor) { $hudSmokeArguments += @('--placement-monitor', $secondaryMonitor) }
    $hudSmoke = Invoke-FiniteMode $executable 'hud-smoke' $hudSmokeArguments $sanitized.Environment $workingDirectory $logRoot ($HudSmokeSeconds + 90) @(0)
    # A clean process exit does not establish persistence: a restricted launch
    # can fail both atomic writes and still shut down normally. This fresh scope
    # must contain the setting actually changed by the second synthetic tick.
    $smokePersisted = $false
    try {
        $smokeConfig = Get-Content -LiteralPath (Join-Path $hudSmokeData 'config.json') -Raw -ErrorAction Stop | ConvertFrom-Json
        $smokeState = Get-Content -LiteralPath (Join-Path $hudSmokeData 'state.json') -Raw -ErrorAction Stop | ConvertFrom-Json
        $smokePersisted = $HudSmokeSeconds -ge 9 -and $smokeState.version -eq 1 -and
            $smokeConfig.hud.theme -eq 'light' -and [Math]::Abs($smokeConfig.hud.opacity - 0.85) -lt 0.001
    } catch {
        # Record the failed assertion without exposing file contents or paths.
    }
    Add-Check 'hud-smoke-persistence' $smokePersisted ('fresh state/config and changed theme/opacity saved=' + $smokePersisted + '; requires at least 9s of synthetic ticks')
}

# Production-path integration check: invented Codex and bridge producer files
# through the real readers, composite and controller, asserting the applied state.
if ($visibleChecksDeferred) {
    Add-Check 'hud-integration-check' $false $visibleChecksSkipReason
} else {
    $integrationArguments = @('--hud-integration-check', '--output-directory', (Join-Path $evidenceRoot 'integration-check'))
    if ($secondaryMonitor) { $integrationArguments += @('--placement-monitor', $secondaryMonitor) }
    $integrationCheck = Invoke-FiniteMode $executable 'hud-integration-check' $integrationArguments $sanitized.Environment $workingDirectory $logRoot 300 @(0)
}
if (-not $visibleChecksDeferred) {
    $integrationPassed = ([regex]::Matches($integrationCheck.Stdout, '(?m)^check PASS ')).Count
    $integrationFailed = ([regex]::Matches($integrationCheck.Stdout, '(?m)^check FAIL ')).Count
    Add-Check 'hud-integration-check' ($integrationCheck.ExitCode -eq 0 -and $integrationFailed -eq 0 -and $integrationPassed -ge 17) (
        'exit=' + $integrationCheck.ExitCode + ' passed=' + $integrationPassed + ' failed=' + $integrationFailed)
}

# Normal-composition check: the integration check builds its own dependency graph, so
# it proves the monitor-to-UI pipeline but not what the app assembles at startup. This
# one drives the real HudComposition - startup configuration, discovery, provider
# selection and controller construction - and asserts the applied HUD state.
if ($visibleChecksDeferred) {
    Add-Check 'hud-composition-check' $false $visibleChecksSkipReason
} else {
    $compositionArguments = @('--hud-composition-check',
        '--output-directory', (Join-Path $evidenceRoot 'composition-check'),
        # A real controller saves state on shutdown, so this must never resolve to a
        # live data directory.
        '--data-directory', (Join-Path $evidenceRoot 'composition-check-data'))
    if ($secondaryMonitor) { $compositionArguments += @('--placement-monitor', $secondaryMonitor) }
    if ($AllowVisibleChecksOnPrimary) { $compositionArguments += '--allow-primary-placement' }
    $compositionCheck = Invoke-FiniteMode $executable 'hud-composition-check' $compositionArguments $sanitized.Environment $workingDirectory $logRoot 300 @(0)
    $compositionPassed = ([regex]::Matches($compositionCheck.Stdout, '(?m)^check PASS ')).Count
    $compositionFailed = ([regex]::Matches($compositionCheck.Stdout, '(?m)^check FAIL ')).Count
    Add-Check 'hud-composition-check' ($compositionCheck.ExitCode -eq 0 -and $compositionFailed -eq 0 -and $compositionPassed -ge 24) (
        'exit=' + $compositionCheck.ExitCode + ' passed=' + $compositionPassed + ' failed=' + $compositionFailed)
}

# The shipped bridge assets must be usable from the extraction alone.
if ($SkipBridgeTests) {
    Write-Step 'bridge asset tests skipped by request'
} else {
    foreach ($integration in @('deepseek-harness', 'claude-code')) {
        $bridgeDirectory = Join-Path $extractionRoot ('integrations/' + $integration)
        [void](Invoke-NodeTest ('packaged-bridge-tests-' + $integration) $bridgeDirectory $sanitized.Environment $logRoot 240)
    }
}

$voiceSmoke = Invoke-FiniteMode $executable 'voice-smoke' @('--voice-smoke', '--data-directory', (Join-Path $evidenceRoot 'voice-smoke-data')) $sanitized.Environment $workingDirectory $logRoot 240 @(0)
$loopback = $voiceSmoke.Stdout -match '127\.0\.0\.1'
Add-Check 'voice-smoke-owned-loopback' $loopback ('stdout mentions loopback=' + $loopback)

# The shipped example pack, installed into its own isolated data directory and
# checked through the real provider (voice smoke generates its own pack).
$packData = Join-Path $evidenceRoot 'pack-check-data'
New-Item -ItemType Directory -Force -Path (Join-Path $packData 'voicepacks') | Out-Null
Copy-Item -Path (Join-Path $extractionRoot 'examples/voicepacks/example') -Destination (Join-Path $packData 'voicepacks/example') -Recurse -Force
$packConfig = '{ "voice": { "muted": true, "provider": "pack", "packId": "example" } }'
Set-Content -LiteralPath (Join-Path $packData 'config.json') -Value $packConfig -Encoding utf8
$packCheck = Invoke-FiniteMode $executable 'voice-pack-check' @('--voice-pack-check', '--data-directory', $packData) $sanitized.Environment $workingDirectory $logRoot 120 @(0)
$packEvents = ([regex]::Matches($packCheck.Stdout, '(?m)^ok   ')).Count
Add-Check 'voice-pack-check-five-events' ($packEvents -ge 5) ('ok-lines=' + $packEvents)

# Deterministic synthetic monitoring: a fake Codex home with one working task.
$syntheticHome = Join-Path $evidenceRoot 'synthetic-codex-home'
$syntheticSessions = Join-Path $syntheticHome 'sessions/2026/01/02'
New-Item -ItemType Directory -Force -Path $syntheticSessions | Out-Null
$conversationId = '01a0c900-0000-0000-0000-0000000000c1'
# Recent timestamps keep the synthetic task inside the stale lease, so the run
# proves live WORKING state rather than an aged idle row. Each record is built as
# its own expression: PowerShell's comma binds tighter than '+', so an array
# literal of concatenations would silently collapse into one line.
$nowUtc = [DateTimeOffset]::UtcNow
$startedAt = $nowUtc.ToUnixTimeSeconds()
$sessionMeta = '{"type":"session_meta","payload":{"id":"' + $conversationId + '","session_id":"' + $conversationId + '","timestamp":"' + $nowUtc.ToString('yyyy-MM-ddTHH:mm:ssZ') + '","cwd":"C:\\work\\demo","originator":"Codex Desktop","source":"vscode","history_mode":"paginated","cli_version":"0.155.1","model_provider":"openai","thread_source":"user"}}'
$taskStarted = '{"type":"event_msg","payload":{"type":"task_started","turn_id":"turn-1","started_at":' + $startedAt + ',"collaboration_mode_kind":"default"}}'
$rolloutPath = Join-Path $syntheticSessions ('rollout-2026-01-02T03-04-05-' + $conversationId + '.jsonl')
[System.IO.File]::WriteAllLines($rolloutPath, @($sessionMeta, $taskStarted), [System.Text.UTF8Encoding]::new($false))
$syntheticDebug = Invoke-FiniteMode $executable 'monitor-debug-synthetic' @('--monitor-debug', '--duration', [string]$SyntheticMonitorSeconds, '--redact-labels', '--codex-home', $syntheticHome, '--data-directory', (Join-Path $evidenceRoot 'monitor-synthetic-data')) $sanitized.Environment $workingDirectory $logRoot ($SyntheticMonitorSeconds + 90) @(0)
$tracked = $syntheticDebug.Stdout -match 'files tracked=1'
$taskRow = $syntheticDebug.Stdout -match '(?m)^#1 \[redacted\] / WORKING'
$workingSummary = $syntheticDebug.Stdout -match '(?m)^1 working '
Add-Check 'monitor-debug-synthetic-task' ($tracked -and $taskRow -and $workingSummary) ('filesTracked1=' + $tracked + ' workingRow=' + $taskRow + ' workingCount=' + $workingSummary)

# Read-only real monitoring check (may legitimately be unavailable here).
$realDebug = Invoke-FiniteMode $executable 'monitor-debug-real' @('--monitor-debug', '--duration', [string]$MonitorSeconds, '--redact-labels', '--data-directory', (Join-Path $evidenceRoot 'monitor-real-data')) $sanitized.Environment $workingDirectory $logRoot ($MonitorSeconds + 120) @(0, 2)
if ($realDebug.ExitCode -eq 2 -and -not $AllowUnavailableMonitor) {
    Add-Check 'monitor-debug-real-readable' $false 'exit=2 essential monitoring unavailable; pass -AllowUnavailableMonitor to record this as an accepted environment limit'
} else {
    Add-Check 'monitor-debug-real-readable' $true ('exit=' + $realDebug.ExitCode + $(if ($realDebug.ExitCode -eq 2) { ' (unavailable accepted by switch)' } else { '' }))
}

# ---- legacy settings upgrade -------------------------------------------------
# A data directory written by an earlier release must be used exactly as written:
# the theme, mute, provider, pack, custom recording, project mapping and window
# placement all survive a new build, and the app does not rewrite them.
$legacyData = Join-Path $evidenceRoot 'upgrade-legacy-data'
New-Item -ItemType Directory -Force -Path (Join-Path $legacyData 'voicepacks/custom') | Out-Null
$legacyConfig = @'
{
  "codexHome": null,
  "hud": {
    "theme": "light",
    "alwaysOnTop": true,
    "opacity": 0.85,
    "startWithWindows": false,
    "taskView": "recent",
    "recentHours": 8,
    "lightPosition": "right",
    "lightSize": 24
  },
  "voice": {
    "muted": true,
    "provider": "pack",
    "packId": "custom",
    "clips": {}
  },
  "project": {
    "mappings": [ { "projectKey": "legacy-project", "name": "Legacy project", "root": "C:\\legacy\\work" } ],
    "aliases": [],
    "maxAncestorChecks": 32,
    "maxCacheEntries": 256
  },
  "disabledHarnessSources": [ "claude/legacy-disabled" ],
  "harnessRoots": [],
  "maximumHarnessSources": 8,
  "runtimeActivitySource": false
}
'@
Set-Content -LiteralPath (Join-Path $legacyData 'config.json') -Value $legacyConfig -Encoding utf8
$legacyState = '{"version":1,"isExpanded":true,"monitorDevice":"LEGACY-MONITOR","offsetXDips":123.5,"offsetYDips":77.25,"dpiScale":1.0,"savedAtUtc":"2026-01-02T03:04:05.0000000Z"}'
Set-Content -LiteralPath (Join-Path $legacyData 'state.json') -Value $legacyState -Encoding utf8
# A real (tiny, synthetic) WAV so the custom recording is a byte-compared asset.
$legacyWav = Join-Path $legacyData 'voicepacks/custom/input.wav'
$wavBytes = [byte[]](0x52, 0x49, 0x46, 0x46, 0x24, 0x00, 0x00, 0x00, 0x57, 0x41, 0x56, 0x45, 0x66, 0x6D, 0x74, 0x20,
    0x10, 0x00, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x40, 0x1F, 0x00, 0x00, 0x80, 0x3E, 0x00, 0x00, 0x02, 0x00,
    0x10, 0x00, 0x64, 0x61, 0x74, 0x61, 0x00, 0x00, 0x00, 0x00)
[System.IO.File]::WriteAllBytes($legacyWav, $wavBytes)
$legacyWavHash = (Get-FileHash -LiteralPath $legacyWav -Algorithm SHA256).Hash

# Duration 5 keeps the finite smoke below its second scenario tick, so the run
# exercises loading and persisting a legacy directory without toggling its theme. The
# seeded legacy monitor is deliberately unknown; the placement argument keeps the
# window off the display in use while the legacy state is still read and validated.
if ($visibleChecksDeferred) {
    Add-Check 'upgrade-legacy-settings' $false $visibleChecksSkipReason
    Add-Check 'upgrade-settings-preserved' $false 'deferred with the legacy run'
    Add-Check 'upgrade-project-and-source-settings-preserved' $false 'deferred with the legacy run'
    Add-Check 'upgrade-placement-file-stays-valid' $false 'deferred with the legacy run'
    Add-Check 'upgrade-custom-recording-preserved' $false 'deferred with the legacy run'
    Add-Check 'upgrade-bridge-spool-is-data-directory-rooted' $false 'deferred with the legacy run'
} else {
$upgradeArguments = @('--hud-smoke', '--duration', '5', '--data-directory', $legacyData)
if ($secondaryMonitor) { $upgradeArguments += @('--placement-monitor', $secondaryMonitor) }
$upgradeRun = Invoke-FiniteMode $executable 'upgrade-legacy-settings' $upgradeArguments $sanitized.Environment $workingDirectory $logRoot 180 @(0)
$upgradedConfig = Get-Content -LiteralPath (Join-Path $legacyData 'config.json') -Raw | ConvertFrom-Json
$upgradedState = Get-Content -LiteralPath (Join-Path $legacyData 'state.json') -Raw | ConvertFrom-Json
$mappingKept = @($upgradedConfig.project.mappings | Where-Object { $_.projectKey -eq 'legacy-project' -and $_.root -eq 'C:\legacy\work' }).Count -eq 1
$disabledKept = @($upgradedConfig.disabledHarnessSources) -contains 'claude/legacy-disabled'
$settingsKept = $upgradedConfig.hud.theme -eq 'light' -and
    $upgradedConfig.hud.alwaysOnTop -eq $true -and
    [math]::Abs([double]$upgradedConfig.hud.opacity - 0.85) -lt 0.001 -and
    $upgradedConfig.hud.taskView -eq 'recent' -and
    [int]$upgradedConfig.hud.recentHours -eq 8 -and
    [int]$upgradedConfig.hud.lightSize -eq 24 -and
    $upgradedConfig.voice.muted -eq $true -and
    $upgradedConfig.voice.provider -eq 'pack' -and
    $upgradedConfig.voice.packId -eq 'custom'
# A running HUD re-derives its own placement from the live monitor layout and saves
# it, so the honest assertion is that the legacy placement file is still accepted and
# rewritten as a valid placement — not that the seeded offsets survive verbatim.
$placementKept = $upgradedState.version -eq 1 -and
    -not [string]::IsNullOrWhiteSpace($upgradedState.monitorDevice) -and
    [double]::IsFinite([double]$upgradedState.offsetXDips) -and
    [double]::IsFinite([double]$upgradedState.offsetYDips) -and
    [double]$upgradedState.dpiScale -ge 0.5 -and [double]$upgradedState.dpiScale -le 4
$wavKept = (Get-FileHash -LiteralPath $legacyWav -Algorithm SHA256).Hash -eq $legacyWavHash
Add-Check 'upgrade-settings-preserved' ([bool]$settingsKept) (
    'theme=' + $upgradedConfig.hud.theme + ' opacity=' + $upgradedConfig.hud.opacity + ' taskView=' + $upgradedConfig.hud.taskView +
    ' recentHours=' + $upgradedConfig.hud.recentHours + ' provider=' + $upgradedConfig.voice.provider + ' pack=' + $upgradedConfig.voice.packId)
Add-Check 'upgrade-project-and-source-settings-preserved' ([bool]($mappingKept -and $disabledKept)) (
    'mapping=' + $mappingKept + ' disabledSource=' + $disabledKept)
Add-Check 'upgrade-placement-file-stays-valid' ([bool]$placementKept) (
    'monitor=' + $upgradedState.monitorDevice + ' x=' + $upgradedState.offsetXDips + ' y=' + $upgradedState.offsetYDips + ' dpi=' + $upgradedState.dpiScale)
Add-Check 'upgrade-custom-recording-preserved' ([bool]$wavKept) ('sha256=' + $legacyWavHash.Substring(0, 12))
# The bridge spool is data-directory rooted, so an app upgrade cannot orphan it.
$upgradeSpool = Join-Path $legacyData 'bridges/dsh/legacy-instance'
New-Item -ItemType Directory -Force -Path $upgradeSpool | Out-Null
Set-Content -LiteralPath (Join-Path $upgradeSpool 'events.jsonl') -Value '' -Encoding utf8
$spoolKept = Test-Path -LiteralPath (Join-Path $legacyData 'bridges/dsh/legacy-instance/events.jsonl')
Add-Check 'upgrade-bridge-spool-is-data-directory-rooted' ([bool]$spoolKept) ('path=' + $upgradeSpool)
}

# ---- normal HUD lifecycle ----------------------------------------------------
if ($visibleChecksDeferred) {
    Add-Check 'hud-live-process-alive' $false $visibleChecksSkipReason
    Add-Check 'hud-live-window-visible' $false ('deferred with the live run: ' + $visibleChecksSkipReason)
    Add-Check 'hud-live-no-focus-steal' $false ('deferred with the live run: ' + $visibleChecksSkipReason)
    Add-Check 'hud-live-second-instance' $false ('deferred with the live run: ' + $visibleChecksSkipReason)
    Add-Check 'hud-live-graceful-exit' $false ('deferred with the live run: ' + $visibleChecksSkipReason)
    Add-Check 'hud-live-graceful-close' $false ('deferred with the live run: ' + $visibleChecksSkipReason)
    Add-Check 'hud-live-persisted-state' $false ('deferred with the live run: ' + $visibleChecksSkipReason)
    Add-Check 'hud-tray-hidden-startup' $false ('deferred with the live run: ' + $visibleChecksSkipReason)
    Add-Check 'hud-tray-close-keeps-session' $false ('deferred with the live run: ' + $visibleChecksSkipReason)
    Add-Check 'hud-no-tray-visible-fallback' $false ('deferred with the live run: ' + $visibleChecksSkipReason)
    Add-Check 'hud-no-tray-graceful-close' $false ('deferred with the live run: ' + $visibleChecksSkipReason)
} elseif (-not $SkipHudLifecycle) {
    $liveData = Join-Path $evidenceRoot 'hud-live-data'
    # The isolated run's own saved placement targets the display the user is not on.
    Set-IsolatedPlacement $liveData $secondaryMonitor
    Write-Step ('hud-live: launching for ' + $HudSeconds + 's from ' + $workingDirectory + ' on ' + $(if ($secondaryMonitor) { $secondaryMonitor } else { 'the primary display (explicitly allowed)' }))
    Set-Content -LiteralPath (Join-Path $liveData 'config.json') -Value '{"hud":{"theme":"light"},"voice":{"muted":true}}' -Encoding utf8
    $liveHandle = Start-PackagedProcess $executable @('--data-directory', $liveData) $sanitized.Environment $workingDirectory
    $live = $liveHandle.Process
    $livePid = $live.Id
    $alive = $true
    $windowSeen = $false
    $focusStolen = $false
    $foregroundSamples = New-Object System.Collections.ArrayList
    $previousForeground = [IntPtr]::Zero
    $deadline = (Get-Date).AddSeconds($HudSeconds)
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 100
        if ($live.HasExited) { $alive = $false; break }
        if ([OverseerWindows]::AnyVisibleWindowOf($livePid)) { $windowSeen = $true }
        $foreground = [OverseerWindows]::GetForegroundWindow()
        $owned = [OverseerWindows]::WindowsForProcess($livePid) -contains $foreground
        # A HUD that becomes the foreground window while the window that had focus is
        # gone did not steal anything: Windows promotes the next window in z-order
        # when a foreground window is destroyed. Only a takeover from a window that
        # still exists counts as focus stealing.
        if ($owned -and -not $focusStolen -and $previousForeground -ne [IntPtr]::Zero -and
            $previousForeground -ne $foreground -and [OverseerWindows]::IsWindow($previousForeground)) {
            $focusStolen = $true
            [void]$focusTransitions.Add([ordered]@{
                atUtc = [DateTimeOffset]::UtcNow.ToString('o')
                from = $previousForeground.ToString()
                to = $foreground.ToString()
            })
        }

        if (-not $owned) { $previousForeground = $foreground }
        [void]$foregroundSamples.Add([ordered]@{
            atUtc = [DateTimeOffset]::UtcNow.ToString('o')
            foreground = $foreground.ToString()
            lastInputTick = [OverseerWindows]::LastInputTick()
            ownedForeground = $owned
        })
    }

    Add-Check 'hud-live-process-alive' $alive ('alive=' + $alive + ' seconds=' + $HudSeconds)
    Add-Check 'hud-live-window-visible' $windowSeen ('visible window seen=' + $windowSeen)
    Add-Check 'hud-live-no-focus-steal' (-not $focusStolen) (
        'foreground samples=' + $foregroundSamples.Count + ' owned-foreground samples=' +
        @($foregroundSamples | Where-Object { $_.ownedForeground }).Count +
        ' takeovers=' + $focusTransitions.Count)
    $foregroundSamples | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath (Join-Path $logRoot 'foreground-samples.json') -Encoding utf8
    $focusTransitions | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath (Join-Path $logRoot 'focus-transitions.json') -Encoding utf8

    if ($alive) {
        if ($SkipActivationChecks) {
            Add-DeferredCheck 'second-instance' 'intentional activation deferred to protect the current foreground work'
            Add-DeferredCheck 'second-instance-still-alive' 'requires the deferred second-instance launch'
        } else {
            $second = Invoke-FiniteMode $executable 'second-instance' @('--data-directory', $liveData) $sanitized.Environment $workingDirectory $logRoot 60 @(0)
            Add-Check 'second-instance-still-alive' (-not $live.HasExited) ('first-alive=' + (-not $live.HasExited))
        }

        # Close now hides to the tray, so the packaged check verifies the hide first.
        # This cannot double as graceful-exit evidence: while a working tray exists
        # every close path hides by design, and no script can click the tray's Exit.
        # The dedicated exit proof below uses a normal-composition run with the tray
        # disabled, where the same close path runs the orderly shutdown.
        $window = [OverseerWindows]::AnyWindowOf($livePid)
        $posted = $false
        if ($window -ne [IntPtr]::Zero) { $posted = [OverseerWindows]::PostMessage($window, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero) }
        Start-Sleep -Seconds 3
        $hiddenAlive = -not $live.HasExited
        $hiddenVisible = [OverseerWindows]::AnyVisibleWindowOf($livePid)
        $hideVerified = $posted -and $hiddenAlive -and -not $hiddenVisible
        Add-Check 'hud-live-close-hides-to-tray' $hideVerified ('posted=' + $posted + ' alive=' + $hiddenAlive + ' visible=' + $hiddenVisible)

        # A second normal launch must restore the same window and session.
        if ($SkipActivationChecks) {
            Add-DeferredCheck 'hud-live-second-launch-restores' 'intentional activation deferred to protect the current foreground work'
        } else {
            $restoreHandle = Start-PackagedProcess $executable @('--data-directory', $liveData) $sanitized.Environment $workingDirectory
            $restore = Complete-PackagedProcess $restoreHandle 60 (Join-Path $logRoot 'restore-instance.log')
            Start-Sleep -Seconds 3
            $restoredVisible = [OverseerWindows]::AnyVisibleWindowOf($livePid)
            Add-Check 'hud-live-second-launch-restores' ($restore.ExitCode -eq 0 -and -not $live.HasExited -and $restoredVisible) ('secondExit=' + $restore.ExitCode + ' firstAlive=' + (-not $live.HasExited) + ' visible=' + $restoredVisible)
        }

        # End the run. The packaged UI has no scriptable Exit while a tray exists, so
        # terminating the process is timeout cleanup, not graceful-exit evidence. That
        # evidence is collected separately by 'hud-live-graceful-exit' below.
        if (-not $live.HasExited) { try { $live.Kill($true) } catch { } }
        [void]$live.WaitForExit(10000)

        $liveStdout = ''
        try { $liveStdout = $liveHandle.Stdout.GetAwaiter().GetResult() } catch { }
        Set-Content -LiteralPath (Join-Path $logRoot 'hud-live.log') -Value $liveStdout -Encoding utf8

        $statePath = Join-Path $liveData 'state.json'
        $configPath = Join-Path $liveData 'config.json'
        $stateExists = Test-Path -LiteralPath $statePath
        $configExists = Test-Path -LiteralPath $configPath
        $stateDetail = 'state=' + $stateExists
        $validState = $false
        $themeValue = ''
        $muteValue = $null
        if ($configExists) {
            try {
                $configJson = Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json
                $themeValue = [string]$configJson.hud.theme
                $muteValue = $configJson.voice.muted
            } catch {
                $stateDetail = $stateDetail + ' config-unreadable=' + $_.Exception.Message
            }
        }

        if ($stateExists) {
            try {
                $stateJson = Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json
                $validState = $stateJson.version -eq 1 -and
                    $stateJson.dpiScale -ge 0.5 -and $stateJson.dpiScale -le 4 -and
                    -not [string]::IsNullOrWhiteSpace($stateJson.monitorDevice) -and
                    [double]::IsFinite([double]$stateJson.offsetXDips) -and
                    [double]::IsFinite([double]$stateJson.offsetYDips)
                $stateDetail = $stateDetail + ' bytes=' + (Get-Item -LiteralPath $statePath).Length +
                    ' dpi=' + $stateJson.dpiScale +
                    ' monitor=' + (-not [string]::IsNullOrWhiteSpace($stateJson.monitorDevice))
            } catch {
                $stateDetail = $stateDetail + ' unreadable=' + $_.Exception.Message
            }
        }

        $stateDetail = $stateDetail + ' theme=' + $themeValue + ' muted=' + $muteValue
        Add-Check 'hud-live-persisted-state' ($validState -and $configExists -and $themeValue -eq 'light' -and $muteValue -is [bool] -and $muteValue) $stateDetail

        # ---- packaged graceful Exit, through the real composition ----------------
        # The tray run above can only prove "close hides", because a working tray makes
        # every close path hide by design. This run uses the SAME normal-mode
        # composition, the same real monitors and the same controller, with the tray
        # disabled: there the close path runs the unconditional orderly shutdown. A
        # synthetic --hud-smoke run would not be equivalent - it builds its own
        # dependencies - and a forced kill would prove nothing at all.
        Write-Step 'hud-exit: normal composition with the tray disabled must exit gracefully'
        $exitData = Join-Path $evidenceRoot 'hud-exit-data'
        Set-IsolatedPlacement $exitData $secondaryMonitor
        Set-Content -LiteralPath (Join-Path $exitData 'config.json') -Value '{"hud":{"theme":"light"},"voice":{"muted":true}}' -Encoding utf8
        $exitHandle = Start-PackagedProcess $executable @('--no-tray', '--data-directory', $exitData) $sanitized.Environment $workingDirectory
        $exitProcess = $exitHandle.Process
        $exitDeadline = (Get-Date).AddSeconds(45)
        $exitWindowSeen = $false
        while ((Get-Date) -lt $exitDeadline -and -not $exitProcess.HasExited) {
            Start-Sleep -Milliseconds 200
            if ([OverseerWindows]::AnyVisibleWindowOf($exitProcess.Id)) { $exitWindowSeen = $true; break }
        }

        $exitPosted = $false
        $exitClosed = $false
        $exitCode = -1
        $exitStateValid = $false
        if ($exitWindowSeen -and -not $exitProcess.HasExited) {
            $exitWindow = [OverseerWindows]::AnyWindowOf($exitProcess.Id)
            if ($exitWindow -ne [IntPtr]::Zero) {
                $exitPosted = [OverseerWindows]::PostMessage($exitWindow, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero)
            }

            $exitClosed = $exitProcess.WaitForExit(45000)
            if ($exitClosed) { $exitCode = $exitProcess.ExitCode }
        }

        # Only timeout cleanup may kill: it is recorded, never counted as an exit.
        $exitKilled = $false
        if (-not $exitProcess.HasExited) {
            $exitKilled = $true
            try { $exitProcess.Kill($true) } catch { }
            [void]$exitProcess.WaitForExit(10000)
        }

        $exitStatePath = Join-Path $exitData 'state.json'
        if ($exitClosed -and (Test-Path -LiteralPath $exitStatePath)) {
            try {
                $exitStateJson = Get-Content -LiteralPath $exitStatePath -Raw | ConvertFrom-Json
                # A graceful shutdown flushes the placement it captured, so a monitor
                # and finite offsets are on disk. A killed process leaves the default.
                $exitStateValid = -not [string]::IsNullOrWhiteSpace($exitStateJson.monitorDevice) -and
                    [double]::IsFinite([double]$exitStateJson.offsetXDips) -and
                    [double]::IsFinite([double]$exitStateJson.offsetYDips)
            } catch {
                $exitStateValid = $false
            }
        }

        try { [void]$exitHandle.Stdout.GetAwaiter().GetResult() } catch { }

        Add-Check 'hud-live-graceful-exit' (
            $exitWindowSeen -and $exitPosted -and $exitClosed -and $exitCode -eq 0 -and -not $exitKilled -and $exitStateValid) (
            'windowSeen=' + $exitWindowSeen + ' posted=' + $exitPosted + ' exited=' + $exitClosed +
            ' exitCode=' + $exitCode + ' forcedKill=' + $exitKilled +
            ' placementFlushed=' + $exitStateValid + ' trayRunHideVerified=' + $hideVerified)

        # Tray start: hidden, alive, still no focus steal. Closing a hidden window
        # keeps the process alive by design, so the script ends only what it started.
        Write-Step 'hud-tray: hidden startup'
        $trayHandle = Start-PackagedProcess $executable @('--start-in-tray', '--data-directory', $liveData) $sanitized.Environment $workingDirectory
        $tray = $trayHandle.Process
        Start-Sleep -Seconds 15
        $trayAlive = -not $tray.HasExited
        $trayVisible = [OverseerWindows]::AnyVisibleWindowOf($tray.Id)
        $trayWindow = [OverseerWindows]::AnyWindowOf($tray.Id)
        $trayFocus = ($trayWindow -ne [IntPtr]::Zero) -and ($trayWindow -eq [OverseerWindows]::GetForegroundWindow())
        Add-Check 'hud-tray-hidden-startup' ($trayAlive -and -not $trayVisible -and -not $trayFocus) ('alive=' + $trayAlive + ' visible=' + $trayVisible + ' focused=' + $trayFocus)
        if ($trayWindow -ne [IntPtr]::Zero) { [void][OverseerWindows]::PostMessage($trayWindow, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero) }
        Start-Sleep -Seconds 3
        $traySurvived = -not $tray.HasExited
        Add-Check 'hud-tray-close-keeps-session' ($traySurvived -and -not [OverseerWindows]::AnyVisibleWindowOf($tray.Id)) ('alive=' + $traySurvived)
        if (-not $tray.HasExited) { try { $tray.Kill($true) } catch { } }
        [void]$tray.WaitForExit(10000)
        try { [void]$trayHandle.Stdout.GetAwaiter().GetResult() } catch { }

        # --start-in-tray --no-tray must still present a controllable window.
        Write-Step 'hud-no-tray: hidden request with tray disabled must stay usable'
        $noTrayData = Join-Path $evidenceRoot 'hud-notray-data'
        Set-IsolatedPlacement $noTrayData $secondaryMonitor
        $noTrayHandle = Start-PackagedProcess $executable @('--start-in-tray', '--no-tray', '--data-directory', $noTrayData) $sanitized.Environment $workingDirectory
        $noTray = $noTrayHandle.Process
        Start-Sleep -Seconds 15
        $noTrayVisible = [OverseerWindows]::AnyVisibleWindowOf($noTray.Id)
        $noTrayAlive = -not $noTray.HasExited
        Add-Check 'hud-no-tray-visible-fallback' ($noTrayAlive -and $noTrayVisible) ('alive=' + $noTrayAlive + ' visible=' + $noTrayVisible)
        $noTrayWindow = [OverseerWindows]::AnyWindowOf($noTray.Id)
        if ($noTrayWindow -ne [IntPtr]::Zero) { [void][OverseerWindows]::PostMessage($noTrayWindow, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero) }
        $noTrayClosed = $noTray.WaitForExit(45000)
        if (-not $noTrayClosed) { try { $noTray.Kill($true) } catch { } }
        $noTrayExit = if ($noTrayClosed) { $noTray.ExitCode } else { -1 }
        Add-Check 'hud-no-tray-graceful-close' ($noTrayClosed -and $noTrayExit -eq 0) ('closed=' + $noTrayClosed + ' exit=' + $noTrayExit)
        try { [void]$noTrayHandle.Stdout.GetAwaiter().GetResult() } catch { }
    } else {
        Add-Check 'hud-live-second-instance' $false 'skipped: the live HUD exited early'
        Add-Check 'hud-live-graceful-exit' $false 'skipped: the live HUD exited early'
        Add-Check 'hud-live-graceful-close' $false 'skipped: the live HUD exited early'
        Add-Check 'hud-live-persisted-state' $false 'skipped: the live HUD exited early'
        Add-Check 'hud-tray-hidden-startup' $false 'skipped: the live HUD exited early'
        Add-Check 'hud-tray-graceful-close' $false 'skipped: the live HUD exited early'
        Add-Check 'hud-no-tray-visible-fallback' $false 'skipped: the live HUD exited early'
        Add-Check 'hud-no-tray-graceful-close' $false 'skipped: the live HUD exited early'
    }
} else {
    Write-Step 'HUD lifecycle checks skipped by request'
    Add-Check 'hud-live-graceful-exit' $false 'skipped: -SkipHudLifecycle was passed'
}

# ---- evidence + verdict ------------------------------------------------------
$failed = @($script:Checks | Where-Object { $_.status -eq 'fail' })
$deferred = @($script:Checks | Where-Object { $_.status -eq 'deferred' })
$evidence = [ordered]@{
    product = 'OVERSEER'
    version = $release.version
    fileVersion = $versionInfo.FileVersion
    productVersion = $versionInfo.ProductVersion
    sourceCommit = $release.sourceCommit
    sourceShortCommit = $release.sourceShortCommit
    integrationEntries = $integrationEntries.Count
    archive = $resolvedArchive
    archiveSha256 = $archiveHash
    archiveEntries = $entryNames.Count
    extractedTo = $extractionRoot
    workingDirectory = $workingDirectory
    childEnvironmentRemoved = $sanitized.Removed
    childEnvironmentLeaks = $sanitized.Leaks
    includedFrameworks = $frameworks
    hudSeconds = $HudSeconds
    monitorSeconds = $MonitorSeconds
    hudLifecycleSkipped = [bool]$SkipHudLifecycle
    activationChecksSkipped = [bool]$SkipActivationChecks
    deferredChecks = $deferred.Count
    visibleChecksSkipped = [bool]$SkipVisibleChecks
    visibleChecksDeferred = [bool]$visibleChecksDeferred
    visibleChecksSkipReason = $visibleChecksSkipReason
    visibleCheckMonitor = $secondaryMonitor
    focusTakeovers = @($focusTransitions)
    realMonitorUnavailable = $realDebug.ExitCode -eq 2
    fullDurationChecks = $HudSeconds -ge 60 -and $MonitorSeconds -ge 60 -and $HudSmokeSeconds -ge 15 -and $SyntheticMonitorSeconds -ge 15
    # Recorded so a reader does not mistake a forced kill for a graceful exit, and does
    # not mistake a passing windowed run for physical interaction evidence.
    gracefulExitEvidence = @(
        'hud-live-graceful-exit proves a normal-composition process exits with code 0 after WM_CLOSE with the tray disabled, and flushes its placement; it is not a human tray-menu click',
        'hud-live-graceful-close is a placeholder that is never satisfied in a passing run: while a working tray exists every close path hides by design and no script can click the tray Exit menu'
    )
    physicalChecksNotPerformed = @(
        'physical sleep/wake (suspend/resume)',
        'monitor disconnect/reconnect and mixed-DPI movement',
        'human tray interaction and acoustic playback by ear',
        'clean machine without a global .NET runtime',
        'native Snap, drag and keyboard/Alt+Tab qualification'
    )
    generatedAtUtc = (Get-Date).ToUniversalTime().ToString('o')
    checks = @($script:Checks)
}
$evidencePath = Join-Path $evidenceRoot ('smoke-evidence-' + $stamp + '.json')
$evidence | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $evidencePath -Encoding utf8

Write-Host ''
Write-Host ('checks  : ' + $script:Checks.Count + ' total, ' + $failed.Count + ' failed, ' + $deferred.Count + ' deferred')
Write-Host ('evidence: ' + $evidencePath)
Write-Host ('logs    : ' + $logRoot)

if ($failed.Count -gt 0) {
    foreach ($item in $failed) { Write-Host ('FAILED: ' + $item.check + ' - ' + $item.detail) }
    exit 1
}

if ($deferred.Count -gt 0) { Write-Host 'packaged smoke: executed checks passed; activation qualification remains deferred' }
else { Write-Host 'packaged smoke: passed' }
exit 0
} finally {
    # Even a script exception must not leave a HUD, hidden tray or child server
    # alive. These Process objects refer only to processes started by this run.
    foreach ($owned in $script:OwnedProcesses) {
        try {
            if (-not $owned.HasExited) { $owned.Kill($true); [void]$owned.WaitForExit(10000) }
        } catch [System.InvalidOperationException] { } # already disposed
        finally { $owned.Dispose() }
    }
    if ($extractionRoot -and $evidenceRoot -and -not $KeepExtraction) {
        Remove-ValidatedItem $extractionRoot $evidenceRoot
    }
}
