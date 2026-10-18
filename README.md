# WinCfApiWrapper

[![CI](https://github.com/HRAshton/WinCfApiWrapper/actions/workflows/ci.yml/badge.svg?branch=master)](https://github.com/HRAshton/WinCfApiWrapper/actions/workflows/ci.yml)
[![Codecov coverage](https://codecov.io/gh/HRAshton/WinCfApiWrapper/branch/master/graph/badge.svg?flag=unittests)](https://app.codecov.io/gh/HRAshton/WinCfApiWrapper)
[![OpenSSF Scorecard](https://api.scorecard.dev/projects/github.com/HRAshton/WinCfApiWrapper/badge)](https://scorecard.dev/viewer/?uri=github.com/HRAshton/WinCfApiWrapper)
[![REUSE status](https://api.reuse.software/badge/github.com/HRAshton/WinCfApiWrapper)](https://api.reuse.software/info/github.com/HRAshton/WinCfApiWrapper)
[![SLSA provenance](https://img.shields.io/badge/SLSA%20provenance-verified-2ea44f)](https://github.com/HRAshton/WinCfApiWrapper/actions/runs/39067209522)
[![NuGet](https://img.shields.io/nuget/v/WinCfApiWrapper)](https://www.nuget.org/packages/WinCfApiWrapper)
[![0BSD License](https://img.shields.io/badge/license-0BSD-blue)](LICENSE)
![.NET 8+](https://img.shields.io/badge/.NET-8%2B-512BD4)
![Windows x64](https://img.shields.io/badge/platform-Windows%20x64-0078D6)

A .NET library for exposing caller-provided content containers as Windows Cloud Files sync roots. The built-in `ZipContainer` supports ZIP sources; callers can implement `IContentContainer` for other formats and storage systems.

See [the library README](src/WinCfApiWrapper/README.md) for usage, the container contract, and mount lifecycle details.

## Guides

- [Contributing](CONTRIBUTING.md) — submitting issues, tests, and pull requests.
- [Documentation index](docs/README.md) — architecture, container integration, development, security, and releasing.
- [Security model](docs/security-model.md) — trust boundaries and limitations.
- [Security policy](SECURITY.md) — private vulnerability reporting.

Releases are versioned with GitVersion and published to NuGet on Thursdays only when `master` has new commits.

## Verifying release provenance

For releases built with provenance export enabled, the GitHub Release includes a
`WinCfApiWrapper.<version>.intoto.jsonl` file alongside the NuGet and symbol
packages. This is the authentic signed Sigstore/in-toto build-provenance bundle,
not merely a checksum or an independently certified SLSA level.

Download the `.nupkg`, `.snupkg`, `.intoto.jsonl`, and
`release-manifest.json` from the same
[GitHub Release](https://github.com/HRAshton/WinCfApiWrapper/releases).
Read the `commit` field from the manifest, then verify each original package:

```powershell
gh attestation verify WinCfApiWrapper.<version>.nupkg `
  --bundle WinCfApiWrapper.<version>.intoto.jsonl `
  --repo HRAshton/WinCfApiWrapper `
  --signer-workflow HRAshton/WinCfApiWrapper/.github/workflows/attested-release-build.yml `
  --source-ref refs/heads/master `
  --source-digest <manifest-commit>
```

Repeat for the `.snupkg`. The release workflow also compares the nuget.org
repository-signed package payload against the original attested package before
finalizing the release. Existing `v0.1.0` has attestations in GitHub's
attestation store, but predates the downloadable `.intoto.jsonl` asset.
