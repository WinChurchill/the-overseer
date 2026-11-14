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
      - upgrades remove only unchanged, retired package files, preserving user files;
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
$OwnershipName = 'overseer-installed-files.txt'
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
$runBefore = (Get-ItemProperty -LiteralPath $RunKey -Name $RunValue -ErrorAction SilentlyContinue)?.$RunValue
if ($null -ne $runBefore) { throw 'An OVERSEER startup entry exists. Run installer ownership checks in a disposable Windows user profile.' }

$work = Join-Path ([System.IO.Path]::GetTempPath()) ('overseer-installer-test-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
$appDir = Join-Path $work 'app'
$dataDir = Join-Path $work 'data'
New-Item -ItemType Directory -Force -Path $dataDir | Out-Null
Set-Content -LiteralPath (Join-Path $dataDir 'keep-me.txt') -Value 'user data must survive uninstall'

$claudeBefore = if (Test-Path -LiteralPath $ClaudeSettings) { (Get-FileHash -LiteralPath $ClaudeSettings -Algorithm SHA256).Hash } else { $null }
$DesktopLink = Join-Path ([Environment]::GetFolderPath('Desktop')) 'OVERSEER.lnk'
$desktopBefore = if (Test-Path -LiteralPath $DesktopLink) { (Get-FileHash -LiteralPath $DesktopLink -Algorithm SHA256).Hash } else { $null }
$process = $null
$script:UnownedFiles = @('custom-note.txt', 'data\keep-me.json', 'modified-retired.dll')

function Invoke-Setup([string] $logName, [string[]] $extra = @()) {
    $arguments = @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', "/DIR=`"$appDir`"", "/LOG=`"$(Join-Path $evidence $logName)`"") + $extra
    $p = Start-Process -FilePath $installer -ArgumentList $arguments -WindowStyle Hidden -Wait -PassThru
    return $p.ExitCode
}

function Compare-WithArchive([string] $label) {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [System.IO.Compression.ZipFile]::OpenRead($archive)
    $mismatch = [System.Collections.Generic.List[string]]::new()
    $expected = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $expectedHashes = @{}
    try {
        foreach ($entry in $zip.Entries) {
            if ($entry.FullName.EndsWith('/')) { continue }
            $relative = $entry.FullName.Replace('/', '\')
            [void] $expected.Add($relative)
            $target = Join-Path $appDir $relative
            if (-not (Test-Path -LiteralPath $target -PathType Leaf)) { $mismatch.Add("missing $relative"); continue }
            $stream = $entry.Open()
            try { $zipHash = [Convert]::ToHexString([System.Security.Cryptography.SHA256]::HashData($stream)) } finally { $stream.Dispose() }
            $expectedHashes[$relative] = $zipHash
            if ($zipHash -ne (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash) { $mismatch.Add("changed $relative") }
        }
    }
    finally { $zip.Dispose() }
    $extra = Get-ChildItem -LiteralPath $appDir -Recurse -File | ForEach-Object { [System.IO.Path]::GetRelativePath($appDir, $_.FullName) } |
        Where-Object { -not $expected.Contains($_) -and $_ -ne $OwnershipName -and $_ -notin $script:UnownedFiles -and $_ -notmatch '^unins\d{3}\.(exe|dat)$' }
    foreach ($e in @($extra)) { $mismatch.Add("unexpected $e") }
    Check "$label files match the ZIP" ($mismatch.Count -eq 0) $(if ($mismatch.Count) { ($mismatch | Select-Object -First 5) -join '; ' } else { "$($expected.Count) files" })
    $inventory = Join-Path $appDir $OwnershipName
    $owned = @{}
    if (Test-Path -LiteralPath $inventory) {
        foreach ($line in Get-Content -LiteralPath $inventory) {
            $parts = $line -split "`t", 2
            if ($parts.Count -eq 2) { $owned[$parts[1]] = $parts[0] }
        }
    }
    $valid = $owned.Count -eq $expected.Count
    foreach ($name in $expectedHashes.Keys) { if ($owned[$name] -ine $expectedHashes[$name]) { $valid = $false } }
    Check "$label ownership inventory matches the ZIP hashes" $valid
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

    # Seed a previous package inventory, plus unmanaged/edited files and an escape.
    $stale = Join-Path $appDir 'old-folder/retired.dll'
    New-Item -ItemType Directory -Force -Path (Join-Path $appDir 'old-folder') | Out-Null
    Set-Content -LiteralPath $stale -Value 'retired package file'
    $inventory = Join-Path $appDir $OwnershipName
    Add-Content -LiteralPath $inventory -Value ((Get-FileHash $stale -Algorithm SHA256).Hash + "`told-folder\retired.dll")
    Set-Content -LiteralPath (Join-Path $appDir 'modified-retired.dll') -Value 'original package bytes'
    Add-Content -LiteralPath $inventory -Value ((Get-FileHash (Join-Path $appDir 'modified-retired.dll') -Algorithm SHA256).Hash + "`tmodified-retired.dll")
    Set-Content -LiteralPath (Join-Path $appDir 'modified-retired.dll') -Value 'edited bytes must survive'
    Set-Content -LiteralPath (Join-Path $appDir 'custom-note.txt') -Value 'unmanaged notes'
    New-Item -ItemType Directory -Force -Path (Join-Path $appDir 'data') | Out-Null
    Set-Content -LiteralPath (Join-Path $appDir 'data/keep-me.json') -Value '{"private":"keep"}'
    $unownedHashes = @{}
    foreach ($name in $script:UnownedFiles) { $unownedHashes[$name] = (Get-FileHash (Join-Path $appDir $name) -Algorithm SHA256).Hash }
    Add-Content -LiteralPath $inventory -Value ((Get-FileHash (Join-Path $dataDir 'keep-me.txt') -Algorithm SHA256).Hash + "`t..\data\keep-me.txt")
    Add-Content -LiteralPath $inventory -Value ((Get-FileHash (Join-Path $dataDir 'keep-me.txt') -Algorithm SHA256).Hash + "`t.. \data\keep-me.txt")
    $external = Join-Path $work 'external'
    New-Item -ItemType Directory -Path $external | Out-Null
    Set-Content -LiteralPath (Join-Path $external 'retired.dll') -Value 'junction target must survive'
    New-Item -ItemType Junction -Path (Join-Path $appDir 'linked-payload') -Target $external | Out-Null
    Add-Content -LiteralPath $inventory -Value ((Get-FileHash (Join-Path $external 'retired.dll') -Algorithm SHA256).Hash + "`tlinked-payload\retired.dll")
    $code = Invoke-Setup 'upgrade.log'
    Check 'upgrade exits 0' ($code -eq 0) "exit $code"
    Check 'upgrade removes only unchanged retired package files and empty directories' (-not (Test-Path -LiteralPath $stale) -and -not (Test-Path -LiteralPath (Join-Path $appDir 'old-folder')))
    foreach ($name in $script:UnownedFiles) { Check "upgrade preserves $name" ((Get-FileHash (Join-Path $appDir $name) -Algorithm SHA256).Hash -eq $unownedHashes[$name]) }
    Check 'upgrade rejects parent traversal in the old inventory' (Test-Path -LiteralPath (Join-Path $dataDir 'keep-me.txt'))
    Check 'upgrade never follows a junction while removing obsolete files' (Test-Path -LiteralPath (Join-Path $external 'retired.dll'))
    Compare-WithArchive 'upgraded'

    # 3. Upgrade while the app is running from the install folder
    if (-not $SkipRunningAppCheck) {
        $exe = Join-Path $appDir 'TheOverseer.App.exe'
        $process = Start-Process -FilePath $exe -ArgumentList @('--start-in-tray', '--data-directory', "`"$dataDir`"") -WindowStyle Hidden -PassThru
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
    if (-not (Test-Path -LiteralPath $RunKey)) { New-Item -Path $RunKey -Force | Out-Null }
    $foreignRun = '"' + (Join-Path $work 'portable/TheOverseer.App.exe') + '" --data-directory "' + (Join-Path $appDir 'data') + '"'
    Set-ItemProperty -LiteralPath $RunKey -Name $RunValue -Value $foreignRun
    $uninstaller = Join-Path $appDir 'unins000.exe'
    Check 'uninstaller present' (Test-Path -LiteralPath $uninstaller)
    $u = Start-Process -FilePath $uninstaller -ArgumentList @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', "/LOG=`"$(Join-Path $evidence 'uninstall.log')`"") -WindowStyle Hidden -Wait -PassThru
    Check 'uninstall exits 0' ($u.ExitCode -eq 0) "exit $($u.ExitCode)"
    # The uninstaller relaunches itself from a temp copy; wait for it to finish.
    $gone = Wait-Until { -not (Test-Path -LiteralPath $UninstallKey) -and -not (Test-Path -LiteralPath (Join-Path $appDir 'TheOverseer.App.exe')) } 60
    Check 'uninstall removes app files and the Apps & features entry' $gone
    foreach ($name in $script:UnownedFiles) { Check "uninstall preserves $name" ((Get-FileHash (Join-Path $appDir $name) -Algorithm SHA256).Hash -eq $unownedHashes[$name]) }
    Check 'uninstall removes the installer ownership inventory' (-not (Test-Path -LiteralPath $inventory))
    Check 'Start-menu shortcut removed' (-not (Test-Path -LiteralPath $StartMenuLink))
    $runAfter = (Get-ItemProperty -LiteralPath $RunKey -Name $RunValue -ErrorAction SilentlyContinue)?.$RunValue
    Check 'different executable with a data argument inside this install keeps its startup entry' ($runAfter -ceq $foreignRun)
    Check 'user data kept' (Test-Path -LiteralPath (Join-Path $dataDir 'keep-me.txt'))
    $claudeAfter = if (Test-Path -LiteralPath $ClaudeSettings) { (Get-FileHash -LiteralPath $ClaudeSettings -Algorithm SHA256).Hash } else { $null }
    Check 'Claude Code settings untouched' ($claudeAfter -eq $claudeBefore)

    # A second install/uninstall exercises the positive executable-ownership case.
    Remove-ItemProperty -LiteralPath $RunKey -Name $RunValue -ErrorAction SilentlyContinue
    $code = Invoke-Setup 'reinstall.log'
    Check 'reinstall alongside preserved user content exits 0' ($code -eq 0)
    Set-ItemProperty -LiteralPath $RunKey -Name $RunValue -Value ('"' + (Join-Path $appDir 'TheOverseer.App.exe') + '" --start-in-tray')
    $u = Start-Process -FilePath (Join-Path $appDir 'unins000.exe') -ArgumentList @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART') -WindowStyle Hidden -Wait -PassThru
    Check 'second uninstall exits 0' ($u.ExitCode -eq 0)
    [void] (Wait-Until { -not (Test-Path -LiteralPath $UninstallKey) } 60)
    $runAfter = (Get-ItemProperty -LiteralPath $RunKey -Name $RunValue -ErrorAction SilentlyContinue)?.$RunValue
    Check 'startup entry for the exact installed executable is removed' ($null -eq $runAfter)
}
finally {
    if ($process -and -not $process.HasExited) { try { $process.Kill() } catch { } }
    # Never leave a test install registered, even after a failure.
    if (Test-Path -LiteralPath $UninstallKey) {
        $cleanup = Join-Path $appDir 'unins000.exe'
        if (Test-Path -LiteralPath $cleanup) {
            Start-Process -FilePath $cleanup -ArgumentList @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART') -WindowStyle Hidden -Wait | Out-Null
            [void] (Wait-Until { -not (Test-Path -LiteralPath $UninstallKey) } 60)
        }
    }
    $runNow = (Get-ItemProperty -LiteralPath $RunKey -Name $RunValue -ErrorAction SilentlyContinue)?.$RunValue
    if ($null -eq $runBefore -and $runNow -and $runNow.Contains($appDir)) { Remove-ItemProperty -LiteralPath $RunKey -Name $RunValue }
    $resolvedWork = [IO.Path]::GetFullPath($work)
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    if (-not $resolvedWork.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($resolvedWork) -notlike 'overseer-installer-test-*') { throw 'Test cleanup escaped its temporary directory' }
    if (Test-Path -LiteralPath $resolvedWork) { Remove-Item -LiteralPath $resolvedWork -Recurse -Force -ErrorAction SilentlyContinue }
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
