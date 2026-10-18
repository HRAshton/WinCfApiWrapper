# Releasing and provenance

Maintainers use the release workflow for publication and recovery.

## Sources and cadence

- `master` is protected by repository rulesets requiring pull requests and CI; branch rules are the source of truth for approval/bypass requirements.
- `GitVersion.yml` produces the version from repository history and release tags.
- `.github/workflows/release.yml` schedules publication on Thursdays in the `Europe/Belgrade` timezone, and also supports a manual `workflow_dispatch`.
- Publication is skipped when there are no qualifying new commits; unexpected or partially completed releases should be recovered through the release workflow rather than repacked manually.

## Build and publish stages

1. **Resolve:** Determine intended commit, version, tag, and whether there is new content to publish.
2. **Build:** `.github/workflows/attested-release-build.yml` checks out the exact commit, executes unit, archive stress, offline integration, and live tests, packs NuGet binaries/symbols, and issues GitHub artifact attestations. Archive stress tests are enabled on both runtimes and must pass before packaging.
3. **Stage/verify:** `scripts/release-transaction.ps1` stages draft release assets, hashes and provenance, and verifies the bundle against the commit and package bytes.
4. **Publish:** The `nuget` GitHub environment obtains a short-lived credential via NuGet Trusted Publishing and publishes the original package.
5. **Reconcile:** The workflow verifies the repository-signed NuGet package content against the attested original, then finalizes the GitHub Release.

Release assets include `.nupkg`, `.snupkg`, a `.intoto.jsonl` attestation bundle, and `release-manifest.json`. Provenance identifies the source commit, build workflow, and package digests; it does not certify a SLSA level.

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

Repeat for `.snupkg`. Inspect the manifest and release tag, and check that the verified source digest matches the expected commit. The published NuGet README comes from the repository-root `README.md` at that same source commit; packaging does not fetch documentation from the network. The guide links in that README point to GitHub so they also work on nuget.org.

Existing `v0.1.0` has attestations in GitHub's attestation store, but predates the downloadable `.intoto.jsonl` asset. For that release, omit `--bundle` to verify against GitHub's attestation store.

## Security releases and release notes

Security fixes may be published outside the Thursday schedule using the
maintainer-controlled manual release workflow. Do not postpone an urgent fix
solely to meet the regular cadence. Follow
[SECURITY.md](../SECURITY.md) when coordinating reports and disclosure.

For public security fixes, release notes should identify:

- Affected versions, the first fixed version, and concrete upgrade instructions.
- The impact and conditions needed to trigger the issue, without publishing
  exploit details before coordinated disclosure.
- The linked GitHub Security Advisory (and CVE identifier, if assigned) once
  disclosure is public, plus any required workarounds.
- How to verify the fixed NuGet artifact and its build provenance.

For non-security releases, publish a concise list of meaningful user-visible
functional changes, breaking changes, and migration requirements. GitHub's
generated changelog can supplement, but should not replace, this summary.
Never overwrite an existing NuGet version or move an immutable release tag;
publish a new version for a corrected release.

## Failed or partial publication

The release script is designed to reconcile draft tags/releases and an already-uploaded NuGet package. Re-run the release workflow following the documented recovery path; do not move immutable tags, replace packages, fabricate attestations, or ignore hash mismatches. Investigate failed provenance verification rather than attempting an unsigned/manual alternative.

## Maintainer checklist

- Confirm the branch is in a releasable state with required CI and security-analysis results.
- Enable private vulnerability reporting in the repository security settings so the reporting form linked by `SECURITY.md` is available. The policy file alone does not enable it.
- Review Renovate changes, especially major updates and changes to build/release dependencies.
- Confirm the `nuget` environment and Trusted Publishing configuration.
- After publication, verify that the GitHub Release is finalized, the NuGet package is indexed, and the published package matches the attested original.
- If there is a security fix, coordinate disclosure per [SECURITY.md](../SECURITY.md).
