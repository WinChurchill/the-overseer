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
$apphost = Join-Path $repo 'src/TheOverseer.App/bin/Release/net10.0-windows/win-x64/TheOverseer.App.exe'
if (-not (Test-Path -LiteralPath $apphost)) { throw 'Build Release before running this check.' }
Copy-Item -LiteralPath $apphost -Destination $payload
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
    @{name=$name;arguments=$arguments;monitor=$state.monitorDevice} | ConvertTo-Json -Compress |
        Add-Content -LiteralPath (Join-Path (Split-Path -Parent $workingDirectory) 'mock-launches.jsonl')
    return @{ExitCode=0;TimedOut=$false;Stdout='mocked'}
}
'@

foreach ($case in @(
    @{Name='no-secondary';Monitor='';OptIn=$false;Expected=0},
    @{Name='secondary';Monitor='\\.\DISPLAY2';OptIn=$false;Expected=2},
    @{Name='primary-opt-in';Monitor='';OptIn=$true;Expected=2}
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
    }
    Write-Host ('PASS ' + $case.Name + ': launches=' + $launches.Count + ', exit=' + $code)
}
Write-Host ('Evidence: ' + $root)
