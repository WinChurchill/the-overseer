#requires -Version 5.1
<#
.SYNOPSIS
    Clean-machine owner check: hash, extract into a fresh spaced/Unicode path,
    launch the ordinary app and record whether the owner can Exit it normally.
.DESCRIPTION
    Copy this script and the CI ZIP to a clean Windows standard-user environment.
    Download through the browser first. Keep Windows protections enabled. Do not
    install SDK/.NET/Node before the first app launch. This script never clicks a
    prompt, changes security, deletes user data or edits harness profiles.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string] $ArchivePath,
    [Parameter(Mandatory=$true)][ValidatePattern('^[A-Fa-f0-9]{64}$')][string] $ExpectedSha256,
    [string] $EvidenceDirectory = (Join-Path $env:USERPROFILE 'Downloads/OVERSEER clean qualification'),
    [ValidateRange(30,900)][int] $ExitTimeoutSeconds = 300
)
$ErrorActionPreference = 'Stop'
$sha = (Get-FileHash -LiteralPath $ArchivePath -Algorithm SHA256).Hash
if ($sha -ne $ExpectedSha256) { throw 'ZIP hash differs from the approved CI archive' }
$root = Join-Path $EvidenceDirectory ('check-' + [Guid]::NewGuid().ToString('N'))
$install = Join-Path $root 'OVERSEER 空间'; $data = Join-Path $root 'empty data 空间'
[void](New-Item -ItemType Directory -Path $root)
$tools = @('dotnet','node','git') | ForEach-Object { [pscustomobject]@{name=$_; present=[bool](Get-Command $_ -ErrorAction SilentlyContinue)} }
Expand-Archive -LiteralPath $ArchivePath -DestinationPath $install
$release = Get-Content -LiteralPath (Join-Path $install 'release.json') -Raw | ConvertFrom-Json
$start = New-Object Diagnostics.ProcessStartInfo
$start.FileName = Join-Path $install 'TheOverseer.App.exe'
$start.Arguments = '--data-directory "' + $data + '" --codex-home "' + (Join-Path $root 'empty codex') + '"'
$start.UseShellExecute = $false; $start.WorkingDirectory = $root
# The self-contained app must succeed without an SDK lookup or Node PATH.
$start.EnvironmentVariables.Remove('DOTNET_ROOT'); $start.EnvironmentVariables['DOTNET_MULTILEVEL_LOOKUP']='0'
$child = New-Object Diagnostics.Process; $child.StartInfo = $start
$clock = [Diagnostics.Stopwatch]::StartNew(); $code = -1; $forced = $false
try {
    [void]$child.Start()
    Write-Host 'Check empty setup, save a setting, reopen the app, and use its explicit Exit menu. X normally hides to tray.'
    if (-not $child.WaitForExit($ExitTimeoutSeconds * 1000)) { $forced=$true; $child.Kill(); $child.WaitForExit(); throw 'Timed out; Exit is not qualified' }
    $code = $child.ExitCode
} finally {
    $report = @{ version=$release.version; sourceCommit=$release.sourceCommit; sha256=$sha; os=[Environment]::OSVersion.VersionString; tools=$tools; seconds=$clock.Elapsed.TotalSeconds; exitCode=$code; forcedKill=$forced; stateFlushed=(Test-Path -LiteralPath (Join-Path $data 'state.json')); machineQualification='Owner must attest standard user, clean OS, browser acquisition and no prerequisites; PATH inventory alone is insufficient.' }
    $report | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $root 'clean-machine-report.json') -Encoding UTF8
    $child.Dispose()
}
if ($code -ne 0 -or $forced -or -not $report.stateFlushed) { throw 'Clean-machine lifecycle failed' }
