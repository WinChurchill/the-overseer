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
    [string] $SigningCertificateThumbprint,
    [ValidateSet('CurrentUser', 'LocalMachine')][string] $SigningCertificateStore = 'CurrentUser',
    [string] $SignToolPath,
    [string] $SigningTimestampServer,
    [switch] $AllowDirtyTree,
    [switch] $KeepStaging
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

# Signing is explicit and happens before any payload hashes or ZIP are produced.
# Never generate a test certificate or modify a trust store to satisfy this gate.
$signingCertificate = $null
if ($SigningCertificateThumbprint) {
    $thumbprint = $SigningCertificateThumbprint.Replace(' ', '')
    if ($thumbprint -notmatch '^[0-9a-fA-F]{40}$') { throw 'A certificate thumbprint must contain 40 hexadecimal characters.' }
    $signingCertificate = Get-Item -LiteralPath ('Cert:\' + $SigningCertificateStore + '\My\' + $thumbprint) -ErrorAction Stop
    if (-not $signingCertificate.HasPrivateKey -or $signingCertificate.NotAfter -le (Get-Date) -or
        $signingCertificate.NotBefore -gt (Get-Date) -or
        @($signingCertificate.EnhancedKeyUsageList | Where-Object { $_.ObjectId -eq '1.3.6.1.5.5.7.3.3' }).Count -eq 0) {
        throw 'The selected identity must be a currently valid code-signing certificate with its private key.'
    }
    if (-not $SignToolPath -or -not (Test-Path -LiteralPath $SignToolPath -PathType Leaf)) { throw 'Supply the installed Windows SDK signtool.exe path.' }
    $timestampUri = $null
    if (-not [Uri]::TryCreate($SigningTimestampServer, [UriKind]::Absolute, [ref]$timestampUri) -or
        $timestampUri.Scheme -ne 'https' -or $timestampUri.UserInfo -or $timestampUri.Fragment) {
        throw 'Supply an HTTPS RFC3161 timestamp server without credentials or fragment.'
    }
} elseif ($SignToolPath -or $SigningTimestampServer) {
    throw 'Signing options require a signing certificate thumbprint.'
}

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
        @{ Source = (Join-Path $repoRoot 'PRIVACY.md'); Target = 'PRIVACY.md' },
        @{ Source = (Join-Path $repoRoot 'SUPPORT.md'); Target = 'SUPPORT.md' },
        @{ Source = (Join-Path $repoRoot 'SECURITY.md'); Target = 'SECURITY.md' },
        @{ Source = (Join-Path $repoRoot 'CONTRIBUTING.md'); Target = 'CONTRIBUTING.md' },
        @{ Source = (Join-Path $repoRoot 'AGENTS.md'); Target = 'AGENTS.md' },
        @{ Source = (Join-Path $repoRoot 'docs/IMPLEMENTATION_STATE.md'); Target = 'docs/IMPLEMENTATION_STATE.md' },
        @{ Source = (Join-Path $repoRoot 'docs/RELEASE_READINESS.md'); Target = 'docs/RELEASE_READINESS.md' },
        @{ Source = (Join-Path $repoRoot 'docs/UPGRADE_AND_ROLLBACK.md'); Target = 'docs/UPGRADE_AND_ROLLBACK.md' },
        @{ Source = (Join-Path $repoRoot 'docs/TESTER_HANDOFF.md'); Target = 'docs/TESTER_HANDOFF.md' },
        @{ Source = (Join-Path $repoRoot 'docs/images/hud-dark.png'); Target = 'docs/images/hud-dark.png' },
        @{ Source = (Join-Path $repoRoot 'docs/images/hud-light.png'); Target = 'docs/images/hud-light.png' },
        @{ Source = (Join-Path $repoRoot 'site/assets/hud-dark.png'); Target = 'site/assets/hud-dark.png' },
        @{ Source = (Join-Path $repoRoot 'site/assets/hud-light.png'); Target = 'site/assets/hud-light.png' },
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

    # Keep user guides/images usable offline. Source-only Markdown links point to
    # the recorded source revision instead of nonexistent files in the archive.
    $repoPrefix = $repoRoot.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    foreach ($doc in Get-ChildItem -LiteralPath $payloadRoot -Recurse -Filter '*.md' -File) {
        $relativeDoc = [IO.Path]::GetRelativePath($payloadRoot, $doc.FullName)
        $sourceDirectory = Split-Path -Parent (Join-Path $repoRoot $relativeDoc)
        $text = Get-Content -LiteralPath $doc.FullName -Raw
        $text = [regex]::Replace($text, '\[([^\]]+)\]\(([^\s)]+)\)', {
            param($match)
            $target = $match.Groups[2].Value
            if ($target -match '^(?:[a-z][a-z0-9+.-]*:|#|//)') { return $match.Value }
            $parts = $target -split '#', 2
            $localTarget = Join-Path $doc.DirectoryName $parts[0]
            if (Test-Path -LiteralPath $localTarget) { return $match.Value }
            $sourceTarget = [IO.Path]::GetFullPath((Join-Path $sourceDirectory $parts[0]))
            if (-not $sourceTarget.StartsWith($repoPrefix, [StringComparison]::OrdinalIgnoreCase) -or
                -not (Test-Path -LiteralPath $sourceTarget)) {
                throw ('broken documentation link in ' + $relativeDoc + ': ' + $target)
            }
            $relativeSource = [IO.Path]::GetRelativePath($repoRoot, $sourceTarget).Replace('\', '/')
            $url = 'https://github.com/WinChurchill/the-overseer/blob/' + $sourceCommit + '/' + $relativeSource
            if ($parts.Count -gt 1) { $url += '#' + $parts[1] }
            return '[' + $match.Groups[1].Value + '](' + $url + ')'
        })
        Set-Content -LiteralPath $doc.FullName -Value $text -Encoding utf8 -NoNewline
    }

    # ---- machine-readable release identity ----------------------------------
    $ownedBinaries = @('TheOverseer.App.exe', 'TheOverseer.App.dll', 'TheOverseer.Core.dll', 'TheOverseer.UI.dll')
    $binarySignatures = [Collections.Generic.List[object]]::new()
    foreach ($binary in $ownedBinaries) {
        $signaturePath = Join-Path $payloadRoot $binary
        if ($signingCertificate) {
            $signArguments = @('sign', '/sha1', $signingCertificate.Thumbprint, '/s', 'My', '/fd', 'SHA256',
                '/tr', $SigningTimestampServer, '/td', 'SHA256')
            if ($SigningCertificateStore -eq 'LocalMachine') { $signArguments += '/sm' }
            $signArguments += $signaturePath
            Invoke-Checked $SignToolPath $signArguments ('Authenticode signing: ' + $binary)
            Invoke-Checked $SignToolPath @('verify', '/pa', '/all', '/v', $signaturePath) ('Authenticode verification: ' + $binary)
        }
        $signature = Get-AuthenticodeSignature -LiteralPath $signaturePath
        if ($signingCertificate -and ($signature.Status -ne 'Valid' -or -not $signature.TimeStamperCertificate -or
            $signature.SignerCertificate.Thumbprint -ne $signingCertificate.Thumbprint)) {
            throw ('Signed payload must have a valid matching Authenticode identity and a timestamp: ' + $binary)
        }
        $binarySignatures.Add([ordered]@{ file = $binary; status = $signature.Status.ToString();
            signerThumbprint = if ($signature.SignerCertificate) { $signature.SignerCertificate.Thumbprint } else { $null };
            timestamped = [bool]$signature.TimeStamperCertificate })
    }
    $signing = [ordered]@{
        status = $binarySignatures[0].status
        signerThumbprint = $binarySignatures[0].signerThumbprint
        timestamped = $binarySignatures[0].timestamped
        files = $binarySignatures.ToArray()
    }
    $release = [ordered]@{
        projectLicense = 'MIT'
        licenseScope = 'Original code, docs and generated artwork: MIT; upstream and runtime terms are in THIRD_PARTY_NOTICES.md.'
        product = 'OVERSEER'
        project = 'THE OVERSEER PROJECT'
        version = $Version
        fileVersion = $fileVersion
        informationalVersion = $informationalVersion
        sourceCommit = $sourceCommit
        sourceShortCommit = $sourceShort
        sourceBranch = $sourceBranch
        sourceTreeDirty = $dirty
        signing = $signing
        sdkVersion = $sdkVersion
        runtimeIdentifier = $RuntimeIdentifier
        configuration = $Configuration
        selfContained = $true
        generatedAtUtc = (Get-Date).ToUniversalTime().ToString('o')
        integrations = $integrationNames
        dataDirectoryNote = 'App settings, state and logs use %LOCALAPPDATA%\OVERSEER unless --data-directory is passed. Bridge producers use their own documented spool settings; --data-directory does not redirect them. Runtime data is not stored in this package folder.'
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
        (Join-Path $payloadRoot 'docs/TESTER_HANDOFF.md'),
        (Join-Path $payloadRoot 'docs/images/hud-dark.png'),
        (Join-Path $payloadRoot 'docs/images/hud-light.png'),
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
        'integrations/deepseek-harness/LICENSE',
        'integrations/deepseek-harness/src/index.js',
        'integrations/claude-code/README.md',
        'integrations/claude-code/NOTICE.md',
        'integrations/claude-code/LICENSE',
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
        signing = $signing
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
