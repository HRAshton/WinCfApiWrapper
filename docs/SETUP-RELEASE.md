# One-time setup before merging CI/CD PR #1

## Verified current state

- Repository is **public**, so public artifact attestations are available.
- GitHub **Allow auto-merge** is enabled.
- Existing [`v*` tag ruleset](https://github.com/HRAshton/WinCfApiWrapper/rules/25304288) is active, protects against deletion, updates and non-fast-forward changes, and allows release tag creation.
- A `master` branch ruleset is now **active** ([ruleset #25304476](https://github.com/HRAshton/WinCfApiWrapper/rules/25304476)), requiring PRs, passing Windows CI, signed commits and linear history.
- **Caution:** It also requires GitHub Code Quality warnings to be resolved and **95% line coverage**. The current CI does not publish GitHub Code Quality coverage results; confirm the Code Quality feature is enabled and real coverage is measured before enforcing a threshold. Until then, turn off `Require code quality results` and `Restrict code coverage` in that ruleset, then enable them after baseline measurements. See [GitHub's coverage setup guide](https://docs.github.com/en/code-security/how-tos/maintain-quality-code/restrict-code-coverage).
- The PR's **Build and unit tests (Windows)** check has passed.

## Apply GitHub settings before merge

The connected GitHub integration can edit source and PRs, but **cannot administer rulesets, environments or Actions variables**. These settings live outside Git history and need an authenticated repository administrator.

Check out PR #1 locally and run with [GitHub CLI](https://cli.github.com/) and PowerShell 7:

```powershell
gh auth login
gh repo clone HRAshton/WinCfApiWrapper
cd WinCfApiWrapper
gh pr checkout 1
pwsh -File scripts/setup-github.ps1 -NuGetUser YOUR_NUGET_ORG_USERNAME
```

This script recognizes and validates an existing `master` ruleset (and **does not replace it**), creates a basic master ruleset only if none exists, verifies or creates the `v*` tag ruleset, configures the `nuget` environment for **protected branches** only, and sets the nonsecret `NUGET_USER` repository variable. It will not install Renovate or modify NuGet's web-account policy.

The fallback [`master` ruleset template](../.github/rulesets/master.json) enforces (if no active master ruleset already exists):
- Pull request required, with **zero required approvals** (Renovate must be able to auto-merge).
- Passing **Build and unit tests (Windows)** check, with strict up-to-date branch checks.
- Linear history (squash or rebase merges), no force push, no deletion.
- No bypass actors; do not add a rule that blocks *all* updates to `master`, which would also prevent successful PR merges.

The branch protection requires one specific status context; if CI is renamed, update the ruleset with the new exact check name. The script uses full repository administration permissions from **your own** authenticated `gh` CLI session (not a stored workflow token).

## Configure services outside GitHub

1. [Install the hosted Renovate app](https://github.com/apps/renovate) and grant it access to **WinCfApiWrapper**. The `renovate.json` on this PR becomes active once merged; Renovate may generate an initial onboarding PR before then. Confirm it can schedule non-major updates into one PR and separate major updates, and that platform automerge works after CI passes.
2. [Create a NuGet Trusted Publishing policy](https://www.nuget.org/account/TrustedPublishing) under your nuget.org account. Choose GitHub Actions and enter **owner** `HRAshton`, **repository** `WinCfApiWrapper`, **workflow file** `release.yml` (just filename), and **environment** `nuget`. Grant scope for **new package creation** and **new package versions**, as appropriate. NuGet identity and package ownership must be authorized; the GitHub connector cannot do this on your behalf.
3. Confirm the `WinCfApiWrapper` package ID is available or already owned by your nuget.org account. Allow any NuGet policy activation window before Thursday's first release.
4. Test a manual release from `master` only after PR #1 is merged and trusted publishing is ready. Never create a `vX.Y.Z` tag on a commit whose package hasn't been published; the weekly release skip compares commits against these tags.

## Verify protections are active

```powershell
gh api repos/HRAshton/WinCfApiWrapper/rulesets
gh api repos/HRAshton/WinCfApiWrapper/environments/nuget
gh variable get NUGET_USER -R HRAshton/WinCfApiWrapper
gh pr checks 1 -R HRAshton/WinCfApiWrapper
```

Open [GitHub rulesets](https://github.com/HRAshton/WinCfApiWrapper/settings/rules) and confirm both tag and branch rulesets show **Active**. Check the **`master`** rule details for the required check, zero approvals, and no bypass. If a Code Quality or coverage rule is also active, confirm it has a successful check and uploaded coverage data; otherwise remove those two thresholds temporarily. No version tags or packages should be created as part of account setup.

NuGet Trusted Publishing is OIDC identity-based authentication. Artifact attestations from the release job are a separate verifiable record; both are configured in the PR and require a real release execution to prove end-to-end success.
