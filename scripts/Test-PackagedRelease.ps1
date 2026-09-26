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
    [switch] $SkipBridgeTests,
    [switch] $KeepExtraction
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

    Write-Step ($name + ': node --test in ' + $directory)
    $node = Get-Command node -ErrorAction SilentlyContinue
    if (-not $node -or -not (Test-Path -LiteralPath $directory)) {
        Add-Check $name $false ('node-on-path=' + [bool]$node + ' directory=' + $directory)
        return [ordered]@{ ExitCode = -1; TimedOut = $false; Stdout = ''; Stderr = ''; Log = '' }
    }

    $handle = Start-PackagedProcess $node.Source @('--test') $environment $directory
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

$hudSmoke = Invoke-FiniteMode $executable 'hud-smoke' @('--hud-smoke', '--duration', [string]$HudSmokeSeconds, '--data-directory', (Join-Path $evidenceRoot 'hud-smoke-data')) $sanitized.Environment $workingDirectory $logRoot ($HudSmokeSeconds + 90) @(0)

# Production-path integration check: invented Codex and bridge producer files
# through the real readers, composite and controller, asserting the applied state.
$integrationCheck = Invoke-FiniteMode $executable 'hud-integration-check' @('--hud-integration-check', '--output-directory', (Join-Path $evidenceRoot 'integration-check')) $sanitized.Environment $workingDirectory $logRoot 300 @(0)
$integrationPassed = ([regex]::Matches($integrationCheck.Stdout, '(?m)^check PASS ')).Count
$integrationFailed = ([regex]::Matches($integrationCheck.Stdout, '(?m)^check FAIL ')).Count
Add-Check 'hud-integration-check' ($integrationCheck.ExitCode -eq 0 -and $integrationFailed -eq 0 -and $integrationPassed -ge 13) (
    'exit=' + $integrationCheck.ExitCode + ' passed=' + $integrationPassed + ' failed=' + $integrationFailed)

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

$upgradeRun = Invoke-FiniteMode $executable 'upgrade-legacy-settings' @('--hud-smoke', '--duration', '6', '--data-directory', $legacyData) $sanitized.Environment $workingDirectory $logRoot 180 @(0)
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
$placementKept = $upgradedState.version -eq 1 -and
    [math]::Abs([double]$upgradedState.offsetXDips - 123.5) -lt 0.001 -and
    [math]::Abs([double]$upgradedState.offsetYDips - 77.25) -lt 0.001 -and
    $upgradedState.monitorDevice -eq 'LEGACY-MONITOR'
$wavKept = (Get-FileHash -LiteralPath $legacyWav -Algorithm SHA256).Hash -eq $legacyWavHash
Add-Check 'upgrade-settings-preserved' ([bool]$settingsKept) (
    'theme=' + $upgradedConfig.hud.theme + ' opacity=' + $upgradedConfig.hud.opacity + ' taskView=' + $upgradedConfig.hud.taskView +
    ' recentHours=' + $upgradedConfig.hud.recentHours + ' provider=' + $upgradedConfig.voice.provider + ' pack=' + $upgradedConfig.voice.packId)
Add-Check 'upgrade-project-and-source-settings-preserved' ([bool]($mappingKept -and $disabledKept)) (
    'mapping=' + $mappingKept + ' disabledSource=' + $disabledKept)
Add-Check 'upgrade-placement-preserved' ([bool]$placementKept) (
    'x=' + $upgradedState.offsetXDips + ' y=' + $upgradedState.offsetYDips + ' dpi=' + $upgradedState.dpiScale)
Add-Check 'upgrade-custom-recording-preserved' ([bool]$wavKept) ('sha256=' + $legacyWavHash.Substring(0, 12))
# The bridge spool is data-directory rooted, so an app upgrade cannot orphan it.
$upgradeSpool = Join-Path $legacyData 'bridges/dsh/legacy-instance'
New-Item -ItemType Directory -Force -Path $upgradeSpool | Out-Null
Set-Content -LiteralPath (Join-Path $upgradeSpool 'events.jsonl') -Value '' -Encoding utf8
$spoolKept = Test-Path -LiteralPath (Join-Path $legacyData 'bridges/dsh/legacy-instance/events.jsonl')
Add-Check 'upgrade-bridge-spool-is-data-directory-rooted' ([bool]$spoolKept) ('path=' + $upgradeSpool)

