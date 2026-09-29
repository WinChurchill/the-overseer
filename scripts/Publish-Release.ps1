#requires -Version 7.0
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
    [ValidateSet('Release')][string] $Configuration = 'Release',
    [ValidateSet('win-x64')][string] $RuntimeIdentifier = 'win-x64',
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
    Push-Location -LiteralPath $repoRoot
    try { & $file @arguments; $code = $LASTEXITCODE }
    finally { Pop-Location }
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
if ([IO.Path]::GetFileName($ArchiveName) -ne $ArchiveName -or $ArchiveName -notlike '*.zip') {
    throw 'ArchiveName must be a ZIP filename, not a path'
}
$pendingArchive = Join-Path $stagingRoot $ArchiveName
$manifestPath = Join-Path $outputRoot 'build-manifest.json'
$generatedNuGetConfig = $null

New-Item -ItemType Directory -Force -Path $outputRoot | Out-Null
New-Item -ItemType Directory -Force -Path $publishRoot | Out-Null
New-Item -ItemType Directory -Force -Path $payloadRoot | Out-Null

Write-Step ('repository ' + $repoRoot)
Write-Step ('output     ' + $outputRoot)

# ---- source identity ---------------------------------------------------------
# ---- release identity --------------------------------------------------------
# The release version may carry a semantic prerelease suffix; the numeric file and
# assembly version never can. Deriving both here is what lets "1.0.0-rc.1" ship with
# a valid "1.0.0.0" file version and a testable release identifier.
$numericVersion = ($Version -split '-')[0]
if ($numericVersion -notmatch '^\d+\.\d+\.\d+$') {
    throw ('Version must start with a numeric major.minor.patch version, for example 1.0.0 or 1.0.0-rc.1; got ' + $Version)
}

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
Push-Location -LiteralPath $repoRoot
try { $sdkVersion = (& $dotnet --version).Trim(); if ($LASTEXITCODE -ne 0) { throw 'dotnet --version failed' } }
finally { Pop-Location }
Write-Step ('dotnet     ' + $dotnet + ' (' + $sdkVersion + ')')
Write-Step ('source     ' + $sourceShort + ' on ' + $sourceBranch + $(if ($dirty) { ' (dirty tree allowed)' } else { '' }))
$fileVersion = $numericVersion + '.0'
$informationalVersion = $Version + '+' + $sourceShort
Write-Step ('version    ' + $Version + ' (file ' + $fileVersion + ') ' + $Configuration + '/' + $RuntimeIdentifier)

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
        $escapedPackages = [System.Security.SecurityElement]::Escape($env:NUGET_PACKAGES)
        $configXml = @"
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" protocolVersion="3" />
  </packageSources>
  <config>
    <add key="globalPackagesFolder" value="$escapedPackages" />
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
        ('-p:FileVersion=' + $fileVersion),
        ('-p:AssemblyVersion=' + $fileVersion),
        ('-p:InformationalVersion=' + $informationalVersion),
        # Map the checkout path out of the deterministic build metadata so the
        # shipped binaries and symbols cannot leak where the release was built.
        ('-p:PathMap=' + $repoRoot + '=/_/'),
        '-p:DeterministicSourcePaths=true',
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

    # Numeric file version exactly, and the release identifier plus source commit in
    # the product version. StartsWith($Version) was wrong: "1.0.0-rc.1" is not a
    # prefix of the numeric "1.0.0.0" the compiler writes.
    $versionInfo = (Get-Item -LiteralPath (Join-Path $publishRoot $executableName)).VersionInfo
    $publishedFileVersion = $versionInfo.FileVersion
    $publishedProductVersion = $versionInfo.ProductVersion
    if ($publishedFileVersion -ne $fileVersion) {
        throw ('published executable FileVersion ' + $publishedFileVersion + ' does not match ' + $fileVersion)
    }

    if ($publishedProductVersion -notlike ($Version + '*')) {
        throw ('published executable ProductVersion ' + $publishedProductVersion + ' does not start with ' + $Version)
    }

    if ($publishedProductVersion -notlike ('*' + $sourceShort + '*')) {
        throw ('published executable ProductVersion ' + $publishedProductVersion + ' does not name source ' + $sourceShort)
    }

    # ---- assemble the shipping payload --------------------------------------
    Write-Step 'assembling shipping payload'
    Copy-Item -Path (Join-Path $publishRoot '*') -Destination $payloadRoot -Recurse -Force

    $requiredPayload = @(
        @{ Source = (Join-Path $repoRoot 'LICENSE'); Target = 'LICENSE' },
        @{ Source = (Join-Path $repoRoot 'README.md'); Target = 'README.md' },
        @{ Source = (Join-Path $repoRoot 'CHANGELOG.md'); Target = 'CHANGELOG.md' },
        @{ Source = (Join-Path $repoRoot 'THIRD_PARTY_NOTICES.md'); Target = 'THIRD_PARTY_NOTICES.md' },
        @{ Source = (Join-Path $repoRoot 'third_party/codex-monitor-hud/LICENSE'); Target = 'licenses/codex-monitor-hud-MIT.txt' }
    )
    # SDK notices need not match the runtime version selected by restore. Read
    # the exact redistributed runtime packages, using their actual versions.
    $assets = Get-Content -LiteralPath (Join-Path $repoRoot 'src/TheOverseer.App/obj/project.assets.json') -Raw | ConvertFrom-Json
    foreach ($framework in $selfContained.runtimeOptions.includedFrameworks) {
        $packageId = ($framework.name + '.Runtime.' + $RuntimeIdentifier).ToLowerInvariant()
        $packageRoot = $null
        foreach ($folder in $assets.packageFolders.PSObject.Properties.Name) {
            $candidate = Join-Path $folder ($packageId + '/' + $framework.version)
            if (Test-Path -LiteralPath $candidate) { $packageRoot = $candidate; break }
        }
        if (-not $packageRoot) { throw ('Cannot locate redistributed runtime license: ' + $packageId + '/' + $framework.version) }
        if ($framework.name -eq 'Microsoft.NETCore.App') {
            $requiredPayload += @{ Source = (Join-Path $packageRoot 'LICENSE.TXT'); Target = 'licenses/dotnet-LICENSE.txt' }
            $requiredPayload += @{ Source = (Join-Path $packageRoot 'THIRD-PARTY-NOTICES.TXT'); Target = 'licenses/dotnet-ThirdPartyNotices.txt' }
        } elseif ($framework.name -eq 'Microsoft.WindowsDesktop.App') {
            $requiredPayload += @{ Source = (Join-Path $packageRoot 'LICENSE'); Target = 'licenses/windowsdesktop-LICENSE.txt' }
        } else { throw ('Unrecognized redistributed framework: ' + $framework.name) }
    }
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

    # ---- optional integrations ----------------------------------------------
    # The harness bridges ship as their own assets, with notices, reversible
    # installers and their tests, so a user never needs this checkout. Their spool
    # lives under the data directory (%LOCALAPPDATA%\OVERSEER), not under the
    # application folder, so installing a later version cannot move or orphan it.
    $integrationRoot = Join-Path $repoRoot 'integrations'
    if (-not (Test-Path -LiteralPath $integrationRoot)) { throw ('integrations directory is missing: ' + $integrationRoot) }
    $integrationTarget = Join-Path $payloadRoot 'integrations'
    New-Item -ItemType Directory -Force -Path $integrationTarget | Out-Null
    Copy-Item -Path (Join-Path $integrationRoot '*') -Destination $integrationTarget -Recurse -Force
    $integrationFiles = @(Get-ChildItem -LiteralPath $integrationTarget -Recurse -File | Sort-Object FullName)
    $integrationNames = @($integrationFiles | ForEach-Object { $_.FullName.Substring($integrationTarget.Length + 1).Replace('\', '/') })

    # ---- machine-readable release identity ----------------------------------
    $release = [ordered]@{
        projectLicense = 'MIT'
        licenseScope = 'Original code/docs; supplied branding exclusions and upstream notices are in THIRD_PARTY_NOTICES.md.'
        product = 'OVERSEER'
        project = 'THE OVERSEER PROJECT'
        version = $Version
        fileVersion = $fileVersion
        informationalVersion = $informationalVersion
        sourceCommit = $sourceCommit
        sourceShortCommit = $sourceShort
        sourceBranch = $sourceBranch
        sourceTreeDirty = $dirty
        sdkVersion = $sdkVersion
        runtimeIdentifier = $RuntimeIdentifier
        configuration = $Configuration
        selfContained = $true
        generatedAtUtc = (Get-Date).ToUniversalTime().ToString('o')
        integrations = $integrationNames
        dataDirectoryNote = 'Runtime data (settings, state, logs and bridge spools) stays under the data directory (%LOCALAPPDATA%\OVERSEER unless --data-directory is passed) and is not stored in this package folder.'
    }
    $release | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $payloadRoot 'release.json') -Encoding utf8

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
        (Join-Path $payloadRoot 'LICENSE'),
        (Join-Path $payloadRoot 'README.md'),
        (Join-Path $payloadRoot 'THIRD_PARTY_NOTICES.md'),
        (Join-Path $payloadRoot 'release.json'),
        (Join-Path $payloadRoot 'integrations/deepseek-harness/README.md'),
        (Join-Path $payloadRoot 'integrations/deepseek-harness/src/index.js'),
        (Join-Path $payloadRoot 'integrations/claude-code/README.md'),
        (Join-Path $payloadRoot 'integrations/claude-code/install.mjs'),
        (Join-Path $payloadRoot 'licenses/codex-monitor-hud-MIT.txt'),
        (Join-Path $payloadRoot 'examples/voicepacks/example/manifest.json'))) {
        if (-not (Test-Path -LiteralPath $required)) { throw ('payload is missing ' + $required) }
    }

    # The build machine's checkout path must not be embedded in shipped binaries
    # or symbols; PathMap above is what keeps this true.
    $leaking = New-Object System.Collections.ArrayList
    foreach ($file in $payloadFiles) {
        if ($file.Extension -notin @('.dll', '.exe', '.pdb', '.json')) { continue }
        $text = [System.Text.Encoding]::ASCII.GetString([System.IO.File]::ReadAllBytes($file.FullName))
        if ($text.Contains($repoRoot)) { [void]$leaking.Add($file.Name) }
    }

    if ($leaking.Count -gt 0) {
        throw ('payload embeds the build checkout path in: ' + (($leaking | Select-Object -First 5) -join ', '))
    }

    # ---- archive -------------------------------------------------------------
    Write-Step ('writing ' + $archivePath)
    $created = $false
    try {
        Add-Type -AssemblyName System.IO.Compression.FileSystem -ErrorAction Stop
        # includeBaseDirectory = false: the payload root must be the archive root.
        [System.IO.Compression.ZipFile]::CreateFromDirectory(
            $payloadRoot, $pendingArchive, [System.IO.Compression.CompressionLevel]::Optimal, $false)
        $created = $true
    } catch {
        Write-Warning ('zip creation through System.IO.Compression failed: ' + $_.Exception.Message)
        if (Test-Path -LiteralPath $pendingArchive) { Remove-ValidatedItem $pendingArchive $stagingRoot }
    }

    if (-not $created) {
        Compress-Archive -Path (Join-Path $payloadRoot '*') -DestinationPath $pendingArchive -CompressionLevel Optimal
    }

    $archive = [System.IO.Compression.ZipFile]::OpenRead($pendingArchive)
    try {
        $entries = @($archive.Entries | ForEach-Object { $_.FullName })
    } finally {
        $archive.Dispose()
    }

    foreach ($required in @(
        $executableName,
        'LICENSE',
        'README.md',
        'THIRD_PARTY_NOTICES.md',
        'release.json',
        'licenses/codex-monitor-hud-MIT.txt',
        'licenses/dotnet-LICENSE.txt',
        'examples/voicepacks/example/manifest.json',
        'examples/voicepacks/example/permission-1.wav',
        'integrations/deepseek-harness/README.md',
        'integrations/deepseek-harness/NOTICE.md',
        'integrations/deepseek-harness/src/index.js',
        'integrations/claude-code/README.md',
        'integrations/claude-code/NOTICE.md',
        'integrations/claude-code/install.mjs',
        'integrations/claude-code/uninstall.mjs')) {
        if ($entries -notcontains $required) { throw ('archive is missing ' + $required) }
    }

    $archiveHash = (Get-FileHash -LiteralPath $pendingArchive -Algorithm SHA256).Hash
    $archiveBytes = (Get-Item -LiteralPath $pendingArchive).Length
    # Preserve the preceding release until the replacement archive is validated.
    Move-Item -LiteralPath $pendingArchive -Destination $archivePath -Force
    Set-Content -LiteralPath ($archivePath + '.sha256') -Value ($archiveHash + '  ' + $ArchiveName) -Encoding ascii

    # ---- manifest ------------------------------------------------------------
    $manifest = [ordered]@{
        product = 'OVERSEER'
        project = 'THE OVERSEER PROJECT'
        version = $Version
        fileVersion = $fileVersion
        informationalVersion = $informationalVersion
        productVersion = $publishedProductVersion
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
        integrationFileCount = $integrationFiles.Count
        integrations = $integrationNames
        payloadFiles = @($payloadFiles | ForEach-Object { [ordered]@{ path = $_.FullName.Substring($payloadRoot.Length + 1).Replace('\', '/'); bytes = $_.Length } })
    }
    $manifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $manifestPath -Encoding utf8

    Write-Host ''
    Write-Host ('archive : ' + $archivePath)
    Write-Host ('sha256  : ' + $archiveHash)
    Write-Host ('bytes   : ' + $archiveBytes)
    Write-Host ('entries : ' + $entries.Count)
    Write-Host ('payload : ' + $payloadFiles.Count + ' files, ' + $payloadBytes + ' bytes')
    Write-Host ('assets  : ' + $integrationFiles.Count + ' integration files')
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
