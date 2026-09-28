#requires -Version 7.0
# Pure negative controls: no windows, package extraction or user data.
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Test-ReleaseUpgrade.ps1') -FunctionsOnly
$checks = 0
function Assert-Count($differences, [int]$expected, [string]$name) {
    $actual = @(Get-FailingDifferences @($differences)).Count
    if ($actual -ne $expected) { throw "$name expected $expected failures, got $actual" }
    $script:checks++
}
foreach ($mode in @($false,$true)) {
    $saved = [ordered]@{WindowView=$mode;WindowWidth=520;WindowHeight=660;WindowX=123.5;WindowY=77.25;
        WindowMonitor='secondary';WindowMaximized=$true;WindowDpi=1.25;offsetXDips=123.5;offsetYDips=77.25;
        monitorDevice='secondary';dpiScale=1.25;savedAtUtc=[datetime]'2026-01-01T00:00:00Z'}
    $before = Get-JsonLeafMap $saved 'state.json'
    Assert-Count @(Compare-JsonLeafMap $before $before) 0 'identical state'
    foreach ($key in @($saved.Keys | Where-Object {$_ -ne 'savedAtUtc'})) {
        $changed = [ordered]@{}; foreach($name in $saved.Keys){$changed[$name]=$saved[$name]}
        $changed[$key] = if($saved[$key] -is [bool]){!$saved[$key]}elseif($saved[$key] -is [string]){'WRONG'}else{99999}
        Assert-Count @(Compare-JsonLeafMap $before (Get-JsonLeafMap $changed 'state.json')) 1 "changed $key"
        $changed.Remove($key)
        Assert-Count @(Compare-JsonLeafMap $before (Get-JsonLeafMap $changed 'state.json')) 1 "deleted $key"
    }
    $saved.savedAtUtc = $saved.savedAtUtc.AddSeconds(5)
    Assert-Count @(Compare-JsonLeafMap $before (Get-JsonLeafMap $saved 'state.json')) 0 'timestamp advanced'
    $saved.Remove('savedAtUtc')
    Assert-Count @(Compare-JsonLeafMap $before (Get-JsonLeafMap $saved 'state.json')) 1 'timestamp removed'
}
# Seed clamping occurs during the previous run, before the upgrade baseline is
# captured. A legitimate clamp is preserved strictly across the candidate/rollback.
$clamped = Get-JsonLeafMap ([ordered]@{WindowWidth=280;WindowHeight=200;WindowX=0;WindowY=0}) 'state.json'
Assert-Count @(Compare-JsonLeafMap $clamped $clamped) 0 'preserved normalized baseline'
Write-Host "PASS: $checks upgrade comparison controls"
