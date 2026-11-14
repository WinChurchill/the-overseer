#requires -Version 7.0
<#
.SYNOPSIS
    Installs, upgrades and uninstalls an OVERSEER setup .exe and checks each step.

.DESCRIPTION
    Uses a throwaway install folder and data folder. Refuses to run when OVERSEER is
    already installed for this user, so it never replaces a real installation.
    Checks that:
      - installed files match the release ZIP byte for byte;
      - the Apps & features entry and Start-menu shortcut are created;
      - reinstalling over the same folder removes files the old version left;
      - a running copy started from the install folder is closed by the upgrade;
      - uninstalling removes the files, entry and shortcut, removes a startup entry
        only when it points at this folder, and leaves user data and Claude Code
        settings alone.
    Writes installer-results.json (and the setup logs) to -EvidenceDirectory and
    exits 1 when any check fails.

.EXAMPLE
    pwsh -NoProfile -File scripts/Test-Installer.ps1 -InstallerPath out/OVERSEER-1.0.0-alpha.10-setup.exe -ArchivePath out/OVERSEER-1.0.0-alpha.10-win-x64.zip -EvidenceDirectory artifacts/installer-test
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $InstallerPath,
    [Parameter(Mandatory)][string] $ArchivePath,
    [Parameter(Mandatory)][string] $EvidenceDirectory,
    [switch] $SkipRunningAppCheck
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem

$AppId = '{4BD1C443-6251-4072-907B-DE45F918FB28}'
$UninstallKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\${AppId}_is1"
$RunKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$RunValue = 'OVERSEER'
$StartMenuLink = Join-Path ([Environment]::GetFolderPath('Programs')) 'OVERSEER.lnk'
$ClaudeSettings = Join-Path $env:USERPROFILE '.claude/settings.json'

$installer = (Resolve-Path -LiteralPath $InstallerPath).Path
$archive = (Resolve-Path -LiteralPath $ArchivePath).Path
New-Item -ItemType Directory -Force -Path $EvidenceDirectory | Out-Null
$evidence = (Resolve-Path -LiteralPath $EvidenceDirectory).Path

$results = [System.Collections.Generic.List[object]]::new()
function Check([string] $name, [bool] $passed, [string] $detail = '') {
    $results.Add([ordered]@{ check = $name; passed = $passed; detail = $detail })
    Write-Host ('{0} {1}{2}' -f ($(if ($passed) { 'PASS' } else { 'FAIL' })), $name, $(if ($detail) { " - $detail" } else { '' }))
}

if (Test-Path -LiteralPath $UninstallKey) {
    throw 'OVERSEER is already installed for this user. Uninstall it, or run this test as another Windows user; the test never replaces a real installation.'
}
if (Test-Path -LiteralPath $StartMenuLink) { throw "A Start-menu shortcut already exists at $StartMenuLink; refusing to overwrite it." }

