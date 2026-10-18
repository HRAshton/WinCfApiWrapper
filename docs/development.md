# Development and testing

## Prerequisites

- Windows x64, Windows 10 version 1709 (build 16299) or later with Cloud Files API support.
- .NET 10 SDK (see `global.json`) and the .NET 8 and .NET 10 runtimes.
- Permissions to register Cloud Files sync roots for native integration tests.
- PowerShell for release-script self-tests and optional GitHub CLI (`gh`) for release verification.

The library targets `net8.0-windows`. Both test projects target `net8.0-windows` and `net10.0-windows`. Most integration tests work offline; the live GitHub ZIP test requires network access and is opt-in.

## Restore and build

```powershell
dotnet restore tests/WinCfApiWrapper.Tests/WinCfApiWrapper.Tests.csproj
dotnet restore tests/WinCfApiWrapper.IntegrationTests/WinCfApiWrapper.IntegrationTests.csproj
dotnet build tests/WinCfApiWrapper.Tests/WinCfApiWrapper.Tests.csproj -c Release -f net8.0-windows
dotnet build tests/WinCfApiWrapper.IntegrationTests/WinCfApiWrapper.IntegrationTests.csproj -c Release -f net8.0-windows
```

## Unit, property, and offline integration tests

```powershell
dotnet test tests/WinCfApiWrapper.Tests/WinCfApiWrapper.Tests.csproj -c Release -f net8.0-windows
dotnet test tests/WinCfApiWrapper.Tests/WinCfApiWrapper.Tests.csproj -c Release -f net10.0-windows

dotnet test tests/WinCfApiWrapper.IntegrationTests/WinCfApiWrapper.IntegrationTests.csproj -c Release -f net8.0-windows --filter "Category=Offline"
dotnet test tests/WinCfApiWrapper.IntegrationTests/WinCfApiWrapper.IntegrationTests.csproj -c Release -f net10.0-windows --filter "Category=Offline"
```

`tests/WinCfApiWrapper.Tests/Fuzzing/InputBoundaryProperties.cs` uses FsCheck to generate inputs for path and transfer-range checks. Archive parsers require separate coverage-guided fuzzing.

## Opt-in archive stress test

The 200,000-file ZIP indexing test is skipped during normal unit runs. To run it:

```powershell
$env:RZM_RUN_ARCHIVE_STRESS = '1'
dotnet test tests/WinCfApiWrapper.Tests/WinCfApiWrapper.Tests.csproj -c Release --filter "Category=ArchiveStress"
Remove-Item Env:RZM_RUN_ARCHIVE_STRESS
```

In GitHub Actions, manually run **CI** with **Run archive stress tests** checked.
This enables the test on both runtimes. Normal pull-request and push runs keep it skipped.
The checkbox becomes available once the workflow change is on the default branch.
Attested release builds always enable the archive stress test on both runtimes;
a failure blocks packaging, just like a failed live test.

## Coverage

```powershell
dotnet test tests/WinCfApiWrapper.Tests/WinCfApiWrapper.Tests.csproj -c Release -f net8.0-windows --settings tests/coverage.runsettings --collect "Code Coverage"
```

CI uploads Cobertura reports to Codecov and excludes selected generated/native adapter code in `tests/coverage.runsettings`. Do not treat the reported percentage as complete coverage of Windows native interop. Preserve or extend focused integration tests when modifying excluded code.

## Opt-in live end-to-end tests

```powershell
$env:RZM_RUN_LIVE_TESTS = '1'
dotnet test tests/WinCfApiWrapper.IntegrationTests/WinCfApiWrapper.IntegrationTests.csproj -c Release --filter "Category=Live" --blame-hang-timeout 3m
Remove-Item Env:RZM_RUN_LIVE_TESTS
```

Live tests make network requests and exercise real Cloud Files registration. Avoid using important or shared directories for tests. CI also runs offline stress tests; release validation runs live tests before packaging.

## Code style, metadata, and release checks

- Follow `.editorconfig` and the StyleCop analyzers configured in `Directory.Build.props`.
- Preserve nullable annotations and add XML documentation for new public APIs.
- REUSE metadata is configured in `REUSE.toml`. Local verification with the REUSE CLI: `reuse lint`.
- Build/restore warnings and CI failures must be resolved rather than suppressed without explanation.
- The release-script syntax and its offline `-Phase SelfTest` checks run in CI. For a local self-test: `./scripts/release-transaction.ps1 -Phase SelfTest`.

Use [CONTRIBUTING.md](../CONTRIBUTING.md) for submission expectations and [security-model.md](security-model.md) for threat-related test cases.
