# Releasing and signing

## Automated validation

`.github/workflows/ci.yml` runs for pull requests, pushes to `main`, and manual dispatches. It uses matching hosted runners to build, run the executable test harness, produce each Native AOT target, and smoke-test its `--version` output:

- Windows x64 on `windows-2025`
- Linux x64 on `ubuntu-24.04`
- Linux ARM64 on `ubuntu-24.04-arm`

The ARM64 hosted runner is currently a GitHub public-preview runner. `paperq` is a public repository, so the standard runner is available without adding a self-hosted machine.

## Creating a release

The release workflow is tag-driven and accepts only `vMAJOR.MINOR.PATCH`. It also requires the tag version to match `<Version>` in `src/Paperq/Paperq.csproj`.

1. Update `<Version>` in `src/Paperq/Paperq.csproj`.
2. Build and run the tests locally.
3. Commit and push the version change, then let CI pass.
4. Create and push the matching tag.

For example:

```powershell
git tag -a v0.1.0 -m "paperq 0.1.0"
git push origin v0.1.0
```

The release workflow rebuilds and tests on all three architectures. It publishes only the executable, creates a ZIP for Windows and `tar.gz` archives for Linux, generates `SHA256SUMS`, creates GitHub artifact attestations, and creates the GitHub Release. It fails rather than replacing an existing release.

No workflow submits anything to the WinGet community repository. Submission remains a reviewed, manual step after testing the exact release asset in Windows Sandbox.

## Verifying a release

Verify the checksums after downloading all four files from a release:

```powershell
$expected = Get-Content .\SHA256SUMS
Get-FileHash .\paperq-*.zip, .\paperq-*.tar.gz -Algorithm SHA256
```

GitHub's cryptographic build-provenance attestation can also be verified with the GitHub CLI:

```powershell
gh attestation verify .\paperq-0.1.0-win-x64.zip --repo Vathivis/paperq
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

After the first release succeeds:

1. choose the final WinGet `PackageIdentifier` and add an explicit repository license;
2. use the immutable GitHub Release URL for the Windows ZIP;
3. declare the ZIP's nested portable executable as `paperq.exe`;
4. set `InstallerSha256` from the published archive;
5. validate the manifest and test installation, upgrade, invocation, and uninstall in Windows Sandbox;
6. submit the reviewed manifest to `microsoft/winget-pkgs`.

Signing can be added before or after the first WinGet submission. If the executable changes from unsigned to signed, publish it as a new version because the release asset and its SHA-256 must remain immutable.
