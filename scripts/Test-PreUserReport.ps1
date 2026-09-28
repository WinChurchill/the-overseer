#requires -Version 7.0
# Exercise only the report classifier; never invoke packages or create windows.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$errors = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile(
    (Join-Path $PSScriptRoot 'Test-PreUserRelease.ps1'), [ref]$null, [ref]$errors)
if ($errors.Count -gt 0) { throw ($errors | Out-String) }
$area = $ast.Find({ param($node)
    $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Area'
}, $true)
if ($null -eq $area) { throw 'Report classifier not found.' }
. ([scriptblock]::Create($area.Extent.Text))

function Expect([string] $expected, [string] $pattern = 'voice', [string[]] $dependencies = @()) {
    $result = Area 6 'Voice' $pattern $dependencies @('Audible qualification remains pending.')
    if ($result.automatedStatus -ne $expected) {
        throw "Expected $expected, got $($result.automatedStatus)"
    }
    if ($result.remainingQualification.Count -ne 1) { throw 'Manual qualification gap was dropped.' }
}
$steps = @()
$smoke = $null
Expect 'fail' # no evidence file
$smoke = [pscustomobject]@{ checks = @([pscustomobject]@{ check = 'voice'; status = 'pass' }) }
Expect 'fail' 'unmatched' # unrelated evidence cannot pass this area
Expect 'pass'
$smoke.checks[0].status = 'deferred'
Expect 'pass-with-deferrals'
$smoke.checks[0].status = 'fail'
Expect 'fail'
$smoke.checks[0].status = 'unknown'
Expect 'fail' # unrecognized result must fail closed
$smoke.checks[0].status = 'pass'
$steps = @([pscustomobject]@{ name = 'build'; status = 'fail' })
Expect 'fail' 'voice' @('build')
Write-Host 'PASS: seven pre-user report controls; qualification gaps retained'
