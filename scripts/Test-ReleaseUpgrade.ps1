#requires -Version 7.0
<#
.SYNOPSIS
    Compares an older OVERSEER package with a candidate package on copies of one
    data directory, exercises the documented rollback boundary, and records what
    actually survives the upgrade.

.DESCRIPTION
    Extracts both archives, seeds one isolated data directory with a complete
    previous release's settings (every monitor root, limit, logging field, HUD
    setting, window mode, voice provider/endpoint/event override/clip assignment,
    project mapping and alias, disabled harness source and saved placement, plus a
    custom recording, a populated bridge spool and an integration hook record),
    runs the previous build, copies the resulting data directory, runs the
    candidate on the copy, runs the PREVIOUS build again on a copy of what the
    candidate wrote (the rollback boundary), and compares every JSON leaf of
    config.json/state.json and of the spool documents by exact JSON path.

    Nothing outside the evidence directory is read or written: the user's real
    data directory, desktop shortcut and HKCU Run entry are never touched. The
    shortcut is a copy inside the evidence directory and the startup entry is a
    file-backed fake registry store that is driven through the production
    TheOverseer.App.Desktop.StartupRegistration helper.

    The comparison is old-to-new: a field the previous build already dropped is
    reported as such instead of being blamed on the candidate.

    A difference is a failure unless it is either (a) an addition the candidate
    makes, or (b) covered by an explicit normalization justification recorded in
    the evidence JSON. Nothing is silently ignored.

.EXAMPLE
    pwsh -NoProfile -File scripts/Test-ReleaseUpgrade.ps1 `
        -PreviousArchivePath artifacts/monitor-lifetime-release/OVERSEER-v1-win-x64.zip `
        -CandidateArchivePath artifacts/rc1/OVERSEER-1.0.0-rc.1-win-x64.zip

.EXAMPLE
    # Loads only the pure comparison helpers so they can be exercised directly:
    pwsh -NoProfile -Command ". scripts/Test-ReleaseUpgrade.ps1 -FunctionsOnly"
