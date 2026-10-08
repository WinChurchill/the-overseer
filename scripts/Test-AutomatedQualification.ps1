#requires -Version 7.0
<#
.SYNOPSIS
    Qualifies exact private CI bytes using existing suites and a hidden mixed-source soak.
.DESCRIPTION
    Creates unique evidence and synthetic data. No live install, profile, registry
    entry or source session is changed. Run bridge suites sequentially. Native UI
    and actual-host qualification remain separately identified owner checks.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $CandidateArchivePath,
    [Parameter(Mandatory)][string] $BuildManifestPath,
    [Parameter(Mandatory)][string] $PreviousArchivePath,
    [Parameter(Mandatory)][string] $CiRunUrl,
    [string] $EvidenceDirectory = 'artifacts/prepublication/automation-round',
    [ValidateRange(75,3600)][int] $EnduranceSeconds = 3600,
    [switch] $AllowVisibleChecksOnPrimary
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
function Full([string] $path) { if ([IO.Path]::IsPathRooted($path)) { return [IO.Path]::GetFullPath($path) }; return [IO.Path]::GetFullPath((Join-Path $repo $path)) }
$archive = Full $CandidateArchivePath
$manifest = Get-Content -LiteralPath (Full $BuildManifestPath) -Raw | ConvertFrom-Json
$sha = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash
if ($manifest.archiveSha256 -ne $sha -or $manifest.archiveBytes -ne (Get-Item -LiteralPath $archive).Length -or $manifest.sourceTreeDirty) { throw 'Archive identity mismatch or dirty source' }
$root = Join-Path (Full $EvidenceDirectory) ('run-' + [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssZ') + '-' + [Guid]::NewGuid().ToString('N').Substring(0,8))
[void](New-Item -ItemType Directory -Path $root)
$steps = [Collections.Generic.List[object]]::new()
$pwsh = (Get-Process -Id $PID).Path
function Run([string] $name, [string] $file, [string[]] $arguments, [int] $timeout = 1800) {
    $start = [Diagnostics.ProcessStartInfo]::new($file)
    $start.WorkingDirectory = $repo
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($argument in $arguments) { [void]$start.ArgumentList.Add($argument) }
    $child = [Diagnostics.Process]::new(); $child.StartInfo = $start
    $code = -1; $detail = ''; $forced = $false
    $clock = [Diagnostics.Stopwatch]::StartNew()
    $stdout = $null; $stderr = $null
    try {
        [void]$child.Start()
        $stdout = $child.StandardOutput.ReadToEndAsync(); $stderr = $child.StandardError.ReadToEndAsync()
        if (-not $child.WaitForExit($timeout * 1000)) { $forced = $true; $child.Kill($true); [void]$child.WaitForExit(10000); throw 'Owned test process timed out' }
        $code = $child.ExitCode
    } catch { $detail = $_.Exception.Message }
    finally {
        $text = if ($null -ne $stdout) { $stdout.GetAwaiter().GetResult() } else { '' }
        $errors = if ($null -ne $stderr) { $stderr.GetAwaiter().GetResult() } else { '' }
        ($text + "`n" + $errors + "`n" + $detail) | Set-Content -LiteralPath (Join-Path $root ($name + '.log')) -Encoding utf8
        $child.Dispose()
    }
    $steps.Add([ordered]@{ name=$name; status=$(if ($code -eq 0 -and -not $detail) {'pass'} else {'fail'}); exitCode=$code; forcedKill=$forced; seconds=$clock.Elapsed.TotalSeconds; detail=$detail })
    Write-Host "$name : $($steps[$steps.Count-1].status)"
}
Write-Host "Qualification evidence: $root"
$identity = [ordered]@{ version=$manifest.version; sha256=$sha; sourceCommit=$manifest.sourceCommit; archive=$archive; ciRun=$CiRunUrl; os=[Environment]::OSVersion.VersionString; machineClass='existing development host, not a clean VM'; generatedAtUtc=[DateTime]::UtcNow.ToString('o') }
$identity | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $root 'identity.json') -Encoding utf8
$preArguments = @('-NoProfile','-File',(Join-Path $PSScriptRoot 'Test-PreUserRelease.ps1'),'-CandidateArchivePath',$archive,'-PreviousArchivePath',(Full $PreviousArchivePath),'-EvidenceDirectory',(Join-Path $root 'pre-user'))
if ($AllowVisibleChecksOnPrimary) { $preArguments += '-AllowVisibleChecksOnPrimary' }
Run 'pre-user' $pwsh $preArguments
$extraction = Join-Path $root 'exact CI bytes 空间'
Expand-Archive -LiteralPath $archive -DestinationPath $extraction
$release = Get-Content -LiteralPath (Join-Path $extraction 'release.json') -Raw | ConvertFrom-Json
if ($release.sourceCommit -ne $manifest.sourceCommit -or $release.version -ne $manifest.version) { throw 'Embedded source/version differs from manifest' }
Run 'endurance' (Join-Path $extraction 'TheOverseer.App.exe') @('--hud-endurance-check','--start-in-tray','--no-tray','--duration',[string]$EnduranceSeconds,'--output-directory',(Join-Path $root 'soak')) ($EnduranceSeconds + 180)
$reports = @(Get-ChildItem -LiteralPath (Join-Path $root 'soak') -Filter endurance-report.json -Recurse)
$soak = if ($reports.Count -eq 1) { Get-Content -LiteralPath $reports[0].FullName -Raw | ConvertFrom-Json } else { $null }
$failed = @($steps | Where-Object status -eq 'fail').Count
if ($null -eq $soak -or $soak.failed -ne 0) { $failed++ }
$result = [ordered]@{ identity=$identity; steps=@($steps.ToArray()); endurance=$soak; automatedStatus=$(if($failed){'fail'}else{'pass-with-qualification-gaps'}); qualification='pending actual-host, native UI, clean-machine, physical/audio and owner publication checks'; fullHour=$EnduranceSeconds -eq 3600 }
$result | ConvertTo-Json -Depth 15 | Set-Content -LiteralPath (Join-Path $root 'qualification-report.json') -Encoding utf8
Write-Host "Result: $($result.automatedStatus); report: $root/qualification-report.json"
if ($failed) { exit 1 }
