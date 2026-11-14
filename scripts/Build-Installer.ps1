#requires -Version 7.0
<#
.SYNOPSIS
    Builds the OVERSEER per-user setup .exe from an already-built release ZIP.

.DESCRIPTION
    The ZIP stays the source of truth: this script extracts the exact ZIP, checks
    its checksum and release.json, and compiles installer/OVERSEER.iss around
    those files with Inno Setup. The installer therefore installs the same bytes
    the package tests checked. It writes the setup .exe, a .sha256 file and
    installer-manifest.json next to it.

.EXAMPLE
    pwsh -NoProfile -File scripts/Build-Installer.ps1 -ArchivePath artifacts/ci-package/OVERSEER-1.0.0-alpha.10-win-x64.zip
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $ArchivePath,
    [string] $OutputDirectory,
    [string] $IsccPath,
    [string] $InstallerName
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
function Resolve-RepoPath([string] $path) {
    if ([System.IO.Path]::IsPathRooted($path)) { return [System.IO.Path]::GetFullPath($path) }
    return [System.IO.Path]::GetFullPath((Join-Path $repoRoot $path))
}

$archive = Resolve-RepoPath $ArchivePath
if (-not (Test-Path -LiteralPath $archive -PathType Leaf)) { throw "Archive not found: $archive" }
$outputRoot = if ($OutputDirectory) { Resolve-RepoPath $OutputDirectory } else { Split-Path -Parent $archive }
New-Item -ItemType Directory -Force -Path $outputRoot | Out-Null

# Find the Inno Setup compiler: parameter, environment, the repo's .tools copy, then PATH.
$candidates = @($IsccPath, $env:OVERSEER_ISCC,
    (Join-Path $repoRoot '.tools/innosetup/ISCC.exe'),
    (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6/ISCC.exe'),
    (Join-Path $env:ProgramFiles 'Inno Setup 6/ISCC.exe')) | Where-Object { $_ }
$iscc = $candidates | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
if (-not $iscc) { $iscc = (Get-Command ISCC.exe -ErrorAction SilentlyContinue)?.Source }
if (-not $iscc) { throw 'Inno Setup compiler (ISCC.exe) not found. Pass -IsccPath or set OVERSEER_ISCC.' }

# Check the ZIP against its checksum file when one is present.
$archiveHash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash
$sidecar = $archive + '.sha256'
if (Test-Path -LiteralPath $sidecar) {
    $expected = ((Get-Content -LiteralPath $sidecar -Raw).Trim() -split '\s+')[0]
    if ($expected -ne $archiveHash) { throw "Archive hash $archiveHash does not match $sidecar ($expected)" }
}

$staging = Join-Path $outputRoot ('installer-staging-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
$payload = Join-Path $staging 'payload'
try {
    New-Item -ItemType Directory -Force -Path $payload | Out-Null
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::ExtractToDirectory($archive, $payload)

    $releaseFile = Join-Path $payload 'release.json'
    if (-not (Test-Path -LiteralPath $releaseFile)) { throw 'release.json is missing from the archive' }
    $release = Get-Content -LiteralPath $releaseFile -Raw | ConvertFrom-Json
    $version = [string] $release.version
    if ($version -notmatch '^\d+\.\d+\.\d+(-(alpha|beta|rc)\.\d+)?$') { throw "Unexpected release version '$version'" }
    if (-not (Test-Path -LiteralPath (Join-Path $payload 'TheOverseer.App.exe'))) { throw 'TheOverseer.App.exe is missing from the archive' }
    $payloadFiles = @(Get-ChildItem -LiteralPath $payload -Recurse -File).Count

    if (-not $InstallerName) { $InstallerName = "OVERSEER-$version-setup.exe" }
    if ($InstallerName -notmatch '^[A-Za-z0-9._-]+\.exe$') { throw "Invalid installer name '$InstallerName'" }
    $outputName = [System.IO.Path]::GetFileNameWithoutExtension($InstallerName)
    $installer = Join-Path $outputRoot $InstallerName
    if (Test-Path -LiteralPath $installer) { Remove-Item -LiteralPath $installer -Force }

    Write-Host "==> compiling $InstallerName with $iscc"
    $script = Join-Path $repoRoot 'installer/OVERSEER.iss'
    & $iscc /Q "/DAppVersion=$version" "/DPayloadDir=$payload" "/DOutputDir=$outputRoot" "/DOutputName=$outputName" $script
    if ($LASTEXITCODE) { throw "ISCC failed with exit code $LASTEXITCODE" }
    if (-not (Test-Path -LiteralPath $installer)) { throw "ISCC did not produce $installer" }

    $installerHash = (Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash
    $installerBytes = (Get-Item -LiteralPath $installer).Length
    Set-Content -LiteralPath ($installer + '.sha256') -Value ($installerHash + '  ' + $InstallerName) -Encoding ascii
    [ordered]@{
        version = $version
        sourceCommit = $release.sourceCommit
        archive = Split-Path -Leaf $archive
        archiveSha256 = $archiveHash
        installer = $InstallerName
        installerSha256 = $installerHash
        installerBytes = $installerBytes
        payloadFiles = $payloadFiles
        installScope = 'CurrentUser'
        defaultInstallDirectory = '%LOCALAPPDATA%\Programs\OVERSEER'
        innoSetup = (Get-Item -LiteralPath $iscc).VersionInfo.ProductVersion
        windowsCodeSigned = $false
    } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $outputRoot 'installer-manifest.json') -Encoding utf8

    Write-Host ('installer : ' + $installer)
    Write-Host ('sha256    : ' + $installerHash)
    Write-Host ('bytes     : ' + $installerBytes)
}
finally {
    if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Recurse -Force }
}
