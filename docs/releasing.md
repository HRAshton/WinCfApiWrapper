# Releasing and provenance

This guide documents the existing release pipeline. Releases and any exceptional recovery are maintainer activities, not part of a typical contributor pull request.

## Sources and cadence

- `master` is protected by repository rulesets requiring pull requests and CI; branch rules are the source of truth for approval/bypass requirements.
- `GitVersion.yml` produces the version from repository history and release tags.
- `.github/workflows/release.yml` schedules publication on Thursdays in the `Europe/Belgrade` timezone, and also supports a manual `workflow_dispatch`.
- Publication is skipped when there are no qualifying new commits; unexpected or partially completed releases should be recovered through the release workflow rather than repacked manually.

## Build and publish stages

1. **Resolve:** Determine intended commit, version, tag, and whether there is new content to publish.
2. **Build:** `.github/workflows/attested-release-build.yml` checks out the exact commit, executes unit, offline integration, and live tests, packs NuGet binaries/symbols, and issues GitHub artifact attestations.
3. **Stage/verify:** `scripts/release-transaction.ps1` stages draft release assets, hashes and provenance, and verifies the bundle against the commit and package bytes.
4. **Publish:** The `nuget` GitHub environment obtains a short-lived credential via NuGet Trusted Publishing and publishes the original package.
5. **Reconcile:** The workflow verifies the repository-signed NuGet package content against the attested original, then finalizes the GitHub Release.

Release assets include `.nupkg`, `.snupkg`, a `.intoto.jsonl` attestation bundle, and `release-manifest.json`. Sigstore/in-toto provenance establishes the source/workflow identity and package digest; it is **not** a claim of third-party certification to a particular SLSA level.

## Verify an existing release

Download the artifacts for a matching tag from [GitHub Releases](https://github.com/HRAshton/WinCfApiWrapper/releases). Read the manifest's `commit` field and verify the exact package bytes:

```powershell
gh attestation verify WinCfApiWrapper.<version>.nupkg `
  --bundle WinCfApiWrapper.<version>.intoto.jsonl `
  --repo HRAshton/WinCfApiWrapper `
  --signer-workflow HRAshton/WinCfApiWrapper/.github/workflows/attested-release-build.yml `
  --source-ref refs/heads/master `
  --source-digest <manifest-commit>
```

Repeat for `.snupkg`. Inspect the manifest and release tag, and check that the verified source digest matches the expected commit. See the [root README](../README.md) for the current verification procedure.

## Failed or partial publication

The release script is designed to reconcile draft tags/releases and an already-uploaded NuGet package. Re-run the release workflow following the documented recovery path; do not move immutable tags, replace packages, fabricate attestations, or ignore hash mismatches. Investigate failed provenance verification rather than attempting an unsigned/manual alternative.

## Maintainer checklist

- Confirm the branch is in a releasable state with required CI and CodeQL results.
- Review Renovate changes, especially major updates and changes to build/release dependencies.
- Confirm the `nuget` environment and Trusted Publishing configuration.
- After publication, verify that the GitHub Release is finalized, the NuGet package is indexed, and the published package matches the attested original.
- If there is a security fix, coordinate disclosure per [SECURITY.md](../SECURITY.md).
