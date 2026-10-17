# WinCfApiWrapper

[![CI](https://github.com/HRAshton/WinCfApiWrapper/actions/workflows/ci.yml/badge.svg?branch=master)](https://github.com/HRAshton/WinCfApiWrapper/actions/workflows/ci.yml)
[![OpenSSF Scorecard](https://api.scorecard.dev/projects/github.com/HRAshton/WinCfApiWrapper/badge)](https://scorecard.dev/viewer/?uri=github.com/HRAshton/WinCfApiWrapper)
[![NuGet](https://img.shields.io/nuget/v/WinCfApiWrapper)](https://www.nuget.org/packages/WinCfApiWrapper)
[![0BSD License](https://img.shields.io/badge/license-0BSD-blue)](LICENSE)
![.NET 8+](https://img.shields.io/badge/.NET-8%2B-512BD4)
![Windows x64](https://img.shields.io/badge/platform-Windows%20x64-0078D6)

A .NET library for exposing caller-provided content containers as Windows Cloud Files sync roots. The built-in `ZipContainer` supports ZIP sources; callers can implement `IContentContainer` for other formats and storage systems.

See [the library README](src/WinCfApiWrapper/README.md) for usage, the container contract, and mount lifecycle details.

Releases are versioned with GitVersion and published to NuGet on Thursdays only when `master` has new commits.

## Supply-chain security

[OpenSSF Scorecard](https://scorecard.dev/viewer/?uri=github.com/HRAshton/WinCfApiWrapper) analyzes the repository after changes to `master` and weekly. Findings are also uploaded to GitHub code scanning.

Release builds run inside a reusable, GitHub-hosted Windows workflow with read-only source access. That workflow tests, packs, and generates GitHub artifact attestations, targeting **SLSA v1.0 Build Level 3**. A separate, privileged release job downloads the exact immutable build artifact by ID, verifies the attested package bytes, and then publishes to NuGet. No unverified rebuild is substituted during a release retry.

To verify the original release package before use (replace `X.Y.Z` with a published version):

```sh
gh release download vX.Y.Z --repo HRAshton/WinCfApiWrapper --pattern '*.nupkg'
gh attestation verify WinCfApiWrapper.X.Y.Z.nupkg \
  --repo HRAshton/WinCfApiWrapper \
  --signer-workflow HRAshton/WinCfApiWrapper/.github/workflows/attested-release-build.yml \
  --source-ref refs/heads/master
```

Verify the `.snupkg` with the same command and its own filename. NuGet.org repository-signs the distributed `.nupkg`, so its download bytes may differ from the original, attested GitHub Release asset.
