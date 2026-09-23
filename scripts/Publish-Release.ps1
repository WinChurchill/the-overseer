<#
.SYNOPSIS
    Builds the OVERSEER V1 self-contained Windows x64 release payload and archive.

.DESCRIPTION
    Publishes src/TheOverseer.App self-contained (Release/win-x64, no trimming,
    no AOT, no single file), assembles the explicit shipping payload, writes
    OVERSEER-v1-win-x64.zip, and records a machine-readable build manifest with
    the source commit, versions, payload file list and archive hash.

    The process is repeatable and validated, not byte-identical: archive
    timestamps differ between runs.

    Run it from anywhere; relative paths (including -OutputDirectory) resolve
    against the repository root, which is found from this script's location.

.EXAMPLE
    pwsh -NoProfile -File scripts/Publish-Release.ps1 -OutputDirectory artifacts/p4-release
#>
[CmdletBinding()]
param(
    [string] $OutputDirectory = 'artifacts/p4-release',
    [string] $Configuration = 'Release',
    [string] $RuntimeIdentifier = 'win-x64',
    [string] $Version = '1.0.0',
    [string] $ArchiveName = 'OVERSEER-v1-win-x64.zip',
    [string] $NuGetConfig,
    [switch] $AllowDirtyTree,
    [switch] $KeepStaging
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

function Write-Step([string] $message) { Write-Host ('==> ' + $message) }

function Invoke-Checked([string] $file, [string[]] $arguments, [string] $what) {
    Write-Host ('    ' + $file + ' ' + ($arguments -join ' '))
    & $file @arguments
    $code = $LASTEXITCODE
    if ($code -ne 0) { throw ($what + ' failed with exit code ' + $code) }
}

function Resolve-UnderRoot([string] $path, [string] $root) {
    if ([System.IO.Path]::IsPathRooted($path)) { return [System.IO.Path]::GetFullPath($path) }
    return [System.IO.Path]::GetFullPath((Join-Path $root $path))
}

# Removes one staging file or directory only when it is verifiably inside the
# intended root. No broad recursive deletion is ever performed against a
# caller-supplied path.
function Remove-ValidatedItem([string] $path, [string] $root) {
    $full = [System.IO.Path]::GetFullPath($path)
    $rootFull = [System.IO.Path]::GetFullPath($root).TrimEnd('\', '/') + [System.IO.Path]::DirectorySeparatorChar
    if (-not $full.StartsWith($rootFull, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw ('refusing to remove ' + $full + ' because it is outside ' + $rootFull)
    }

    if (Test-Path -LiteralPath $full) { Remove-Item -LiteralPath $full -Recurse -Force }
}

$repoRoot = Split-Path -Parent $PSScriptRoot
$solution = Join-Path $repoRoot 'TheOverseer.sln'
if (-not (Test-Path -LiteralPath $solution)) { throw ('repository root not found from ' + $PSScriptRoot) }

$appProject = Join-Path $repoRoot 'src/TheOverseer.App/TheOverseer.App.csproj'
$outputRoot = Resolve-UnderRoot $OutputDirectory $repoRoot
$stamp = (Get-Date).ToUniversalTime().ToString('yyyyMMddTHHmmssZ')
$stagingRoot = Join-Path $outputRoot ('staging-' + $stamp + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
$publishRoot = Join-Path $stagingRoot 'publish'
$payloadRoot = Join-Path $stagingRoot 'payload'
$archivePath = Join-Path $outputRoot $ArchiveName
$manifestPath = Join-Path $outputRoot 'build-manifest.json'
$generatedNuGetConfig = $null

New-Item -ItemType Directory -Force -Path $outputRoot | Out-Null
New-Item -ItemType Directory -Force -Path $publishRoot | Out-Null
New-Item -ItemType Directory -Force -Path $payloadRoot | Out-Null

Write-Step ('repository ' + $repoRoot)
Write-Step ('output     ' + $outputRoot)

# ---- source identity ---------------------------------------------------------
$sourceCommit = (& git -C $repoRoot rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0) { throw 'git rev-parse failed; the release must come from a Git checkout' }
$sourceShort = (& git -C $repoRoot rev-parse --short HEAD).Trim()
$sourceBranch = (& git -C $repoRoot rev-parse --abbrev-ref HEAD).Trim()
$dirty = -not [string]::IsNullOrWhiteSpace((& git -C $repoRoot status --porcelain | Out-String).Trim())
if ($dirty -and -not $AllowDirtyTree) {
    throw 'the working tree is dirty; commit the release source or pass -AllowDirtyTree for a non-release build'
}

# ---- toolchain ---------------------------------------------------------------
$dotnet = if ($env:OVERSEER_DOTNET) { $env:OVERSEER_DOTNET } else { $null }
if (-not $dotnet) {
    $onPath = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($onPath) { $dotnet = $onPath.Source }
}
if (-not $dotnet) {
    $workspaceSdk = Join-Path (Split-Path -Parent $repoRoot) '.tools/dotnet/dotnet.exe'
    if (Test-Path -LiteralPath $workspaceSdk) { $dotnet = $workspaceSdk }
}
if (-not $dotnet -or -not (Test-Path -LiteralPath $dotnet)) {
    throw 'dotnet was not found; set OVERSEER_DOTNET or follow the README SDK setup'
}

$dotnetRoot = Split-Path -Parent $dotnet
$sdkVersion = (& $dotnet --version).Trim()
Write-Step ('dotnet     ' + $dotnet + ' (' + $sdkVersion + ')')
Write-Step ('source     ' + $sourceShort + ' on ' + $sourceBranch + $(if ($dirty) { ' (dirty tree allowed)' } else { '' }))
Write-Step ('version    ' + $Version + ' ' + $Configuration + '/' + $RuntimeIdentifier)

$previousEnvironment = @{}
foreach ($name in 'DOTNET_CLI_TELEMETRY_OPTOUT', 'DOTNET_NOLOGO', 'DOTNET_CLI_USE_MSBUILD_SERVER', 'MSBUILDDISABLENODEREUSE') {
    $previousEnvironment[$name] = [Environment]::GetEnvironmentVariable($name)
}
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_CLI_USE_MSBUILD_SERVER = '0'
$env:MSBUILDDISABLENODEREUSE = '1'

try {
    # ---- restore configuration ------------------------------------------------
    # A scoped config avoids depending on a machine-wide NuGet configuration that
    # may be unreadable, and never commits absolute cache paths.
    $restoreArguments = @()
    if ($NuGetConfig) {
        if (-not (Test-Path -LiteralPath $NuGetConfig)) { throw ('NuGet config not found: ' + $NuGetConfig) }
        $restoreArguments = @('--configfile', (Resolve-Path -LiteralPath $NuGetConfig).Path)
    } elseif ($env:NUGET_PACKAGES) {
        $generatedNuGetConfig = Join-Path $outputRoot ('nuget-' + $stamp + '.config')
        $configXml = @"
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" protocolVersion="3" />
  </packageSources>
  <config>
    <add key="globalPackagesFolder" value="$env:NUGET_PACKAGES" />
  </config>
</configuration>
"@
        Set-Content -LiteralPath $generatedNuGetConfig -Value $configXml -Encoding utf8
        $restoreArguments = @('--configfile', $generatedNuGetConfig)
    }

    # ---- publish --------------------------------------------------------------
    Write-Step 'publishing self-contained folder output'
    $publishArguments = @(
        'publish', $appProject,
        '-c', $Configuration,
        '-r', $RuntimeIdentifier,
        '--self-contained', 'true',
        '-p:PublishSingleFile=false',
        '-p:PublishTrimmed=false',
        '-p:PublishAot=false',
        ('-p:Version=' + $Version),
        '--disable-build-servers',
        '-nodeReuse:false',
        '-m:1',
        '-o', $publishRoot
    ) + $restoreArguments
    Invoke-Checked $dotnet $publishArguments 'dotnet publish'

    # ---- verify the published application ------------------------------------
    $executableName = 'TheOverseer.App.exe'
    $requiredPublishFiles = @(
        $executableName,
        'TheOverseer.App.dll',
        'TheOverseer.App.runtimeconfig.json',
        'coreclr.dll',
        'hostfxr.dll',
        'hostpolicy.dll'
    )
    foreach ($required in $requiredPublishFiles) {
        $candidate = Join-Path $publishRoot $required
        if (-not (Test-Path -LiteralPath $candidate)) { throw ('publish output is missing ' + $required) }
    }

    $selfContained = Get-Content -LiteralPath (Join-Path $publishRoot 'TheOverseer.App.runtimeconfig.json') -Raw | ConvertFrom-Json
    $includedFrameworks = @()
    if ($selfContained.runtimeOptions.includedFrameworks) {
        foreach ($framework in $selfContained.runtimeOptions.includedFrameworks) {
            $includedFrameworks += ($framework.name + ' ' + $framework.version)
        }
    }

    if ($includedFrameworks.Count -eq 0) { throw 'publish output is not self-contained: no included frameworks were recorded' }

    $fileVersion = (Get-Item -LiteralPath (Join-Path $publishRoot $executableName)).VersionInfo.FileVersion
    if (-not $fileVersion -or -not $fileVersion.StartsWith($Version, [System.StringComparison]::Ordinal)) {
        throw ('published executable version ' + $fileVersion + ' does not match ' + $Version)
    }

    # ---- assemble the shipping payload --------------------------------------
    Write-Step 'assembling shipping payload'
    Copy-Item -Path (Join-Path $publishRoot '*') -Destination $payloadRoot -Recurse -Force

    $requiredPayload = @(
        @{ Source = (Join-Path $repoRoot 'README.md'); Target = 'README.md' },
        @{ Source = (Join-Path $repoRoot 'THIRD_PARTY_NOTICES.md'); Target = 'THIRD_PARTY_NOTICES.md' },
        @{ Source = (Join-Path $repoRoot 'third_party/codex-monitor-hud/LICENSE'); Target = 'licenses/codex-monitor-hud-MIT.txt' },
        @{ Source = (Join-Path $dotnetRoot 'LICENSE.txt'); Target = 'licenses/dotnet-LICENSE.txt' },
        @{ Source = (Join-Path $dotnetRoot 'ThirdPartyNotices.txt'); Target = 'licenses/dotnet-ThirdPartyNotices.txt' }
    )
    foreach ($item in $requiredPayload) {
        if (-not (Test-Path -LiteralPath $item.Source)) { throw ('required payload source is missing: ' + $item.Source) }
        $destination = Join-Path $payloadRoot $item.Target
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $destination) | Out-Null
        Copy-Item -LiteralPath $item.Source -Destination $destination -Force
    }

    $examplePack = Join-Path $repoRoot 'assets/voicepacks/example'
    if (-not (Test-Path -LiteralPath (Join-Path $examplePack 'manifest.json'))) { throw ('example pack is missing: ' + $examplePack) }
    $exampleTarget = Join-Path $payloadRoot 'examples/voicepacks/example'
    New-Item -ItemType Directory -Force -Path $exampleTarget | Out-Null
    Copy-Item -Path (Join-Path $examplePack '*') -Destination $exampleTarget -Recurse -Force

    # ---- payload audit -------------------------------------------------------
    $payloadFiles = Get-ChildItem -LiteralPath $payloadRoot -Recurse -File | Sort-Object FullName
    $payloadBytes = ($payloadFiles | Measure-Object -Property Length -Sum).Sum
    # Compare payload-relative segments: the staging tree itself lives under
    # artifacts/, so absolute paths would match the build location, not content.
    $forbidden = @('.git', 'artifacts', 'HANDOFF.md', 'TheOverseer.Tests', 'node_modules')
    foreach ($file in $payloadFiles) {
        $relative = $file.FullName.Substring($payloadRoot.Length + 1)
        foreach ($segment in ($relative -split '[\\/]')) {
            if ($forbidden -contains $segment) {
                throw ('payload contains forbidden content: ' + $relative)
            }
        }
    }

    foreach ($required in @(
        (Join-Path $payloadRoot $executableName),
        (Join-Path $payloadRoot 'README.md'),
        (Join-Path $payloadRoot 'THIRD_PARTY_NOTICES.md'),
        (Join-Path $payloadRoot 'licenses/codex-monitor-hud-MIT.txt'),
        (Join-Path $payloadRoot 'examples/voicepacks/example/manifest.json'))) {
        if (-not (Test-Path -LiteralPath $required)) { throw ('payload is missing ' + $required) }
    }

    # ---- archive -------------------------------------------------------------
    Write-Step ('writing ' + $archivePath)
    if (Test-Path -LiteralPath $archivePath) { Remove-Item -LiteralPath $archivePath -Force }
    $created = $false
    try {
        Add-Type -AssemblyName System.IO.Compression.FileSystem -ErrorAction Stop
        # includeBaseDirectory = false: the payload root must be the archive root.
        [System.IO.Compression.ZipFile]::CreateFromDirectory(
            $payloadRoot, $archivePath, [System.IO.Compression.CompressionLevel]::Optimal, $false)
        $created = $true
    } catch {
        Write-Warning ('zip creation through System.IO.Compression failed: ' + $_.Exception.Message)
        if (Test-Path -LiteralPath $archivePath) { Remove-Item -LiteralPath $archivePath -Force }
    }

    if (-not $created) {
        Compress-Archive -Path (Join-Path $payloadRoot '*') -DestinationPath $archivePath -CompressionLevel Optimal
    }

    $archive = [System.IO.Compression.ZipFile]::OpenRead($archivePath)
    try {
        $entries = @($archive.Entries | ForEach-Object { $_.FullName })
    } finally {
        $archive.Dispose()
    }

    foreach ($required in @(
        $executableName,
        'README.md',
        'THIRD_PARTY_NOTICES.md',
        'licenses/codex-monitor-hud-MIT.txt',
        'licenses/dotnet-LICENSE.txt',
        'examples/voicepacks/example/manifest.json',
        'examples/voicepacks/example/permission-1.wav')) {
        if ($entries -notcontains $required) { throw ('archive is missing ' + $required) }
    }

    $archiveHash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash
    $archiveBytes = (Get-Item -LiteralPath $archivePath).Length

    # ---- manifest ------------------------------------------------------------
    $manifest = [ordered]@{
        product = 'OVERSEER'
        project = 'THE OVERSEER PROJECT'
        version = $Version
        configuration = $Configuration
        runtimeIdentifier = $RuntimeIdentifier
        selfContained = $true
        publishSingleFile = $false
        publishTrimmed = $false
        publishAot = $false
        sourceCommit = $sourceCommit
        sourceBranch = $sourceBranch
        sourceTreeDirty = $dirty
        sdkVersion = $sdkVersion
        includedFrameworks = $includedFrameworks
        generatedAtUtc = (Get-Date).ToUniversalTime().ToString('o')
        archive = $ArchiveName
        archiveBytes = $archiveBytes
        archiveSha256 = $archiveHash
        archiveEntryCount = $entries.Count
        payloadFileCount = $payloadFiles.Count
        payloadBytes = $payloadBytes
        payloadFiles = @($payloadFiles | ForEach-Object { [ordered]@{ path = $_.FullName.Substring($payloadRoot.Length + 1).Replace('\', '/'); bytes = $_.Length } })
    }
    $manifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $manifestPath -Encoding utf8

    Write-Host ''
    Write-Host ('archive : ' + $archivePath)
    Write-Host ('sha256  : ' + $archiveHash)
    Write-Host ('bytes   : ' + $archiveBytes)
    Write-Host ('entries : ' + $entries.Count)
    Write-Host ('payload : ' + $payloadFiles.Count + ' files, ' + $payloadBytes + ' bytes')
    Write-Host ('manifest: ' + $manifestPath)
    Write-Host ('source  : ' + $sourceCommit + ' (' + $sourceBranch + ')')
    Write-Host 'publish complete'
} finally {
    foreach ($name in $previousEnvironment.Keys) {
        [Environment]::SetEnvironmentVariable($name, $previousEnvironment[$name])
    }

    if ($generatedNuGetConfig -and (Test-Path -LiteralPath $generatedNuGetConfig)) {
        Remove-ValidatedItem $generatedNuGetConfig $outputRoot
    }

    if (-not $KeepStaging) { Remove-ValidatedItem $stagingRoot $outputRoot }
    elseif (Test-Path -LiteralPath $stagingRoot) { Write-Host ('staging kept: ' + $stagingRoot) }
}
