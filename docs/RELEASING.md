# Release automation and provenance

## Schedule

- Every pull request and push to `master` builds, runs unit tests, and checks NuGet packaging on Windows.
- Renovate checks for new updates on Mondays (Europe/Belgrade). All minor and patch changes share one PR; each major dependency update gets its own PR. Both can auto-merge after required CI passes. Vulnerability PRs can bypass the schedule.
- At 12:17 Europe/Belgrade every Thursday, the release workflow compares `master` to the latest stable `vX.Y.Z` tag. Equal SHA = skip. New commits = GitVersion -> tests -> pack -> attest -> NuGet Trusted Publishing -> GitHub Release.
- Manual `workflow_dispatch` runs the same pipeline from `master` for hotfixes; it never republishes a commit already tagged.
- Native Cloud Files integration tests are a separate manual workflow until they are proven reliable on GitHub-hosted Windows machines. The normal CI gate covers unit tests, NOT native integration behavior.

## One-time owner setup (required before first release)

1. Follow [the pre-merge setup checklist](SETUP-RELEASE.md), including the administrator-applied ruleset and GitHub environment configuration. Install/authorize the [Mend Renovate GitHub App](https://github.com/apps/renovate) for this repository.
2. In GitHub Settings -> Actions, enable actions. Enable auto-merge and protect `master` with a ruleset requiring the CI `Build and unit tests (Windows)` check. Do not require human review for Renovate PRs if fully unattended updates are desired. Configure branch/ruleset protections as needed; the config file alone does not turn these on.
3. Create a GitHub environment named `nuget`. You may require human approval for deployment if desired, but doing so will make Thursday releases non-automatic.
4. Set the repository (or `nuget` environment) variable `NUGET_USER` to your nuget.org **username**, not email.
5. On [nuget.org Trusted Publishing](https://www.nuget.org/account/TrustedPublishing), create a GitHub Actions policy for owner `HRAshton`, repository `WinCfApiWrapper`, workflow `release.yml`, and environment `nuget`. Permit first-package creation as well as future versions. New policies may need an activation period.
6. The repository is **public** (verified 2026-10-17), so public artifact attestations can be used. The workflow deliberately fails closed at the attestation step, before NuGet publishing, if attestation cannot be generated.
7. Verify that `WinCfApiWrapper` is available to claim as a package ID, or update the ID and matching README badge before publishing.

## Versioning

GitVersion 6.x uses the Mainline version strategy with stable `vX.Y.Z` release tags on `master`. `+semver: minor` and `+semver: major` in commit / squash-merge messages request larger bumps. Ordinary new commits default to patch. No changes since the last tag means no new release. If no tags exist, GitVersion calculates a first version automatically: review that version before the first release if you want a specific initial version.

Use **full Git history** when running GitVersion. Release tags must point at commits on `master` and should never be moved or deleted. Merge/squash practices affect which commit messages GitVersion sees. A Renovate major dependency update does **not** automatically force a major version of this library.

## Provenance verification

The workflow attests the original `.nupkg` and `.snupkg` with `actions/attest` before NuGet publishing and uploads those exact files as GitHub Release assets. Verify a downloaded **GitHub Release asset** (not the NuGet.org-downloaded package) with:

```sh
gh attestation verify WinCfApiWrapper.X.Y.Z.nupkg --repo HRAshton/WinCfApiWrapper --signer-workflow HRAshton/WinCfApiWrapper/.github/workflows/release.yml
```

NuGet.org automatically **repository-signs** uploaded packages and therefore changes their bytes; GitHub's attestation is for the original published build artifact, not the post-signing NuGet download. NuGet's repository signature can be verified separately using `dotnet nuget verify`. Trusted Publishing's OIDC authentication is different from either artifact attestation or package signature.

## Partial-release recovery

The workflow creates the GitHub Release **after** NuGet publication, so if the latter succeeds but GitHub Release creation fails, rerunning will likely fail on NuGet's immutable duplicate version. Do not use `--skip-duplicate` to conceal this. Recover by confirming the uploaded NuGet version, retrieving the **original** release artifacts from that workflow run, confirming the recorded attestation, and creating the matching tag and GitHub Release from the same `master` commit. Never rebuild/re-attest a different artifact under the same version without explicit investigation.

Do not claim an OpenSSF Scorecard score or SLSA certification until the relevant checks are actually configured and results publicly available.