$work = Join-Path ([System.IO.Path]::GetTempPath()) ('overseer-installer-test-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
$appDir = Join-Path $work 'app'
$dataDir = Join-Path $work 'data'
New-Item -ItemType Directory -Force -Path $dataDir | Out-Null
Set-Content -LiteralPath (Join-Path $dataDir 'keep-me.txt') -Value 'user data must survive uninstall'

$claudeBefore = if (Test-Path -LiteralPath $ClaudeSettings) { (Get-FileHash -LiteralPath $ClaudeSettings -Algorithm SHA256).Hash } else { $null }
$DesktopLink = Join-Path ([Environment]::GetFolderPath('Desktop')) 'OVERSEER.lnk'
$desktopBefore = if (Test-Path -LiteralPath $DesktopLink) { (Get-FileHash -LiteralPath $DesktopLink -Algorithm SHA256).Hash } else { $null }
$runBefore = (Get-ItemProperty -LiteralPath $RunKey -Name $RunValue -ErrorAction SilentlyContinue)?.$RunValue
$process = $null

function Invoke-Setup([string] $logName, [string[]] $extra = @()) {
    $arguments = @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', "/DIR=`"$appDir`"", "/LOG=`"$(Join-Path $evidence $logName)`"") + $extra
    $p = Start-Process -FilePath $installer -ArgumentList $arguments -Wait -PassThru
    return $p.ExitCode
}

function Compare-WithArchive([string] $label) {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [System.IO.Compression.ZipFile]::OpenRead($archive)
    $mismatch = [System.Collections.Generic.List[string]]::new()
    $expected = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    try {
        foreach ($entry in $zip.Entries) {
            if ($entry.FullName.EndsWith('/')) { continue }
            $relative = $entry.FullName.Replace('/', '\')
            [void] $expected.Add($relative)
            $target = Join-Path $appDir $relative
            if (-not (Test-Path -LiteralPath $target -PathType Leaf)) { $mismatch.Add("missing $relative"); continue }
            $stream = $entry.Open()
            try { $zipHash = [Convert]::ToHexString([System.Security.Cryptography.SHA256]::HashData($stream)) } finally { $stream.Dispose() }
            if ($zipHash -ne (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash) { $mismatch.Add("changed $relative") }
        }
    }
    finally { $zip.Dispose() }
    $extra = Get-ChildItem -LiteralPath $appDir -Recurse -File | ForEach-Object { [System.IO.Path]::GetRelativePath($appDir, $_.FullName) } |
        Where-Object { -not $expected.Contains($_) -and $_ -notmatch '^unins\d{3}\.(exe|dat)$' }
    foreach ($e in @($extra)) { $mismatch.Add("unexpected $e") }
    Check "$label files match the ZIP" ($mismatch.Count -eq 0) $(if ($mismatch.Count) { ($mismatch | Select-Object -First 5) -join '; ' } else { "$($expected.Count) files" })
}

function Wait-Until([scriptblock] $condition, [int] $seconds) {
    $deadline = [DateTime]::UtcNow.AddSeconds($seconds)
    while ([DateTime]::UtcNow -lt $deadline) { if (& $condition) { return $true }; Start-Sleep -Milliseconds 250 }
    return [bool] (& $condition)
}

try {
    $version = ([System.IO.Compression.ZipFile]::OpenRead($archive) | ForEach-Object {
        try { $r = [System.IO.StreamReader]::new($_.GetEntry('release.json').Open()); try { ($r.ReadToEnd() | ConvertFrom-Json).version } finally { $r.Dispose() } } finally { $_.Dispose() } })

    # 1. Fresh install
    $code = Invoke-Setup 'install.log'
    Check 'fresh install exits 0' ($code -eq 0) "exit $code"
    Compare-WithArchive 'fresh install'
    $entry = Get-ItemProperty -LiteralPath $UninstallKey -ErrorAction SilentlyContinue
    Check 'Apps & features entry created' ($null -ne $entry)
    if ($entry) {
        Check 'entry shows the release version' ($entry.DisplayVersion -eq $version) "$($entry.DisplayVersion)"
        Check 'entry points at the install folder' ([System.IO.Path]::TrimEndingDirectorySeparator($entry.InstallLocation) -ieq $appDir) "$($entry.InstallLocation)"
    }
    Check 'Start-menu shortcut created' (Test-Path -LiteralPath $StartMenuLink)
    $desktopAfter = if (Test-Path -LiteralPath $DesktopLink) { (Get-FileHash -LiteralPath $DesktopLink -Algorithm SHA256).Hash } else { $null }
    Check 'no desktop shortcut by default' ($desktopAfter -eq $desktopBefore) $(if ($desktopBefore) { 'an existing shortcut was left as it was' } else { '' })

    # 2. Upgrade in place removes leftovers from the previous version
    $stale = Join-Path $appDir 'left-by-old-version.dll'
    Set-Content -LiteralPath $stale -Value 'stale'
    New-Item -ItemType Directory -Force -Path (Join-Path $appDir 'old-folder') | Out-Null
    $code = Invoke-Setup 'upgrade.log'
    Check 'upgrade exits 0' ($code -eq 0) "exit $code"
    Check 'upgrade removes stale files and folders' (-not (Test-Path -LiteralPath $stale) -and -not (Test-Path -LiteralPath (Join-Path $appDir 'old-folder')))
    Compare-WithArchive 'upgraded'

    # 3. Upgrade while the app is running from the install folder
    if (-not $SkipRunningAppCheck) {
        $exe = Join-Path $appDir 'TheOverseer.App.exe'
        $process = Start-Process -FilePath $exe -ArgumentList @('--start-in-tray', '--data-directory', "`"$dataDir`"") -PassThru
        $started = Wait-Until { $process.Refresh(); -not $process.HasExited -and $process.Responding } 20
        Start-Sleep -Seconds 3
        Check 'test copy of the app starts' ($started -and -not $process.HasExited)
        $code = Invoke-Setup 'upgrade-running.log' @('/CLOSEAPPLICATIONS')
        $closed = Wait-Until { $process.Refresh(); $process.HasExited } 15
        Check 'upgrade closes the running copy' $closed
        Check 'upgrade over a running copy exits 0' ($code -eq 0) "exit $code"
        Compare-WithArchive 'upgraded-while-running'
        if (-not $process.HasExited) { $process.Kill() }
    }

    # 4. Uninstall
    $ownRun = $null -eq $runBefore
    if ($ownRun) {
        Set-ItemProperty -LiteralPath $RunKey -Name $RunValue -Value ('"' + (Join-Path $appDir 'TheOverseer.App.exe') + '" --start-in-tray')
    }
    $uninstaller = Join-Path $appDir 'unins000.exe'
    Check 'uninstaller present' (Test-Path -LiteralPath $uninstaller)
    $u = Start-Process -FilePath $uninstaller -ArgumentList @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', "/LOG=`"$(Join-Path $evidence 'uninstall.log')`"") -Wait -PassThru
    Check 'uninstall exits 0' ($u.ExitCode -eq 0) "exit $($u.ExitCode)"
    # The uninstaller relaunches itself from a temp copy; wait for it to finish.
    $gone = Wait-Until { -not (Test-Path -LiteralPath $UninstallKey) -and -not (Test-Path -LiteralPath (Join-Path $appDir 'TheOverseer.App.exe')) } 60
    Check 'uninstall removes app files and the Apps & features entry' $gone
    $leftovers = if (Test-Path -LiteralPath $appDir) { @(Get-ChildItem -LiteralPath $appDir -Recurse -Force).Count } else { 0 }
    Check 'install folder left empty or removed' ($leftovers -eq 0) "$leftovers item(s)"
    Check 'Start-menu shortcut removed' (-not (Test-Path -LiteralPath $StartMenuLink))
    $runAfter = (Get-ItemProperty -LiteralPath $RunKey -Name $RunValue -ErrorAction SilentlyContinue)?.$RunValue
    if ($ownRun) { Check 'startup entry pointing at this folder removed' ($null -eq $runAfter) }
    else { Check 'existing startup entry for another copy kept' ($runAfter -eq $runBefore) }
    Check 'user data kept' (Test-Path -LiteralPath (Join-Path $dataDir 'keep-me.txt'))
    $claudeAfter = if (Test-Path -LiteralPath $ClaudeSettings) { (Get-FileHash -LiteralPath $ClaudeSettings -Algorithm SHA256).Hash } else { $null }
    Check 'Claude Code settings untouched' ($claudeAfter -eq $claudeBefore)
}
finally {
    if ($process -and -not $process.HasExited) { try { $process.Kill() } catch { } }
    # Never leave a test install registered, even after a failure.
    if (Test-Path -LiteralPath $UninstallKey) {
        $cleanup = Join-Path $appDir 'unins000.exe'
        if (Test-Path -LiteralPath $cleanup) {
            Start-Process -FilePath $cleanup -ArgumentList @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART') -Wait | Out-Null
            [void] (Wait-Until { -not (Test-Path -LiteralPath $UninstallKey) } 60)
        }
    }
    $runNow = (Get-ItemProperty -LiteralPath $RunKey -Name $RunValue -ErrorAction SilentlyContinue)?.$RunValue
    if ($null -eq $runBefore -and $runNow -and $runNow.Contains($appDir)) { Remove-ItemProperty -LiteralPath $RunKey -Name $RunValue }
    if (Test-Path -LiteralPath $work) { Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue }
}

$failed = @($results | Where-Object { -not $_.passed }).Count
[ordered]@{
    installer = Split-Path -Leaf $installer
    installerSha256 = (Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash
    archive = Split-Path -Leaf $archive
    archiveSha256 = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash
    passed = $results.Count - $failed
    failed = $failed
    checks = $results
} | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $evidence 'installer-results.json') -Encoding utf8
Write-Host ("{0} passed, {1} failed" -f ($results.Count - $failed), $failed)
if ($failed) { exit 1 }