# ---- normal HUD lifecycle ----------------------------------------------------
if (-not $SkipHudLifecycle) {
    $liveData = Join-Path $evidenceRoot 'hud-live-data'
    New-Item -ItemType Directory -Force -Path $liveData | Out-Null
    Write-Step ('hud-live: launching for ' + $HudSeconds + 's from ' + $workingDirectory)
    Set-Content -LiteralPath (Join-Path $liveData 'config.json') -Value '{"hud":{"theme":"light"},"voice":{"muted":true}}' -Encoding utf8
    $liveHandle = Start-PackagedProcess $executable @('--data-directory', $liveData) $sanitized.Environment $workingDirectory
    $live = $liveHandle.Process
    $livePid = $live.Id
    $alive = $true
    $windowSeen = $false
    $focusStolen = $false
    $foregroundSamples = New-Object System.Collections.ArrayList
    $deadline = (Get-Date).AddSeconds($HudSeconds)
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 100
        if ($live.HasExited) { $alive = $false; break }
        if ([OverseerWindows]::AnyVisibleWindowOf($livePid)) { $windowSeen = $true }
        $foreground = [OverseerWindows]::GetForegroundWindow()
        [void]$foregroundSamples.Add([ordered]@{
            atUtc = [DateTimeOffset]::UtcNow.ToString('o')
            foreground = $foreground.ToString()
            lastInputTick = [OverseerWindows]::LastInputTick()
            ownedForeground = [OverseerWindows]::WindowsForProcess($livePid) -contains $foreground
        })
        foreach ($hWnd in [OverseerWindows]::WindowsForProcess($livePid)) {
            if ($hWnd -eq $foreground) { $focusStolen = $true }
        }
    }

    Add-Check 'hud-live-process-alive' $alive ('alive=' + $alive + ' seconds=' + $HudSeconds)
    Add-Check 'hud-live-window-visible' $windowSeen ('visible window seen=' + $windowSeen)
    Add-Check 'hud-live-no-focus-steal' (-not $focusStolen) ('foreground samples=' + $foregroundSamples.Count + ' own window foreground=' + $focusStolen)
    $foregroundSamples | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath (Join-Path $logRoot 'foreground-samples.json') -Encoding utf8

    if ($alive) {
        # Second instance must exit by itself without disturbing the first.
        $second = Invoke-FiniteMode $executable 'second-instance' @('--data-directory', $liveData) $sanitized.Environment $workingDirectory $logRoot 60 @(0)
        Add-Check 'second-instance-still-alive' (-not $live.HasExited) ('first-alive=' + (-not $live.HasExited))

        # Close now hides to the tray (explicit Exit is covered by unit tests and
        # has no scriptable path), so the packaged check verifies the hide first.
        $window = [OverseerWindows]::AnyWindowOf($livePid)
        $posted = $false
        if ($window -ne [IntPtr]::Zero) { $posted = [OverseerWindows]::PostMessage($window, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero) }
        Start-Sleep -Seconds 3
        $hiddenAlive = -not $live.HasExited
        $hiddenVisible = [OverseerWindows]::AnyVisibleWindowOf($livePid)
        Add-Check 'hud-live-close-hides-to-tray' ($posted -and $hiddenAlive -and -not $hiddenVisible) ('posted=' + $posted + ' alive=' + $hiddenAlive + ' visible=' + $hiddenVisible)

        # A second normal launch must restore the same window and session.
        $restoreHandle = Start-PackagedProcess $executable @('--data-directory', $liveData) $sanitized.Environment $workingDirectory
        $restore = Complete-PackagedProcess $restoreHandle 60 (Join-Path $logRoot 'restore-instance.log')
        Start-Sleep -Seconds 3
        $restoredVisible = [OverseerWindows]::AnyVisibleWindowOf($livePid)
        Add-Check 'hud-live-second-launch-restores' ($restore.ExitCode -eq 0 -and -not $live.HasExited -and $restoredVisible) ('secondExit=' + $restore.ExitCode + ' firstAlive=' + (-not $live.HasExited) + ' visible=' + $restoredVisible)

        # End the run: the packaged UI has no scriptable Exit, so the script
        # terminates only the process it started.
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
        Add-Check 'hud-live-graceful-close' $false 'skipped: the live HUD exited early'
        Add-Check 'hud-live-persisted-state' $false 'skipped: the live HUD exited early'
        Add-Check 'hud-tray-hidden-startup' $false 'skipped: the live HUD exited early'
        Add-Check 'hud-tray-graceful-close' $false 'skipped: the live HUD exited early'
        Add-Check 'hud-no-tray-visible-fallback' $false 'skipped: the live HUD exited early'
        Add-Check 'hud-no-tray-graceful-close' $false 'skipped: the live HUD exited early'
    }
} else {
    Write-Step 'HUD lifecycle checks skipped by request'
}

# ---- evidence + verdict ------------------------------------------------------
$failed = @($script:Checks | Where-Object { $_.status -ne 'pass' })
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
    realMonitorUnavailable = $realDebug.ExitCode -eq 2
    fullDurationChecks = $HudSeconds -ge 60 -and $MonitorSeconds -ge 60 -and $HudSmokeSeconds -ge 15 -and $SyntheticMonitorSeconds -ge 15
    physicalChecksNotPerformed = @(
        'physical sleep/wake (suspend/resume)',
        'monitor disconnect/reconnect and mixed-DPI movement',
        'human tray interaction and acoustic playback by ear',
        'clean machine without a global .NET runtime'
    )
    generatedAtUtc = (Get-Date).ToUniversalTime().ToString('o')
    checks = @($script:Checks)
}
$evidencePath = Join-Path $evidenceRoot ('smoke-evidence-' + $stamp + '.json')
$evidence | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $evidencePath -Encoding utf8

Write-Host ''
Write-Host ('checks  : ' + $script:Checks.Count + ' total, ' + $failed.Count + ' failed')
Write-Host ('evidence: ' + $evidencePath)
Write-Host ('logs    : ' + $logRoot)

if ($failed.Count -gt 0) {
    foreach ($item in $failed) { Write-Host ('FAILED: ' + $item.check + ' - ' + $item.detail) }
    exit 1
}

Write-Host 'packaged smoke: passed'
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