#>
[CmdletBinding(DefaultParameterSetName = 'Compare')]
param(
    [Parameter(Mandatory = $true, ParameterSetName = 'Compare')][string] $PreviousArchivePath,
    [Parameter(Mandatory = $true, ParameterSetName = 'Compare')][string] $CandidateArchivePath,
    [string] $EvidenceDirectory = 'artifacts/upgrade',
    [ValidateRange(2, 600)][int] $DurationSeconds = 5,
    [switch] $KeepExtraction,
    [switch] $WindowView,
    # Both runs open a HUD window. It goes to a non-primary display so the comparison
    # never appears on the display the user is working on; on a single-display machine
    # the windowed comparison is deferred with an explicit gap unless this is passed.
    [switch] $AllowVisibleChecksOnPrimary,
    # Loads the comparison helpers and returns without touching any package, window,
    # display or data directory. Because the script is then dot-sourced, the helper
    # functions (and this script's parameter variables) land in the caller's scope.
    [Parameter(Mandatory = $true, ParameterSetName = 'Helpers')][switch] $FunctionsOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$script:Checks = New-Object System.Collections.ArrayList
$script:OwnedProcesses = New-Object System.Collections.ArrayList

# Leaves whose value legitimately cannot be carried across two runs of a HUD. Every
# entry is reported verbatim in the evidence JSON; a difference covered here is still
# listed as a difference, it is only classified as explained instead of failing.
# Only the save timestamp is volatile. Persisted placement is user state.
$script:NormalizationJustifications = @(
    [ordered]@{ path = 'state.json.savedatutc'; justification = 'Valid save timestamps may advance after an orderly placement write.' }
)
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

function Test-UnderRoot([string] $path, [string] $root) {
    $full = [System.IO.Path]::GetFullPath($path)
    $rootFull = [System.IO.Path]::GetFullPath($root).TrimEnd('\', '/') + [System.IO.Path]::DirectorySeparatorChar
    return $full.StartsWith($rootFull, [System.StringComparison]::OrdinalIgnoreCase)
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

# ---------------------------------------------------------------------------
# Semantic JSON comparison. Every document is flattened to one leaf per scalar,
# keyed by a case-insensitive canonical path, so two documents can be compared
# generically instead of against a hand-listed key set. Property-name casing is
# not a difference: MonitorConfigStore and HudStateStore both deserialize with
# PropertyNameCaseInsensitive = true, so "HarnessId" and "harnessId" are the same
# setting. Array order is significant and every element is compared.
# ---------------------------------------------------------------------------
function Add-JsonLeaf {
    param(
        [System.Collections.IDictionary] $Map,
        [string] $Canonical,
        [string] $Display,
        [object] $Value
    )

    $Map[$Canonical] = [ordered]@{ path = $Display; value = $Value }
}

function Add-JsonNode {
    param(
        [object] $Node,
        [string] $Display,
        [string] $Canonical,
        [System.Collections.IDictionary] $Map
    )

    if ($null -eq $Node) {
        Add-JsonLeaf $Map $Canonical $Display $null
        return
    }

    if ($Node -is [System.Collections.IDictionary]) {
        $keys = @($Node.Keys | Sort-Object -Property { [string]$_ })
        if ($keys.Count -eq 0) { Add-JsonLeaf $Map $Canonical $Display '<empty-object>'; return }
        foreach ($key in $keys) {
            $name = [string]$key
            Add-JsonNode -Node $Node[$key] -Display ($Display + '.' + $name) -Canonical ($Canonical + '.' + $name.ToLowerInvariant()) -Map $Map
        }

        return
    }

    if ($Node -is [System.Management.Automation.PSCustomObject]) {
        $properties = @($Node.PSObject.Properties | Where-Object { $_.MemberType -in @('NoteProperty', 'Property') } | Sort-Object -Property Name)
        if ($properties.Count -eq 0) { Add-JsonLeaf $Map $Canonical $Display '<empty-object>'; return }
        foreach ($property in $properties) {
            Add-JsonNode -Node $property.Value -Display ($Display + '.' + $property.Name) -Canonical ($Canonical + '.' + $property.Name.ToLowerInvariant()) -Map $Map
        }

        return
    }

    if (($Node -is [System.Collections.IEnumerable]) -and ($Node -isnot [string])) {
        $items = @($Node)
        Add-JsonLeaf $Map ($Canonical + '.length') ($Display + '.length') $items.Count
        for ($index = 0; $index -lt $items.Count; $index++) {
            Add-JsonNode -Node $items[$index] -Display ($Display + '[' + $index + ']') -Canonical ($Canonical + '[' + $index + ']') -Map $Map
        }

        return
    }

    Add-JsonLeaf $Map $Canonical $Display $Node
}

function Get-JsonLeafMap {
    param([object] $Node, [string] $Label)

    $map = [ordered]@{}
    Add-JsonNode -Node $Node -Display $Label -Canonical $Label.ToLowerInvariant() -Map $map
    return $map
}

function Test-JsonLeafEqual {
    param([object] $Left, [object] $Right)

    $leftNull = $null -eq $Left
    $rightNull = $null -eq $Right
    if ($leftNull -or $rightNull) { return ($leftNull -and $rightNull) }

    if ($Left -is [datetime] -or $Right -is [datetime]) {
        if (($Left -isnot [datetime]) -or ($Right -isnot [datetime])) { return $false }
        return ([datetime]$Left).ToUniversalTime().Equals(([datetime]$Right).ToUniversalTime())
    }

    $leftNumber = ($Left -is [ValueType]) -and ($Left -isnot [bool]) -and ($Left -isnot [char])
    $rightNumber = ($Right -is [ValueType]) -and ($Right -isnot [bool]) -and ($Right -isnot [char])
    if ($leftNumber -and $rightNumber) {
        # Compare numbers in their widest common form. Exact equality: the JSON
        # round trip of one literal must not look like a change.
        if ($Left -is [double] -or $Right -is [double]) { return ([double]$Left -eq [double]$Right) }
        if ($Left -is [decimal] -or $Right -is [decimal]) { return ([decimal]$Left -eq [decimal]$Right) }
        return ([long]$Left -eq [long]$Right)
    }

    if ($leftNumber -or $rightNumber) { return $false }
    if (($Left -is [bool]) -ne ($Right -is [bool])) { return $false }
    if ($Left -is [bool]) { return ([bool]$Left -eq [bool]$Right) }
    return [string]::Equals([string]$Left, [string]$Right, [System.StringComparison]::Ordinal)
}

# A difference is reported with its exact JSON path, its before/after values and
# whether it fails the run.
function Compare-JsonLeafMap {
    param(
        [System.Collections.IDictionary] $Before,
        [System.Collections.IDictionary] $After,
        [object[]] $ExtraJustifications = @()
    )

    $differences = New-Object System.Collections.ArrayList
    foreach ($key in $Before.Keys) {
        $left = $Before[$key]
        if (-not $After.Contains($key)) {
            [void]$differences.Add((New-Difference $key $left.path $left.value $null 'missing-after' $ExtraJustifications))
            continue
        }

        $right = $After[$key]
        if (-not (Test-JsonLeafEqual $left.value $right.value)) {
            [void]$differences.Add((New-Difference $key $left.path $left.value $right.value 'changed' $ExtraJustifications))
        }
    }

    foreach ($key in $After.Keys) {
        if ($Before.Contains($key)) { continue }
        $right = $After[$key]
        [void]$differences.Add((New-Difference $key $right.path $null $right.value 'added-after' $ExtraJustifications))
    }

    return @($differences)
}

function Get-NormalizationJustification {
    param([string] $CanonicalPath, [object[]] $ExtraJustifications = @())

    foreach ($entry in @($script:NormalizationJustifications) + @($ExtraJustifications)) {
        if ($null -eq $entry) { continue }
        if ($CanonicalPath -eq $entry.path -or $CanonicalPath.StartsWith($entry.path, [System.StringComparison]::Ordinal)) {
            return [string]$entry.justification
        }
    }

    return $null
}

function New-Difference {
    param(
        [string] $CanonicalPath,
        [string] $DisplayPath,
        [object] $Before,
        [object] $After,
        [string] $Kind,
        [object[]] $ExtraJustifications = @()
    )

    $justification = $null
    $disposition = 'fails'
    if ($Kind -eq 'added-after') {
        $disposition = 'additive'
        $justification = 'The previous data did not contain this path; an upgrade may add a setting, so an addition is reported but does not fail.'
    } elseif ($Kind -eq 'changed' -and $CanonicalPath -eq 'state.json.savedatutc' -and
        $Before -is [datetime] -and $After -is [datetime]) {
        $justification = Get-NormalizationJustification $CanonicalPath $ExtraJustifications
        if ($justification) { $disposition = 'explained' }
    }

    return [ordered]@{
        path = $DisplayPath
        canonicalPath = $CanonicalPath
        before = Format-ComparableValue $Before
        after = Format-ComparableValue $After
        kind = $Kind
        disposition = $disposition
        justification = $justification
    }
}

function Format-ComparableValue {
    param([object] $Value)

    if ($null -eq $Value) { return $null }
    if ($Value -is [datetime]) { return ([datetime]$Value).ToUniversalTime().ToString('o') }
    return $Value
}

function Get-FailingDifferences {
    param([object[]] $Differences = @())
    return @($Differences | Where-Object { $_.disposition -eq 'fails' })
}

function Format-DifferenceList {
    param([object[]] $Differences = @(), [int] $Limit = 8)

    $items = @($Differences | Select-Object -First $Limit | ForEach-Object {
        $_.path + ' (' + $_.kind + '): [' + [string]$_.before + '] -> [' + [string]$_.after + ']'
    })
    $text = ($items -join '; ')
    if (@($Differences).Count -gt $Limit) { $text += '; ... ' + (@($Differences).Count - $Limit) + ' more' }
    return $text
}

function Get-DocumentLeafMap {
    param([string] $Directory, [string] $Name)

    $path = Join-Path $Directory $Name
    if (-not (Test-Path -LiteralPath $path)) { return $null }
    $text = Get-Content -LiteralPath $path -Raw
    if ([string]::IsNullOrWhiteSpace($text)) { return $null }
    return Get-JsonLeafMap ($text | ConvertFrom-Json) $Name
}

# Reads one leaf without throwing when the document (or the leaf) is absent.
function Get-LeafValue {
    param([System.Collections.IDictionary] $Map, [string] $CanonicalPath)

    if ($null -eq $Map) { return $null }
    if (-not $Map.Contains($CanonicalPath)) { return $null }
    return $Map[$CanonicalPath].value
}

function Merge-JsonLeafMap {
    param([object[]] $Maps = @())

    $merged = [ordered]@{}
    foreach ($map in $Maps) {
        if ($null -eq $map) { continue }
        foreach ($key in $map.Keys) { $merged[$key] = $map[$key] }
    }

    return $merged
}

# Full semantic comparison of the two settings documents of a data directory.
function Compare-DataState {
    param(
        [string] $BeforeDirectory,
        [string] $AfterDirectory,
        [object[]] $ExtraJustifications = @()
    )

    $documents = @('config.json', 'state.json')
    $comparedPaths = New-Object System.Collections.ArrayList
    $differences = New-Object System.Collections.ArrayList
    $unreadable = New-Object System.Collections.ArrayList

    foreach ($name in $documents) {
        $before = $null
        $after = $null
        $beforeError = ''
        $afterError = ''
        try { $before = Get-DocumentLeafMap $BeforeDirectory $name }
        catch { $beforeError = $_.Exception.Message }
        try { $after = Get-DocumentLeafMap $AfterDirectory $name }
        catch { $afterError = $_.Exception.Message }

        foreach ($pair in @(@($beforeError, $BeforeDirectory), @($afterError, $AfterDirectory))) {
            if (-not $pair[0]) { continue }
            $message = $name + ' in ' + $pair[1] + ': ' + $pair[0]
            [void]$unreadable.Add($message)
            [void]$differences.Add([ordered]@{
                path = $name
                canonicalPath = $name.ToLowerInvariant()
                before = $(if ($pair[1] -eq $BeforeDirectory) { 'unreadable' } else { 'not read' })
                after = $message
                kind = 'unreadable'
                disposition = 'fails'
                justification = $null
            })
        }

        if ($beforeError -or $afterError) { continue }

        if ($null -eq $before -and $null -eq $after) { continue }
        if ($null -eq $after) {
            [void]$differences.Add([ordered]@{
                path = $name
                canonicalPath = $name.ToLowerInvariant()
                before = 'present'
                after = 'missing'
                kind = 'document-missing'
                disposition = 'fails'
                justification = $null
            })
            foreach ($key in $before.Keys) { [void]$comparedPaths.Add($before[$key].path) }
            continue
        }

        if ($null -eq $before) {
            [void]$differences.Add([ordered]@{
                path = $name
                canonicalPath = $name.ToLowerInvariant()
                before = 'missing'
                after = 'present'
                kind = 'document-added'
                disposition = 'additive'
                justification = 'The before directory had no such document; an upgrade may create it, so it does not fail.'
            })
            foreach ($key in $after.Keys) { [void]$comparedPaths.Add($after[$key].path) }
            continue
        }

        foreach ($key in (@($before.Keys) + @($after.Keys) | Sort-Object -Unique)) {
            if ($before.Contains($key)) { [void]$comparedPaths.Add($before[$key].path) }
            elseif ($after.Contains($key)) { [void]$comparedPaths.Add($after[$key].path) }
        }

        foreach ($difference in (Compare-JsonLeafMap $before $after $ExtraJustifications)) { [void]$differences.Add($difference) }
    }

    return [ordered]@{
        documents = $documents
        comparedPaths = @($comparedPaths)
        differences = @($differences)
        failures = @(Get-FailingDifferences @($differences))
        unreadable = @($unreadable)
    }
}

# One leaf per file: presence for the inventory, sha256 for the recordings.
function Get-FileInventoryLeafMap {
    param([string] $Directory, [string] $Label, [string[]] $ExcludeRelativePrefixes = @())

    $map = [ordered]@{}
    if (-not (Test-Path -LiteralPath $Directory)) { return $map }
    $rootFull = [System.IO.Path]::GetFullPath($Directory).TrimEnd('\', '/') + [System.IO.Path]::DirectorySeparatorChar
    foreach ($file in @(Get-ChildItem -LiteralPath $Directory -Recurse -File -Force)) {
        $relative = $file.FullName.Substring($rootFull.Length).Replace('\', '/')
        $excluded = $false
        foreach ($prefix in $ExcludeRelativePrefixes) {
            if ($relative.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase)) { $excluded = $true; break }
        }

        if ($excluded) { continue }
        $map[($Label + '/' + $relative).ToLowerInvariant()] = [ordered]@{ path = ($Label + '/' + $relative); value = 'present' }
    }

    return $map
}

function Get-FileHashLeafMap {
    param([string] $Directory, [string] $Label)

    $map = [ordered]@{}
    if (-not (Test-Path -LiteralPath $Directory)) { return $map }
    $rootFull = [System.IO.Path]::GetFullPath($Directory).TrimEnd('\', '/') + [System.IO.Path]::DirectorySeparatorChar
    foreach ($file in @(Get-ChildItem -LiteralPath $Directory -Recurse -File -Force)) {
        $relative = $file.FullName.Substring($rootFull.Length).Replace('\', '/')
        $map[($Label + '/' + $relative).ToLowerInvariant()] = [ordered]@{
            path = ($Label + '/' + $relative)
            value = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
        }
    }

    return $map
}

# ---------------------------------------------------------------------------
# Bridge spool: the shipped producers write events.jsonl (one JSON record per
# line), an atomically replaced current.json and a health.json. The upgrade must
# leave the records and the current-state document semantically intact, not
# merely present.
# ---------------------------------------------------------------------------
function Get-SpoolSnapshot {
    param([string] $SpoolDirectory)

    $snapshot = [ordered]@{
        directory = $SpoolDirectory
        present = (Test-Path -LiteralPath $SpoolDirectory)
        recordCount = 0
        parseErrors = @()
        records = [ordered]@{}
        current = [ordered]@{}
        health = [ordered]@{}
    }

    if (-not $snapshot.present) { return $snapshot }
    $eventsPath = Join-Path $SpoolDirectory 'events.jsonl'
    if (Test-Path -LiteralPath $eventsPath) {
        $lines = @(Get-Content -LiteralPath $eventsPath | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
        $snapshot.recordCount = $lines.Count
        $documents = New-Object System.Collections.ArrayList
        $index = 0
        foreach ($line in $lines) {
            try { [void]$documents.Add(($line | ConvertFrom-Json)) }
            catch { $snapshot.parseErrors += ('events.jsonl[' + $index + ']: ' + $_.Exception.Message) }
            $index++
        }

        $snapshot['records'] = Get-JsonLeafMap $documents.ToArray() 'events.jsonl'
    }

    foreach ($pair in @(@('current', 'current.json'), @('health', 'health.json'))) {
        $path = Join-Path $SpoolDirectory $pair[1]
        if (-not (Test-Path -LiteralPath $path)) { continue }
        try {
            $text = Get-Content -LiteralPath $path -Raw
            $snapshot[$pair[0]] = Get-JsonLeafMap ($text | ConvertFrom-Json) $pair[1]
        } catch {
            $snapshot.parseErrors += ($pair[1] + ': ' + $_.Exception.Message)
        }
    }

    return $snapshot
}

function Compare-SpoolDocument {
    param(
        [System.Collections.IDictionary] $Before,
        [System.Collections.IDictionary] $After
    )

    if ($null -eq $Before) { $Before = [ordered]@{} }
    if ($null -eq $After) { $After = [ordered]@{} }
    $differences = @(Compare-JsonLeafMap $Before $After)
    return [ordered]@{
        differences = $differences
        failures = @(Get-FailingDifferences $differences)
    }
}

function Get-DataStateSnapshot {
    param([string] $DataDirectory)

    return [ordered]@{
        config = $(try { Get-DocumentLeafMap $DataDirectory 'config.json' } catch { $null })
        state = $(try { Get-DocumentLeafMap $DataDirectory 'state.json' } catch { $null })
    }
}

function Get-DataStatePlain {
    param([System.Collections.IDictionary] $Snapshot)

    $plain = [ordered]@{}
    foreach ($name in @('config', 'state')) {
        $map = $Snapshot[$name]
        if ($null -eq $map) { $plain[$name] = $null; continue }
        $values = [ordered]@{}
        foreach ($key in $map.Keys) { $values[$map[$key].path] = Format-ComparableValue $map[$key].value }
        $plain[$name] = $values
    }

    return $plain
}

function Get-ChecksNotPerformed {
    param([System.Collections.IDictionary] $State)

    $list = New-Object System.Collections.ArrayList
    [void]$list.Add('real desktop shortcut: the user shortcut on the Desktop is never created, read, modified or deleted; a copied .lnk inside the evidence directory is used instead')
    [void]$list.Add('real HKCU\Software\Microsoft\Windows\CurrentVersion\Run entry: never read or written; a file-backed fake registry store inside the evidence directory is driven through the production StartupRegistration helper instead')
    [void]$list.Add('HUD rendering, focus, tray interaction, keyboard/mouse behaviour and audible voice playback are not judged (both runs are --hud-smoke only)')
    [void]$list.Add('in-place upgrade over an installed directory: every archive is extracted into its own directory, so nothing is overwritten in place')
    [void]$list.Add('a data directory that is moved to a different absolute path: this harness copies it, so absolute paths recorded inside producer files (for example health.json.directory) keep pointing at the copy they were written in')
    [void]$list.Add('live bridge rebinding on a real host: a stable spool proves the data survived, it does not prove a real Claude Code or dsh hook still fires after the upgrade')
    if (-not $State.RollbackPerformed) { [void]$list.Add('rollback run: the previous build was not started again against candidate-written data') }
    if (-not $State.SpoolCompared) { [void]$list.Add('populated bridge spool comparison (events.jsonl records and current.json semantics)') }
    if (-not $State.ShortcutChecked) { [void]$list.Add('shortcut and startup-target checks across versioned directories') }
    if (-not $State.IntegrationChecked) { [void]$list.Add('integration-recorded executable reference check') }
    if (-not $State.RestoreChecked) { [void]$list.Add('pre-update backup/restore round trip of the data directory') }
    return @($list)
}

function Resolve-DotNetHost {
    $candidates = New-Object System.Collections.ArrayList
    if ($env:DOTNET_ROOT) { [void]$candidates.Add((Join-Path $env:DOTNET_ROOT 'dotnet.exe')) }
    $command = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($command) { [void]$candidates.Add([string]$command.Source) }
    foreach ($candidate in $candidates) {
        if ($candidate -and (Test-Path -LiteralPath $candidate)) { return [string]$candidate }
    }

    return ''
}

# ---------------------------------------------------------------------------
# The startup target is built by the real production helper. A throwaway console
# project under the evidence directory references the candidate package's
# TheOverseer.App.dll and drives StartupRegistration against a fake, file-backed
# registry store. The registry store shape is mine; the helper is the product's.
# ---------------------------------------------------------------------------
$script:StartupProbeCsproj = @'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0-windows</TargetFramework>
    <UseWPF>true</UseWPF>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <SelfContained>false</SelfContained>
    <AssemblyName>startup-probe</AssemblyName>
    <RootNamespace>StartupProbe</RootNamespace>
    <EnableDefaultCompileItems>true</EnableDefaultCompileItems>
    <NoWarn>$(NoWarn);CS1591</NoWarn>
  </PropertyGroup>
  <ItemGroup>
    <Reference Include="TheOverseer.App">
      <HintPath>$(ProbePackageDirectory)/TheOverseer.App.dll</HintPath>
      <Private>true</Private>
    </Reference>
  </ItemGroup>
</Project>
'@

$script:StartupProbeProgram = @'
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using TheOverseer.App.Desktop;

internal sealed class FileStartupRegistry : IStartupRegistry
{
    private readonly string _path;

    public FileStartupRegistry(string path) => _path = path;

    public string? Read(string valueName)
    {
        var values = ReadAll();
        return values.TryGetValue(valueName, out var value) ? value : null;
    }

    public void Write(string valueName, string value)
    {
        var values = ReadAll();
        values[valueName] = value;
        WriteAll(values);
    }

    public void Delete(string valueName)
    {
        var values = ReadAll();
        if (values.Remove(valueName)) WriteAll(values);
    }

    private Dictionary<string, string> ReadAll()
    {
        if (!File.Exists(_path)) return new Dictionary<string, string>(StringComparer.Ordinal);
        var text = File.ReadAllText(_path);
        if (string.IsNullOrWhiteSpace(text)) return new Dictionary<string, string>(StringComparer.Ordinal);
        return JsonSerializer.Deserialize<Dictionary<string, string>>(text) ?? new Dictionary<string, string>(StringComparer.Ordinal);
    }

    private void WriteAll(Dictionary<string, string> values)
    {
        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        File.WriteAllText(_path, JsonSerializer.Serialize(values, new JsonSerializerOptions { WriteIndented = true }));
    }
}

internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length < 4)
        {
            Console.Error.WriteLine("usage: startup-probe <previousExe> <candidateExe> <fakeRegistryJson> <outputJson>");
            return 2;
        }

        var previousExe = args[0];
        var candidateExe = args[1];
        var registryPath = args[2];
        var outputPath = args[3];

        var store = new FileStartupRegistry(registryPath);
        var previous = new StartupRegistration(store, previousExe);
        var candidate = new StartupRegistration(store, candidateExe);

        // The user opts in while the previous version is installed.
        var optIn = previous.Set(true);
        var registeredForPrevious = store.Read(StartupRegistration.ValueName);

        // The candidate now lives in a different versioned directory.
        var readBeforeRepair = candidate.Read();
        var recordedBeforeRepair = store.Read(StartupRegistration.ValueName);

        var repair = candidate.Set(true);
        var recordedAfterRepair = store.Read(StartupRegistration.ValueName);
        var readAfterRepair = candidate.Read();

        var candidateCommand = StartupRegistration.BuildCommand(candidateExe);
        var document = new Dictionary<string, object?>
        {
            ["valueName"] = StartupRegistration.ValueName,
            ["startHiddenFlag"] = StartupRegistration.StartHiddenFlag,
            ["helperAssembly"] = typeof(StartupRegistration).Assembly.Location,
            ["helperAssemblyVersion"] = typeof(StartupRegistration).Assembly.GetName().Version?.ToString(),
            ["previousExecutable"] = previousExe,
            ["candidateExecutable"] = candidateExe,
            ["previousExecutableUsable"] = StartupRegistration.IsUsableExecutablePath(previousExe),
            ["candidateExecutableUsable"] = StartupRegistration.IsUsableExecutablePath(candidateExe),
            ["dotnetExecutableRejected"] = !StartupRegistration.IsUsableExecutablePath(@"C:\Program Files\dotnet\dotnet.exe"),
            ["previousCommand"] = StartupRegistration.BuildCommand(previousExe),
            ["candidateCommand"] = candidateCommand,
            ["commandStartsWithQuotedCandidatePath"] = candidateCommand.StartsWith("\"" + candidateExe + "\"", StringComparison.Ordinal),
            ["commandCarriesStartHiddenFlag"] = candidateCommand.EndsWith(" " + StartupRegistration.StartHiddenFlag, StringComparison.Ordinal),
            ["optInState"] = optIn.State.ToString(),
            ["optInReasonCode"] = optIn.ReasonCode,
            ["registeredCommandForPrevious"] = registeredForPrevious,
            ["readForCandidateBeforeRepairState"] = readBeforeRepair.State.ToString(),
            ["readForCandidateBeforeRepairReason"] = readBeforeRepair.ReasonCode,
            ["recordedCommandBeforeRepair"] = recordedBeforeRepair,
            ["optInStillRegisteredAfterMove"] = !string.IsNullOrWhiteSpace(recordedBeforeRepair),
            ["recordedCommandStillPointsAtPrevious"] = recordedBeforeRepair is not null && recordedBeforeRepair.Contains(previousExe, StringComparison.OrdinalIgnoreCase),
            ["repairState"] = repair.State.ToString(),
            ["repairReasonCode"] = repair.ReasonCode,
            ["readForCandidateAfterRepairState"] = readAfterRepair.State.ToString(),
            ["registeredCommandAfterRepair"] = recordedAfterRepair,
            ["repairCommandMatchesCandidateCommand"] = string.Equals(recordedAfterRepair, candidateCommand, StringComparison.OrdinalIgnoreCase),
        };

        File.WriteAllText(outputPath, JsonSerializer.Serialize(document, new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }
}
'@

function Invoke-StartupHelperProbe {
    param(
        [string] $EvidenceRoot,
        [string] $CandidateRoot,
        [string] $PreviousExecutable,
        [string] $CandidateExecutable,
        [string] $LogDirectory
    )

    $result = [ordered]@{
        performed = $false
        ok = $false
        error = ''
        buildLogPath = ''
        runLogPath = ''
        document = $null
        registryPath = ''
        executablePath = ''
    }

    $dotnet = Resolve-DotNetHost
    if (-not $dotnet) {
        $result.error = 'no dotnet host was found (DOTNET_ROOT or PATH); the production StartupRegistration helper could not be exercised'
        return $result
    }

    $probeRoot = Join-Path $EvidenceRoot 'startup-probe'
    New-Item -ItemType Directory -Force -Path $probeRoot | Out-Null
    Set-Content -LiteralPath (Join-Path $probeRoot 'startup-probe.csproj') -Value $script:StartupProbeCsproj -Encoding utf8
    Set-Content -LiteralPath (Join-Path $probeRoot 'Program.cs') -Value $script:StartupProbeProgram -Encoding utf8
    $registryPath = Join-Path $probeRoot 'fake-registry.json'
    $outputPath = Join-Path $probeRoot 'startup-probe.json'
    $result.registryPath = $registryPath
    $result.buildLogPath = Join-Path $LogDirectory 'startup-probe-build.log'
    $result.runLogPath = Join-Path $LogDirectory 'startup-probe-run.log'

    $buildArguments = @(
        'build', $probeRoot, '-c', 'Release', '--nologo', '-m:1',
        '-p:UseSharedCompilation=false',
        ('-p:ProbePackageDirectory=' + $CandidateRoot)
    )
    Write-Step ('startup helper probe build: ' + $dotnet + ' ' + ($buildArguments -join ' '))
    $buildOutput = ''
    try {
        $buildOutput = (& $dotnet @buildArguments 2>&1 | Out-String)
    } catch {
        $buildOutput = [string]$_
    }

    Set-Content -LiteralPath $result.buildLogPath -Value $buildOutput -Encoding utf8
    $result.executablePath = Join-Path $probeRoot 'bin/Release/net10.0-windows/startup-probe.dll'
    if (-not (Test-Path -LiteralPath $result.executablePath)) {
        $result.error = 'the startup helper probe did not build ' + $result.executablePath + '; see ' + $result.buildLogPath
        return $result
    }

    $runArguments = @($result.executablePath, $PreviousExecutable, $CandidateExecutable, $registryPath, $outputPath)
    Write-Step ('startup helper probe run: ' + ($runArguments -join ' '))
    $runOutput = ''
    try {
        $runOutput = (& $dotnet @runArguments 2>&1 | Out-String)
    } catch {
        $runOutput = [string]$_
    }

    Set-Content -LiteralPath $result.runLogPath -Value $runOutput -Encoding utf8
    $result.performed = $true
    if (-not (Test-Path -LiteralPath $outputPath)) {
        $result.error = 'the startup helper probe did not produce ' + $outputPath + '; see ' + $result.runLogPath
        return $result
    }

    try {
        $result.document = Get-Content -LiteralPath $outputPath -Raw | ConvertFrom-Json
        $result.ok = $true
    } catch {
        $result.error = 'the startup helper probe output could not be read: ' + $_.Exception.Message
    }

    return $result
}

if ($FunctionsOnly) {
    Write-Host 'comparison helpers loaded; no package, window or user data was touched'
    return
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
        $evidencePath = Join-Path $evidenceRoot ('upgrade-evidence-' + $stamp + '.json')
        [ordered]@{
            product = 'OVERSEER'
            check = 'release-upgrade'
            previousArchive = $previousArchive
            candidateArchive = $candidateArchive
            visibleChecksDeferred = $true
            visibleCheckMonitor = ''
            deferredReason = 'no non-primary display was found and -AllowVisibleChecksOnPrimary was not passed'
            comparedPaths = @()
            normalizationJustifications = @($script:NormalizationJustifications)
            settingsAfterPrevious = $null
            settingsAfterCandidate = $null
            settingsAfterRollback = $null
            differences = @()
            rollback = [ordered]@{ performed = $false; reason = 'deferred before any package was extracted' }
            physicalChecksNotPerformed = @(
                'desktop shortcut target rewrite (only a copy inside the evidence directory is ever touched)',
                'Windows startup entry rewrite (the real HKCU Run value is never read or written)',
                'rollback from candidate to previous release',
                'populated bridge spool comparison',
                'production StartupRegistration helper probe'
            )
            checksNotPerformed = @(Get-ChecksNotPerformed @{ RollbackPerformed = $false; SpoolCompared = $false; ShortcutChecked = $false; IntegrationChecked = $false; RestoreChecked = $false })
            generatedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
            checks = @($script:Checks)
        } | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $evidencePath -Encoding utf8
        Write-Host ('evidence: ' + $evidencePath)
        exit 1
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

    # Isolated legacy data: a previous release's complete settings, on a copy we own.
    $legacyData = Join-Path $evidenceRoot 'legacy-data'
    New-Item -ItemType Directory -Force -Path (Join-Path $legacyData 'voicepacks/custom') | Out-Null
    $legacyConfig = @'
{
  "codexHome": "C:/legacy/codex-home",
  "additionalRoots": [ "C:/legacy/roots/alpha", "C:/legacy/roots/beta", "C:/legacy/roots/gamma" ],
  "limits": {
    "maxTrackedFiles": 48,
    "perFileBytesPerPass": 131072,
    "globalBytesPerPass": 2097152,
    "staleLeaseMinutes": 11,
    "discoveryEntriesPerSlice": 1024,
    "maxTitleCacheEntries": 256
  },
  "logging": { "enabled": false, "maxBytes": 524288, "level": "debug" },
  "runtimeActivitySource": false,
  "hud": {
    "theme": "light",
    "alwaysOnTop": true,
    "opacity": 0.85,
    "startWithWindows": false,
    "taskView": "recent",
    "recentHours": 8,
    "lightPosition": "left",
    "lightSize": 24
  },
  "voice": {
    "muted": true,
    "provider": "tts",
    "packId": "custom",
    "endpoint": "https://voice.example.invalid/v1/speak",
    "timeoutSeconds": 9.5,
    "events": {
      "permission_required": true,
      "input_required": false,
      "task_error": true,
      "task_complete": false,
      "all_tasks_complete": true,
      "monitoring_lost": true,
      "monitoring_restored": false,
      "still_waiting": true,
      "system_online": true
    },
    "clips": {
      "permission_required": "permission-1.wav",
      "input_required": "input.wav",
      "task_error": "error.wav",
      "task_complete": "complete.wav",
      "still_waiting": "wait.wav"
    }
  },
  "project": {
    "mappings": [
      { "projectKey": "legacy-project", "name": "Legacy project", "root": "C:/legacy/work" },
      { "projectKey": "legacy-second", "name": "Legacy second", "root": "C:/legacy/second" },
      { "projectKey": "legacy-third", "name": "Legacy third", "root": "C:/legacy/third" }
    ],
    "aliases": [
      { "alias": "\\\\wsl$\\Ubuntu\\srv\\legacy", "canonical": "C:/legacy/wsl" },
      { "alias": "/srv/legacy", "canonical": "C:/legacy/posix" },
      { "alias": "D:/alias/legacy", "canonical": "D:/legacy/real" }
    ],
    "maxAncestorChecks": 24,
    "maxCacheEntries": 256
  },
  "disabledHarnessSources": [ "claude/legacy-disabled", "dsh/legacy-dsh", "claude/legacy-disabled-2" ],
  "harnessRoots": [
    { "harnessId": "claude", "path": "C:/legacy/spools/claude" },
    { "harnessId": "dsh", "path": "C:/legacy/spools/dsh" },
    { "harnessId": "claude", "path": "C:/legacy/spools/claude-extra" }
  ],
  "maximumHarnessSources": 5
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
        WindowView = [bool]$WindowView
        WindowPinned = $true
        WindowMaximized = $false
        WindowMonitor = $legacyMonitor
        WindowX = 123.5
        WindowY = 77.25
        WindowWidth = 520
        WindowHeight = 660
        WindowDpi = 1.0
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
    foreach ($clip in @('input.wav', 'permission-1.wav', 'error.wav', 'complete.wav', 'wait.wav')) {
        [System.IO.File]::WriteAllBytes((Join-Path $legacyData ('voicepacks/custom/' + $clip)), $wavBytes)
    }

    $legacyRecordings = Get-FileHashLeafMap (Join-Path $legacyData 'voicepacks/custom') 'voicepacks/custom'

    # A pre-existing bridge spool in the data directory with real producer content:
    # one JSON record per line in events.jsonl plus the atomically published
    # current-state document and the writer health document.
    $legacySpool = Join-Path $legacyData 'bridges/dsh/legacy-instance'
    New-Item -ItemType Directory -Force -Path $legacySpool | Out-Null
    $spoolRecords = @(
        '{"schemaVersion":1,"harnessId":"dsh","sourceInstanceId":"legacy-instance","sourceEpoch":"dsh-legacy-epoch-1","sessionId":"legacy-parent","sequence":0,"eventId":"dsh:legacy-parent:0","observedAtUtc":"2026-01-02T03:00:00.040Z","eventAtUtc":"2026-01-02T03:00:00.000Z","kind":"session/start","turnId":null,"callId":null,"requestId":null,"projectRoot":"C:\\\\legacy\\\\work","category":"unknown","evidence":"explicit","outcome":null,"hydration":false,"producerVersion":"dsh-observer/1.0.0"}'
        '{"schemaVersion":1,"harnessId":"dsh","sourceInstanceId":"legacy-instance","sourceEpoch":"dsh-legacy-epoch-1","sessionId":"legacy-parent","sequence":1,"eventId":"dsh:legacy-parent:1","observedAtUtc":"2026-01-02T03:00:00.070Z","eventAtUtc":"2026-01-02T03:00:00.010Z","kind":"turn/start","turnId":"dsh:legacy-parent:turn:1","callId":null,"requestId":null,"projectRoot":"C:\\\\legacy\\\\work","category":"working","evidence":"explicit","outcome":null,"hydration":false,"producerVersion":"dsh-observer/1.0.0"}'
        '{"schemaVersion":1,"harnessId":"dsh","sourceInstanceId":"legacy-instance","sourceEpoch":"dsh-legacy-epoch-1","sessionId":"legacy-parent","sequence":2,"eventId":"dsh:legacy-parent:2","observedAtUtc":"2026-01-02T03:00:00.100Z","eventAtUtc":"2026-01-02T03:00:00.020Z","kind":"tool/start","turnId":"dsh:legacy-parent:turn:1","callId":"call-legacy-1","requestId":null,"projectRoot":"C:\\\\legacy\\\\work","category":"working","evidence":"explicit","outcome":null,"hydration":false,"producerVersion":"dsh-observer/1.0.0"}'
        '{"schemaVersion":1,"harnessId":"dsh","sourceInstanceId":"legacy-instance","sourceEpoch":"dsh-legacy-epoch-1","sessionId":"legacy-parent","sequence":3,"eventId":"dsh:legacy-parent:3","observedAtUtc":"2026-01-02T03:00:00.130Z","eventAtUtc":"2026-01-02T03:00:00.030Z","kind":"approval/asked","turnId":null,"callId":"call-legacy-1","requestId":"approval-legacy-1","projectRoot":"C:\\\\legacy\\\\work","category":"waiting","evidence":"explicit","outcome":null,"hydration":false,"producerVersion":"dsh-observer/1.0.0"}'
        '{"schemaVersion":1,"harnessId":"dsh","sourceInstanceId":"legacy-instance","sourceEpoch":"dsh-legacy-epoch-1","sessionId":"legacy-parent","sequence":4,"eventId":"dsh:legacy-parent:4","observedAtUtc":"2026-01-02T03:00:00.160Z","eventAtUtc":"2026-01-02T03:00:00.040Z","kind":"turn/end","turnId":"dsh:legacy-parent:turn:1","callId":null,"requestId":null,"projectRoot":"C:\\\\legacy\\\\work","category":"complete","evidence":"explicit","outcome":"completed","hydration":false,"producerVersion":"dsh-observer/1.0.0"}'
        '{"schemaVersion":1,"harnessId":"dsh","sourceInstanceId":"legacy-instance","sourceEpoch":"dsh-legacy-epoch-1","sessionId":null,"sequence":5,"eventId":"dsh:*:5","observedAtUtc":"2026-01-02T03:00:00.190Z","eventAtUtc":null,"kind":"heartbeat","turnId":null,"callId":null,"requestId":null,"projectRoot":null,"category":"idle","evidence":"inferred","outcome":null,"hydration":false,"producerVersion":"dsh-observer/1.0.0"}'
    )
    Set-Content -LiteralPath (Join-Path $legacySpool 'events.jsonl') -Value $spoolRecords -Encoding utf8
    $spoolCurrent = @'
{"schemaVersion":1,"harnessId":"dsh","sourceInstanceId":"legacy-instance","sourceEpoch":"dsh-legacy-epoch-1","producerVersion":"dsh-observer/1.0.0","updatedAtUtc":"2026-01-02T03:00:00.190Z","evictedSessions":0,"sessions":[{"sessionId":"legacy-parent","parentSessionId":null,"projectRoot":"C:\\legacy\\work","createdAtUtc":"2026-01-02T03:00:00.000Z","firstSeenUtc":"2026-01-02T03:00:00.010Z","lastObservedUtc":"2026-01-02T03:00:00.190Z","openTurnId":null,"planMode":false,"pendingApprovalRequestId":null,"category":"idle","disposed":false,"lastSequence":4,"lastHostSeq":5,"lastKind":"turn/end","lastEventId":"dsh:legacy-parent:4","records":5},{"sessionId":"legacy-child","parentSessionId":"legacy-parent","projectRoot":"C:\\legacy\\work","createdAtUtc":"2026-01-02T03:00:00.050Z","firstSeenUtc":"2026-01-02T03:00:00.060Z","lastObservedUtc":"2026-01-02T03:00:00.120Z","openTurnId":"dsh:legacy-child:turn:1","planMode":true,"pendingApprovalRequestId":"approval-legacy-2","category":"waiting","disposed":false,"lastSequence":2,"lastHostSeq":2,"lastKind":"approval/asked","lastEventId":"dsh:legacy-child:2","records":3}]}
'@
    Set-Content -LiteralPath (Join-Path $legacySpool 'current.json') -Value $spoolCurrent -Encoding utf8
    $spoolHealth = [ordered]@{
        version = 1
        lastWrite = '2026-01-02T03:00:00.190Z'
        queueDepth = 0
        dropped = 0
        counters = [ordered]@{
            enqueued = 6
            written = 6
            bytesWritten = 4096
            droppedOldest = 0
            droppedOversize = 0
            droppedError = 0
            droppedClosed = 0
            writeErrors = 0
            unavailable = 0
            rotations = 0
            pruned = 0
            expired = 0
            currentWrites = 2
            healthWrites = 1
        }
        sourceInstanceId = 'legacy-instance'
        sourceEpoch = 'dsh-legacy-epoch-1'
        producerVersion = 'dsh-observer/1.0.0'
        startedAtUtc = '2026-01-02T02:59:59.000Z'
        harnessId = 'dsh'
        directory = $legacySpool
    }
    $spoolHealth | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $legacySpool 'health.json') -Encoding utf8
    $legacySpoolSnapshot = Get-SpoolSnapshot $legacySpool

    # The integration's own record, outside the data directory exactly like the real
    # one (the shipped claude-code installer writes an absolute bridge path into the
    # user's hook settings). A copy inside the evidence directory is used; the user's
    # real settings file is never read or written.
    $integrationDirectory = Join-Path $evidenceRoot 'integration'
    New-Item -ItemType Directory -Force -Path $integrationDirectory | Out-Null
    $bridgeRelativePath = 'integrations/claude-code/bridge.mjs'
    $bridgeMarker = 'overseer-claude-bridge'
    $previousBridge = Join-Path $previousRoot ($bridgeRelativePath -replace '/', '\')
    $candidateBridge = Join-Path $candidateRoot ($bridgeRelativePath -replace '/', '\')
    $integrationSettingsPath = Join-Path $integrationDirectory 'claude-settings.json'
    [ordered]@{
        hooks = [ordered]@{
            PreToolUse = @(
                [ordered]@{
                    hooks = @(
                        [ordered]@{
                            type = 'command'
                            command = 'node'
                            args = @($previousBridge, $bridgeMarker)
                            timeout = 10
                        }
                    )
                }
            )
        }
    } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $integrationSettingsPath -Encoding utf8
    $integrationSettingsHash = (Get-FileHash -LiteralPath $integrationSettingsPath -Algorithm SHA256).Hash
    $recordedHandler = (Get-Content -LiteralPath $integrationSettingsPath -Raw | ConvertFrom-Json).hooks.PreToolUse[0].hooks[0]
    $recordedArguments = @($recordedHandler.args)
    $recordedExecutable = if ($recordedArguments.Count -gt 0) { [string]$recordedArguments[0] } else { '' }
    $recordedReferenceResolves = $recordedExecutable.Length -gt 0 -and (Test-Path -LiteralPath $recordedExecutable)
    $recordedReferencePointsAtPrevious = $recordedExecutable.StartsWith($previousRoot, [System.StringComparison]::OrdinalIgnoreCase)
    $candidateShipsRecordedRelativePath = Test-Path -LiteralPath $candidateBridge
    $recordedReferenceFollowsPackageMove = $recordedExecutable.StartsWith($candidateRoot, [System.StringComparison]::OrdinalIgnoreCase)
    Add-Check 'integration-recorded-executable-reference-resolves' (
        $recordedArguments.Count -eq 2 -and $recordedArguments[1] -eq $bridgeMarker -and $recordedReferenceResolves -and $candidateShipsRecordedRelativePath) (
        'recorded=' + $recordedExecutable + ' resolves=' + $recordedReferenceResolves + ' pointsAtPreviousPackage=' + $recordedReferencePointsAtPrevious +
        ' candidateShipsSameRelativePath=' + $candidateShipsRecordedRelativePath + ' recordedPathFollowsPackageMove=' + $recordedReferenceFollowsPackageMove)

    $environment = New-SanitizedEnvironment
    $workingDirectory = Join-Path $evidenceRoot 'cwd'
    New-Item -ItemType Directory -Force -Path $workingDirectory | Out-Null

    $legacyState = Get-DataStateSnapshot $legacyData
    # Placed once the harness knows whether the previous build kept the seeded monitor.
    $conditionalJustifications = New-Object System.Collections.ArrayList

    Write-Step 'running the previous build against the legacy data directory'
    # The previous build is never given an argument it may not know: placement comes
    # from the saved state seeded above, which every release understands.
    $previousRun = Invoke-PackagedRun $previousExe 'previous-run' @('--hud-smoke', '--duration', [string]$DurationSeconds, '--data-directory', $legacyData) $environment $workingDirectory $logRoot ($DurationSeconds + 120)
    Add-Check 'previous-build-runs' ($previousRun.ExitCode -eq 0) ('exit=' + $previousRun.ExitCode)
    $afterPrevious = Get-DataStateSnapshot $legacyData
    # What the previous build itself did to the seeded settings. It rewrites the whole
    # normalized document when it saves, so the comparison is seeded-leaves vs what the
    # previous build left behind, not a directory compared with itself.
    $lostByPreviousDifferences = @(
        (Compare-JsonLeafMap (Merge-JsonLeafMap @($legacyState.config)) (Merge-JsonLeafMap @($afterPrevious.config)) @($conditionalJustifications))) +
        @(Compare-JsonLeafMap (Merge-JsonLeafMap @($legacyState.state)) (Merge-JsonLeafMap @($afterPrevious.state)) @($conditionalJustifications))
    $lostByPrevious = [ordered]@{
        documents = @('config.json', 'state.json')
        comparedPaths = @((@($legacyState.config.Keys) + @($afterPrevious.config.Keys)) | Sort-Object -Unique)
        differences = $lostByPreviousDifferences
        failures = @(Get-FailingDifferences $lostByPreviousDifferences)
    }

    # The pre-update backup the operator is told to take before upgrading.
    $backupData = Join-Path $evidenceRoot 'backup-data'
    Copy-Item -LiteralPath $legacyData -Destination $backupData -Recurse -Force

    # The candidate runs on a COPY of what the previous build left behind, so the
    # comparison never depends on the user's own data directory.
    $candidateData = Join-Path $evidenceRoot 'candidate-data'
    Copy-Item -LiteralPath $legacyData -Destination $candidateData -Recurse -Force
    Write-Step 'running the candidate build against the copied data directory'
    $candidateArguments = @('--hud-smoke', '--duration', [string]$DurationSeconds, '--data-directory', $candidateData)
    if ($secondaryMonitor) { $candidateArguments += @('--placement-monitor', $secondaryMonitor) }
    $candidateRun = Invoke-PackagedRun $candidateExe 'candidate-run' $candidateArguments $environment $workingDirectory $logRoot ($DurationSeconds + 120)
    Add-Check 'candidate-build-runs' ($candidateRun.ExitCode -eq 0) ('exit=' + $candidateRun.ExitCode)
    $afterCandidate = Get-DataStateSnapshot $candidateData

    # Both versions use the same seeded monitor and topology. The first run may
    # legitimately clamp the original seed; the candidate must preserve the actual
    # placement saved by that run, not receive a blanket placement exemption.
    $seededMonitor = [string](Get-LeafValue $legacyState.state 'state.json.monitordevice')
    $previousKeptSeededMonitor = ([string](Get-LeafValue $afterPrevious.state 'state.json.monitordevice')) -eq $seededMonitor
    $candidateChanged = Compare-DataState $legacyData $candidateData @($conditionalJustifications)
    $candidateFailures = @($candidateChanged.failures)
    Add-Check 'candidate-preserves-previous-settings' ($candidateFailures.Count -eq 0) (
        $(if ($candidateFailures.Count -eq 0) {
            'all ' + @($candidateChanged.comparedPaths).Count + ' compared JSON paths identical (' + @($candidateChanged.differences).Count + ' reported differences, all additive or justified)'
        } else {
            'changed: ' + (Format-DifferenceList $candidateFailures)
        }))

    $candidateRecordings = Get-FileHashLeafMap (Join-Path $candidateData 'voicepacks/custom') 'voicepacks/custom'
    $recordingDifferences = @(Compare-JsonLeafMap $legacyRecordings $candidateRecordings)
    Add-Check 'candidate-preserves-custom-recording' (@($recordingDifferences).Count -eq 0) (
        $(if (@($recordingDifferences).Count -eq 0) {
            @($legacyRecordings.Keys).Count.ToString() + ' pack files byte-identical (sha256)'
        } else {
            Format-DifferenceList $recordingDifferences
        }))

    $spoolKept = Test-Path -LiteralPath (Join-Path $candidateData 'bridges/dsh/legacy-instance/events.jsonl')
    Add-Check 'candidate-keeps-data-directory-spool' $spoolKept ('path=' + (Join-Path $candidateData 'bridges/dsh/legacy-instance'))
    $candidateSpoolSnapshot = Get-SpoolSnapshot (Join-Path $candidateData 'bridges/dsh/legacy-instance')
    $spoolRecordDiff = Compare-SpoolDocument $legacySpoolSnapshot.records $candidateSpoolSnapshot.records
    Add-Check 'candidate-preserves-spool-event-records' (
        @($spoolRecordDiff.failures).Count -eq 0 -and @($candidateSpoolSnapshot.parseErrors).Count -eq 0 -and $candidateSpoolSnapshot.recordCount -eq $legacySpoolSnapshot.recordCount) (
        'records=' + $legacySpoolSnapshot.recordCount + '->' + $candidateSpoolSnapshot.recordCount + ' parseErrors=' + @($candidateSpoolSnapshot.parseErrors).Count +
        $(if (@($spoolRecordDiff.failures).Count -gt 0) { ' changed: ' + (Format-DifferenceList $spoolRecordDiff.failures) } else { ' all record fields identical' }))
    $spoolCurrentDiff = Compare-SpoolDocument $legacySpoolSnapshot.current $candidateSpoolSnapshot.current
    Add-Check 'candidate-preserves-spool-current-state' (@($spoolCurrentDiff.failures).Count -eq 0) (
        $(if (@($spoolCurrentDiff.failures).Count -eq 0) { 'current.json identical across ' + @($legacySpoolSnapshot.current.Keys).Count + ' fields' } else { Format-DifferenceList $spoolCurrentDiff.failures }))
    $spoolHealthDiff = Compare-SpoolDocument $legacySpoolSnapshot.health $candidateSpoolSnapshot.health
    Add-Check 'candidate-preserves-spool-producer-health' (@($spoolHealthDiff.failures).Count -eq 0) (
        $(if (@($spoolHealthDiff.failures).Count -eq 0) { 'health.json identical' } else { Format-DifferenceList $spoolHealthDiff.failures }))

    $legacyInventory = Get-FileInventoryLeafMap $legacyData 'data' @('logs/')
    $candidateInventory = Get-FileInventoryLeafMap $candidateData 'data' @('logs/')
    $inventoryDifferences = @(Compare-JsonLeafMap $legacyInventory $candidateInventory)
    $inventoryFailures = @(Get-FailingDifferences $inventoryDifferences)
    Add-Check 'candidate-preserves-data-directory-files' ($inventoryFailures.Count -eq 0) (
        $(if ($inventoryFailures.Count -eq 0) {
            @($legacyInventory.Keys).Count.ToString() + ' non-log files still present (' + @($inventoryDifferences).Count + ' additions)'
        } else {
            Format-DifferenceList $inventoryFailures
        }))

    # The startup target, built by the production helper against a fake registry store.
    $probe = Invoke-StartupHelperProbe $evidenceRoot $candidateRoot $previousExe $candidateExe $logRoot
    $probeDocument = $probe.document
    $startupChecks = [ordered]@{
        helperUsed = $false
        quotedCandidatePath = $false
        carriesStartInTray = $false
        targetResolves = $false
        staleDetected = $false
        optInPreserved = $false
        repairRegistered = $false
    }

    if ($probe.ok -and $probeDocument) {
        $candidateTarget = [string]$probeDocument.candidateExecutable
        $startupChecks.helperUsed = ([string]$probeDocument.helperAssembly).EndsWith('TheOverseer.App.dll', [System.StringComparison]::OrdinalIgnoreCase) -and
            (Test-Path -LiteralPath ([string]$probeDocument.helperAssembly)) -and [bool]$probeDocument.dotnetExecutableRejected
        $startupChecks.quotedCandidatePath = [bool]$probeDocument.candidateExecutableUsable -and [bool]$probeDocument.commandStartsWithQuotedCandidatePath
        $startupChecks.carriesStartInTray = [bool]$probeDocument.commandCarriesStartHiddenFlag -and ([string]$probeDocument.startHiddenFlag) -eq '--start-in-tray'
        $startupChecks.targetResolves = Test-Path -LiteralPath $candidateTarget
        $startupChecks.staleDetected = ([string]$probeDocument.readForCandidateBeforeRepairState) -eq 'StalePath' -and [bool]$probeDocument.recordedCommandStillPointsAtPrevious
        $startupChecks.optInPreserved = [bool]$probeDocument.optInStillRegisteredAfterMove
        $startupChecks.repairRegistered = ([string]$probeDocument.repairState) -eq 'Enabled' -and [bool]$probeDocument.repairCommandMatchesCandidateCommand -and ([string]$probeDocument.readForCandidateAfterRepairState) -eq 'Enabled'
    }

    Add-Check 'startup-command-uses-production-helper' ([bool]$startupChecks.helperUsed) (
        $(if ($probe.ok) {
            'helper=' + [string]$probeDocument.helperAssembly + ' version=' + [string]$probeDocument.helperAssemblyVersion + ' dotnetExeRejected=' + [string]$probeDocument.dotnetExecutableRejected
        } else {
            'not executed: ' + $probe.error
        }))
    Add-Check 'startup-command-quotes-candidate-executable-and-flag' (
        [bool]$startupChecks.quotedCandidatePath -and [bool]$startupChecks.carriesStartInTray -and [bool]$startupChecks.targetResolves) (
        $(if ($probe.ok) {
            'command=' + [string]$probeDocument.candidateCommand + ' targetExists=' + [string]$startupChecks.targetResolves
        } else {
            'not executed: ' + $probe.error
        }))
    Add-Check 'startup-entry-reports-stale-path-after-move' ([bool]$startupChecks.staleDetected) (
        $(if ($probe.ok) {
            'read=' + [string]$probeDocument.readForCandidateBeforeRepairState + '/' + [string]$probeDocument.readForCandidateBeforeRepairReason +
            ' recorded=' + [string]$probeDocument.recordedCommandBeforeRepair
        } else {
            'not executed: ' + $probe.error
        }))
    Add-Check 'startup-opt-in-state-preserved' ([bool]$startupChecks.optInPreserved -and [bool]$startupChecks.repairRegistered) (
        $(if ($probe.ok) {
            'registeredAfterMove=' + [string]$probeDocument.optInStillRegisteredAfterMove + ' repair=' + [string]$probeDocument.repairState +
            ' repairedCommand=' + [string]$probeDocument.registeredCommandAfterRepair
        } else {
            'not executed: ' + $probe.error
        }))

    # The shortcut is a copy inside the evidence directory. The user's Desktop is
    # never touched: this script asserts the path is under the evidence root first.
    $shortcutPath = Join-Path $integrationDirectory 'OVERSEER.lnk'
    if (-not (Test-UnderRoot $shortcutPath $evidenceRoot)) { throw ('refusing to touch a shortcut outside ' + $evidenceRoot) }
    $shortcutCreated = $false
    $shortcutTargetsPrevious = $false
    $shortcutDetail = ''
    $shortcutFollowsCandidate = $false
    $shortcutArguments = ''
    $shell = $null
    try {
        $shell = New-Object -ComObject WScript.Shell
        $shortcut = $shell.CreateShortcut($shortcutPath)
        $shortcut.TargetPath = $previousExe
        $shortcut.WorkingDirectory = $previousRoot
        $shortcut.Arguments = '--start-in-tray'
        $shortcut.Description = 'OVERSEER evidence copy (never the user shortcut)'
        $shortcut.Save()
        $shortcutCreated = Test-Path -LiteralPath $shortcutPath
        $readBack = $shell.CreateShortcut($shortcutPath)
        $shortcutTargetsPrevious = ([string]$readBack.TargetPath) -eq $previousExe -and ([string]$readBack.Arguments).Trim() -eq '--start-in-tray'
        $copy = $shell.CreateShortcut($shortcutPath)
        $copy.TargetPath = $candidateExe
        $copy.WorkingDirectory = $candidateRoot
        $copy.Arguments = '--start-in-tray'
        $copy.Save()
        $reread = $shell.CreateShortcut($shortcutPath)
        $shortcutArguments = ([string]$reread.Arguments).Trim()
        $shortcutFollowsCandidate = ([string]$reread.TargetPath) -eq $candidateExe -and (Test-Path -LiteralPath ([string]$reread.TargetPath)) -and $shortcutArguments -eq '--start-in-tray'
        $shortcutDetail = 'copy=' + $shortcutPath + ' targetNow=' + [string]$reread.TargetPath + ' arguments=' + $shortcutArguments
    } catch {
        $shortcutDetail = 'WScript.Shell COM failed: ' + $_.Exception.Message
    } finally {
        if ($shell) { try { [void][System.Runtime.InteropServices.Marshal]::ReleaseComObject($shell) } catch { } }
    }

    Add-Check 'copied-shortcut-targets-previous-build' ($shortcutCreated -and $shortcutTargetsPrevious) (
        $(if ($shortcutDetail) { $shortcutDetail } else { 'the copied .lnk was not created' }))
    Add-Check 'shortcut-target-follows-candidate-directory' ($shortcutFollowsCandidate -and $shortcutArguments -eq '--start-in-tray') ('expectedTarget=' + $candidateExe + ' ' + $shortcutDetail)

    $integrationSettingsUnchanged = (Get-FileHash -LiteralPath $integrationSettingsPath -Algorithm SHA256).Hash -eq $integrationSettingsHash

    # Rollback: the previous build against a copy of what the candidate wrote.
    $rollbackData = Join-Path $evidenceRoot 'rollback-data'
    Copy-Item -LiteralPath $candidateData -Destination $rollbackData -Recurse -Force
    Write-Step 'running the previous build against a copy of the candidate data directory (rollback)'
    $rollbackRun = Invoke-PackagedRun $previousExe 'rollback-run' @('--hud-smoke', '--duration', [string]$DurationSeconds, '--data-directory', $rollbackData) $environment $workingDirectory $logRoot ($DurationSeconds + 120)
    Add-Check 'rollback-previous-build-runs' ($rollbackRun.ExitCode -eq 0) ('exit=' + $rollbackRun.ExitCode)
    $afterRollback = Get-DataStateSnapshot $rollbackData
    $rollbackChanged = Compare-DataState $candidateData $rollbackData @($conditionalJustifications)
    $rollbackFailures = @($rollbackChanged.failures)
    Add-Check 'rollback-preserves-candidate-settings' ($rollbackFailures.Count -eq 0) (
        $(if ($rollbackFailures.Count -eq 0) {
            'all ' + @($rollbackChanged.comparedPaths).Count + ' compared JSON paths survived the backward run (' + @($rollbackChanged.differences).Count + ' reported differences, all additive or justified)'
        } else {
            'the previous build lost data written by the candidate: ' + (Format-DifferenceList $rollbackFailures)
        }))
    $rollbackRecordings = Get-FileHashLeafMap (Join-Path $rollbackData 'voicepacks/custom') 'voicepacks/custom'
    $rollbackRecordingDiff = @(Compare-JsonLeafMap $candidateRecordings $rollbackRecordings)
    Add-Check 'rollback-preserves-custom-recording' ($rollbackRecordingDiff.Count -eq 0) (
        $(if ($rollbackRecordingDiff.Count -eq 0) { @($candidateRecordings.Keys).Count.ToString() + ' pack files byte-identical after the backward run' } else { Format-DifferenceList $rollbackRecordingDiff }))
    $rollbackSpoolSnapshot = Get-SpoolSnapshot (Join-Path $rollbackData 'bridges/dsh/legacy-instance')
    $rollbackSpoolDiff = Compare-SpoolDocument $candidateSpoolSnapshot.records $rollbackSpoolSnapshot.records
    Add-Check 'rollback-preserves-spool-records' (@($rollbackSpoolDiff.failures).Count -eq 0) (
        $(if (@($rollbackSpoolDiff.failures).Count -eq 0) { 'records=' + $rollbackSpoolSnapshot.recordCount + ' unchanged by the backward run' } else { Format-DifferenceList $rollbackSpoolDiff.failures }))

    # The documented pre-update backup/restore boundary, exercised without a launch:
    # damage the candidate-written copy, restore the pre-update backup over it, and
    # require the restored directory to reproduce the pre-update data exactly.
    $restoreData = Join-Path $evidenceRoot 'restore-data'
    Copy-Item -LiteralPath $candidateData -Destination $restoreData -Recurse -Force
    Remove-Item -LiteralPath (Join-Path $restoreData 'config.json') -Force
    Remove-Item -LiteralPath (Join-Path $restoreData 'bridges') -Recurse -Force
    Copy-Item -Path (Join-Path $backupData '*') -Destination $restoreData -Recurse -Force
    $restoredState = Get-DataStateSnapshot $restoreData
    $restoreDifferences = @(
        (Compare-JsonLeafMap (Merge-JsonLeafMap @($afterPrevious.config)) (Merge-JsonLeafMap @($restoredState.config)) @($conditionalJustifications))) +
        @(Compare-JsonLeafMap (Merge-JsonLeafMap @($afterPrevious.state)) (Merge-JsonLeafMap @($restoredState.state)) @($conditionalJustifications))
    $restoreFailures = @(Get-FailingDifferences $restoreDifferences)
    $restoredSpool = Get-SpoolSnapshot (Join-Path $restoreData 'bridges/dsh/legacy-instance')
    $restoredSpoolDiff = Compare-SpoolDocument $legacySpoolSnapshot.records $restoredSpool.records
    $restoredRecordings = Get-FileHashLeafMap (Join-Path $restoreData 'voicepacks/custom') 'voicepacks/custom'
    $restoredRecordingDiff = @(Compare-JsonLeafMap $legacyRecordings $restoredRecordings)
    Add-Check 'pre-update-backup-restores-previous-data' (
        $restoreFailures.Count -eq 0 -and @($restoredSpoolDiff.failures).Count -eq 0 -and $restoredRecordingDiff.Count -eq 0) (
        $(if ($restoreFailures.Count -eq 0 -and @($restoredSpoolDiff.failures).Count -eq 0 -and $restoredRecordingDiff.Count -eq 0) {
            'restoring the pre-update backup over a deliberately damaged candidate copy reproduces the pre-update config.json, state.json, spool records and recordings exactly'
        } else {
            'restore did not reproduce the pre-update data: ' + (Format-DifferenceList $restoreFailures) + ' ' + (Format-DifferenceList $restoredSpoolDiff.failures)
        }))

    $previousVersion = (Get-Item -LiteralPath $previousExe).VersionInfo.FileVersion
    $candidateVersion = (Get-Item -LiteralPath $candidateExe).VersionInfo.FileVersion
    $candidateProduct = (Get-Item -LiteralPath $candidateExe).VersionInfo.ProductVersion
    Add-Check 'candidate-identity-differs-from-previous' ($candidateProduct -like ('*' + $candidateRelease.sourceShortCommit + '*')) (
        'previousFileVersion=' + $previousVersion + ' candidateFileVersion=' + $candidateVersion + ' candidateProduct=' + $candidateProduct)

    $failed = @($script:Checks | Where-Object { $_.status -ne 'pass' })
    $allDifferences = @($candidateChanged.differences) + @($rollbackChanged.differences) + @($lostByPrevious.differences) + @($restoreDifferences) +
        @($spoolRecordDiff.differences) + @($spoolCurrentDiff.differences) + @($spoolHealthDiff.differences) + @($rollbackSpoolDiff.differences) +
        @($recordingDifferences) + @($rollbackRecordingDiff) + @($inventoryDifferences)
    $rollbackVerdict = if ($rollbackFailures.Count -eq 0) {
        'tested: the previous build started against data last written by the candidate and every compared setting, recording and spool record was still present'
    } else {
        'NOT compatible: the previous build run against candidate-written data lost ' + $rollbackFailures.Count + ' compared path(s); use the pre-update backup/restore route below instead'
    }
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
        previousBuildKeptSeededMonitor = $previousKeptSeededMonitor
        settingsSeeded = Get-DataStatePlain $legacyState
        settingsAfterPrevious = Get-DataStatePlain $afterPrevious
        settingsAfterCandidate = Get-DataStatePlain $afterCandidate
        settingsAfterRollback = Get-DataStatePlain $afterRollback
        comparedPaths = @($candidateChanged.comparedPaths)
        comparedPathCount = @($candidateChanged.comparedPaths).Count
        differences = $allDifferences
        failingDifferences = @(Get-FailingDifferences $allDifferences)
        normalizationJustifications = @($script:NormalizationJustifications) + @($conditionalJustifications)
        conditionalNormalizationJustifications = @($conditionalJustifications)
        settingsThePreviousBuildItselfDropped = $lostByPrevious.differences
        settingsThePreviousBuildItselfDroppedThatFail = $lostByPrevious.failures
        rollback = [ordered]@{
            performed = $true
            boundary = 'run the previous build against a copy of the data directory the candidate wrote'
            verdict = $rollbackVerdict
            previousBuildExitCode = $rollbackRun.ExitCode
            failures = $rollbackFailures
            differences = $rollbackChanged.differences
            backupRestoreVerified = $restoreFailures.Count -eq 0 -and @($restoredSpoolDiff.failures).Count -eq 0 -and $restoredRecordingDiff.Count -eq 0
            backupDirectory = $backupData
            restoreCheck = 'the candidate-written copy was deliberately damaged (config.json and bridges/ removed) and the pre-update backup was restored over it; the restored data was then compared with the pre-update data by JSON path'
        }
        spool = [ordered]@{
            directory = 'bridges/dsh/legacy-instance'
            seededRecords = $legacySpoolSnapshot.recordCount
            recordsAfterCandidate = $candidateSpoolSnapshot.recordCount
            recordsAfterRollback = $rollbackSpoolSnapshot.recordCount
            recordParseErrors = @($candidateSpoolSnapshot.parseErrors)
            recordDifferences = $spoolRecordDiff.differences
            currentStateDifferences = $spoolCurrentDiff.differences
            healthDifferences = $spoolHealthDiff.differences
            producerRecordedDirectory = [string](Get-LeafValue $legacySpoolSnapshot.health 'health.json.directory')
            producerRecordedDirectoryStillResolves = Test-Path -LiteralPath ([string](Get-LeafValue $legacySpoolSnapshot.health 'health.json.directory'))
            producerRecordedDirectoryFollowsCopy = [string](Get-LeafValue $candidateSpoolSnapshot.health 'health.json.directory') -eq $legacySpool
            producerRecordedDirectoryNote = 'the producer records the absolute spool directory it writes to; copying the data directory to a new path leaves that recorded path pointing at the copy it was written in'
        }
        integrationRecord = [ordered]@{
            fixture = $integrationSettingsPath
            recordShape = 'integrations/claude-code/lib/settings.mjs buildOwnedHandler(bridgePath) -> { type: "command", command: "node", args: [bridgePath, "overseer-claude-bridge"], timeout: 10 }'
            recordedExecutable = $recordedExecutable
            recordedExecutableResolves = $recordedReferenceResolves
            recordedExecutablePointsAtPreviousPackage = $recordedReferencePointsAtPrevious
            candidateShipsSameRelativePath = $candidateShipsRecordedRelativePath
            recordedPathFollowsPackageMove = $recordedReferenceFollowsPackageMove
            fixtureUnchangedByRuns = $integrationSettingsUnchanged
            note = 'The shipped installer records an absolute path outside the data directory, so extracting a new version into a new directory leaves that recorded path pointing at the older directory. Re-run the shipped installer (or rebind through the app) after upgrading; an archive extraction cannot rewrite a user hook setting.'
        }
        startupTarget = [ordered]@{
            productionHelper = 'TheOverseer.App.Desktop.StartupRegistration (BuildCommand, IsUsableExecutablePath, ValueName)'
            helperExecuted = $probe.ok
            helperAssembly = $(if ($probe.ok) { [string]$probeDocument.helperAssembly } else { '' })
            probeError = $probe.error
            probeBuildLog = $probe.buildLogPath
            probeRunLog = $probe.runLogPath
            fakeRegistryStore = $probe.registryPath
            valueName = $(if ($probe.ok) { [string]$probeDocument.valueName } else { 'OVERSEER' })
            previousCommand = $(if ($probe.ok) { [string]$probeDocument.previousCommand } else { '' })
            candidateCommand = $(if ($probe.ok) { [string]$probeDocument.candidateCommand } else { '' })
            requestedByProbe = $startupChecks
            realRegistryTouched = $false
        }
        shortcut = [ordered]@{
            isEvidenceCopy = (Test-UnderRoot $shortcutPath $evidenceRoot)
            path = $shortcutPath
            created = $shortcutCreated
            targetedPreviousBuild = $shortcutTargetsPrevious
            followsCandidateDirectory = $shortcutFollowsCandidate
            arguments = $shortcutArguments
            realDesktopShortcutTouched = $false
        }
        physicalChecksNotPerformed = @(
            'physical sleep/wake, monitor disconnect/reconnect and mixed-DPI placement restore',
            'desktop shortcut rewrite of the real user shortcut (a copy inside the evidence directory is used instead)',
            'Windows startup entry rewrite (the real HKCU Run value is never read or written)',
            'audible voice playback by ear'
        )
        checksNotPerformed = @(Get-ChecksNotPerformed @{
            RollbackPerformed = $true
            SpoolCompared = $true
            ShortcutChecked = $true
            IntegrationChecked = $true
            RestoreChecked = $true
        })
        visibleChecksDeferred = [bool]$visibleChecksDeferred
        visibleCheckMonitor = $secondaryMonitor
        logs = $logRoot
        generatedAtUtc = (Get-Date).ToUniversalTime().ToString('o')
        checks = @($script:Checks)
    }
    $evidencePath = Join-Path $evidenceRoot ('upgrade-evidence-' + $stamp + '.json')
    $evidence | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $evidencePath -Encoding utf8

    Write-Host ''
    Write-Host ('checks  : ' + $script:Checks.Count + ' total, ' + $failed.Count + ' failed')
    Write-Host ('paths   : ' + @($candidateChanged.comparedPaths).Count + ' compared JSON paths, ' + @($allDifferences).Count + ' reported differences')
    Write-Host ('rollback: ' + $rollbackVerdict)
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
