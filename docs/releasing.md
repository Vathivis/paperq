# Releasing and signing

## Automated validation

`.github/workflows/ci.yml` runs for pull requests, pushes to `main`, and manual dispatches. It uses matching hosted runners to build, run the executable test harness, produce each Native AOT target, and smoke-test its `--version` output:

- Windows x64 on `windows-2025`
- Windows ARM64 on `windows-11-arm`
- Linux x64 on `ubuntu-24.04`
- Linux ARM64 on `ubuntu-24.04-arm`

The Windows ARM64 hosted runner is currently in GitHub public preview. `paperq` is a public repository, so all four standard runners are available without adding self-hosted machines.

## Creating a release

The release workflow is tag-driven and accepts only `vMAJOR.MINOR.PATCH`. It also requires the tag version to match `<Version>` in `src/Paperq/Paperq.csproj`.

1. Update `<Version>` in `src/Paperq/Paperq.csproj`.
2. Build and run the tests locally.
3. Commit and push the version change, then let CI pass.
4. Create and push the matching tag.

For example:

```powershell
$version = "1.0.0"
git tag -a "v$version" -m "paperq $version"
git push origin "v$version"
```

The release workflow rebuilds and tests all four runtime targets. It creates x64 and ARM64 ZIPs for Windows, plus `tar.gz` and `.deb` packages for both Linux architectures, generates `SHA256SUMS`, creates GitHub artifact attestations, and creates the GitHub Release. It fails rather than replacing an existing release.

No workflow submits anything to the WinGet community repository. Submission remains a reviewed, manual step after testing the exact release asset in Windows Sandbox.

## Verifying a release

Verify the checksums after downloading all seven files from a release:

```powershell
$checksumLines = @(Get-Content -LiteralPath .\SHA256SUMS)
if ($checksumLines.Count -ne 6) {
    throw "Expected six release checksums, found $($checksumLines.Count)."
}

foreach ($line in $checksumLines) {
    if ($line -notmatch '^([0-9a-fA-F]{64})  (.+)$') {
        throw "Invalid SHA256SUMS entry: $line"
    }

    $expectedHash = $Matches[1].ToLowerInvariant()
    $fileName = $Matches[2]
    if ([System.IO.Path]::GetFileName($fileName) -ne $fileName) {
        throw "Unsafe release filename in SHA256SUMS: $fileName"
    }

    $assetPath = Join-Path $PWD $fileName
    if (-not (Test-Path -LiteralPath $assetPath -PathType Leaf)) {
        throw "Missing release asset: $fileName"
    }

    $actualHash = (Get-FileHash -LiteralPath $assetPath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actualHash -ne $expectedHash) {
        throw "Checksum mismatch for $fileName"
    }
}
```

GitHub's cryptographic build-provenance attestation can also be verified with the GitHub CLI:

```powershell
$version = "1.0.0"
gh attestation verify ".\paperq-$version-win-x64.zip" --repo Vathivis/paperq
```

An [artifact attestation](https://docs.github.com/en/actions/how-tos/secure-your-work/use-artifact-attestations/use-artifact-attestations) proves which GitHub workflow, repository, commit, and event produced a file. It is not a Windows Authenticode signature and does not establish SmartScreen reputation.

## Does WinGet require signing?

Not for this distribution model. WinGet [supports ZIP and portable installers](https://learn.microsoft.com/en-us/windows/package-manager/winget/#supported-installer-formats) and requires the manifest's `InstallerSha256` to match the immutable release asset. Its [submission validation](https://learn.microsoft.com/en-us/windows/package-manager/package/repository) also evaluates packages for ecosystem safety. The community-repository rules do not require an Authenticode signature for a portable executable.

Signing is nevertheless useful: Windows can show a verified publisher, and signed releases can build SmartScreen reputation. A new certificate or signing identity does not guarantee that initial SmartScreen warnings disappear immediately.

Do not use a self-signed certificate for public releases, and do not commit a PFX file or password to this repository.

## Recommended signing options

Choose a signing identity before adding a CI signing step:

1. **SignPath Foundation** offers free managed OV-level signing to qualifying open-source projects. The repository will need an explicit open-source license before it can qualify.
2. **Azure Artifact Signing** (formerly Trusted Signing) is Microsoft's managed signing service and [integrates with GitHub Actions](https://learn.microsoft.com/en-us/azure/artifact-signing/how-to-signing-integrations) without a hardware token. It requires identity validation, an Azure signing account and certificate profile, and an eligible region/account type. Microsoft currently lists EU organizations as eligible, but individual accounts only in the United States and Canada.
3. **A traditional OV certificate** is the fallback when a managed service is not suitable. Modern publicly trusted code-signing private keys generally live in approved hardware or a managed HSM, which makes a physical token awkward on a GitHub-hosted runner.

Microsoft's current [code-signing options comparison](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/code-signing-options) covers the costs, eligibility, and SmartScreen behavior of these choices.

Once a provider is selected, the Windows release job should be extended in this order:

1. publish `paperq.exe`;
2. authenticate to the managed signing service using GitHub OIDC, or access the protected signing key;
3. Authenticode-sign and RFC 3161 timestamp `paperq.exe` with SHA-256;
4. fail the job unless `signtool verify /pa /v paperq.exe` succeeds;
5. smoke-test, ZIP, checksum, attest, and publish the signed executable.

The provider-specific action and identity settings are intentionally not present yet. Adding placeholders would make releases fail and could encourage unsafe secret handling.

## Preparing the WinGet submission

After a new release succeeds, copy the latest tracked manifest set under `winget-manifests/manifests/v/Vathivis/paperq/` into a directory named for the new version, then:

1. update every `PackageVersion`, release-note URL, and Windows asset URL;
2. set each `InstallerSha256` from the immutable published archive;
3. preserve one portable installer entry per architecture and the nested executable name `paperq.exe`;
4. validate the manifest and test installation, upgrade, invocation, and uninstall in Windows Sandbox;
5. submit the reviewed manifest to `microsoft/winget-pkgs`.

Signing can be added before or after the first WinGet submission. If the executable changes from unsigned to signed, publish it as a new version because the release asset and its SHA-256 must remain immutable.
