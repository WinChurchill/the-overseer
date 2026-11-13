# Verify a release download

The alpha is an **unsigned Windows x64 ZIP**. Windows may show an unknown-publisher
or SmartScreen warning. A checksum or build attestation does not give the executable
a trusted Windows publisher signature. Keep Windows security protections enabled.

## Check the ZIP before extracting

Download the app ZIP and its adjacent `.zip.sha256` file from the same official
release. In PowerShell, replace the version below with the downloaded version:

```powershell
$archive = 'OVERSEER-1.0.0-alpha.6-win-x64.zip'
$expected = ((Get-Content -LiteralPath "$archive.sha256" -Raw).Trim() -split '\s+')[0]
$actual = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash
if ($expected -notmatch '^[a-fA-F0-9]{64}$' -or $actual -ine $expected) {
    throw 'Checksum mismatch: do not run this download.'
}
'Checksum matches.'
```

This detects changed download bytes. A checksum downloaded beside the ZIP does not
independently prove who built it.

## Optional: verify GitHub build provenance

A build attestation is GitHub's signed receipt linking the ZIP hash to its source
revision and Actions workflow. Once available for a public release, install the
[GitHub CLI](https://cli.github.com/) and run:

```powershell
gh attestation verify .\OVERSEER-1.0.0-alpha.6-win-x64.zip --repo WinChurchill/the-overseer --signer-workflow WinChurchill/the-overseer/.github/workflows/package.yml
```

Require successful verification and compare the reported source revision with the
release notes. This establishes build origin; it does not establish that the code
is safe or remove Windows warnings.

The package workflow attests newly built ZIPs only when the repository is public.
On free GitHub plans, private builds cannot use GitHub's built-in attestations.
Private candidates include checksums and a build manifest; `attestation-status.json`
records the skipped attestation explicitly. No attestation is claimed retroactively
for an older ZIP. A missing attestation for an advertised attested release is a
verification failure, not a reason to skip the check.

[GitHub documentation](https://docs.github.com/en/actions/how-tos/secure-your-work/use-artifact-attestations/use-artifact-attestations)
