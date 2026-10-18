# WinCfApiWrapper

[![CI](https://github.com/HRAshton/WinCfApiWrapper/actions/workflows/ci.yml/badge.svg?branch=master)](https://github.com/HRAshton/WinCfApiWrapper/actions/workflows/ci.yml)
[![Codecov coverage](https://codecov.io/gh/HRAshton/WinCfApiWrapper/branch/master/graph/badge.svg?flag=unittests)](https://app.codecov.io/gh/HRAshton/WinCfApiWrapper)
[![OpenSSF Scorecard](https://api.scorecard.dev/projects/github.com/HRAshton/WinCfApiWrapper/badge)](https://scorecard.dev/viewer/?uri=github.com/HRAshton/WinCfApiWrapper)
[![OpenSSF Best Practices](https://www.bestpractices.dev/projects/15642/badge)](https://www.bestpractices.dev/en/projects/15642)
[![REUSE status](https://api.reuse.software/badge/github.com/HRAshton/WinCfApiWrapper)](https://api.reuse.software/info/github.com/HRAshton/WinCfApiWrapper)
[![SLSA provenance](https://img.shields.io/badge/SLSA%20provenance-verified-2ea44f)](https://github.com/HRAshton/WinCfApiWrapper/blob/master/docs/releasing.md)
[![NuGet](https://img.shields.io/nuget/v/WinCfApiWrapper)](https://www.nuget.org/packages/WinCfApiWrapper)
[![0BSD License](https://img.shields.io/badge/license-0BSD-blue)](https://github.com/HRAshton/WinCfApiWrapper/blob/master/LICENSE)
![.NET 8+](https://img.shields.io/badge/.NET-8%2B-512BD4)
![Windows x64](https://img.shields.io/badge/platform-Windows%20x64-0078D6)

Expose a read-only content source as a Windows Cloud Files sync root. Files start as placeholders and their contents are fetched on demand. Built-in containers support ZIP, TAR, TAR.GZ, GZip, Brotli, Deflate, and ZLib; implement `IContentContainer` for other sources.

## Requirements

- Windows x64, Windows 10 version 1709 (build 16299) or later with Cloud Files support.
- .NET 8 or later, targeting `net8.0-windows` or a later Windows target framework.

## Installation

```powershell
dotnet add package WinCfApiWrapper
```

The example below uses the Microsoft dependency injection container. If your application does not already reference it, also install `Microsoft.Extensions.DependencyInjection`.

## Quick start

```csharp
using Microsoft.Extensions.DependencyInjection;
using WinCfApiWrapper;
using WinCfApiWrapper.Public;
using WinCfApiWrapper.Public.Containers;

var services = new ServiceCollection();
services.AddWinCfApiWrapper();
using var provider = services.BuildServiceProvider();

var factory = provider.GetRequiredService<ICloudMountFactory>();
await using ICloudMount mount = factory.Open(new CloudMountOptions
{
    Container = new ZipContainer(_ => File.OpenRead(@"C:\archives\sample.zip")),
    Root = @"C:\Cloud\Sample",
});

mount.Error += (_, error) => Console.Error.WriteLine(error.Exception);
await mount.MountAsync();
Console.WriteLine($"Mounted at {mount.Root}");
await mount.HydrateAsync(@"docs\hello.txt");
```

`MountAsync` creates or validates placeholders and connects the sync root; it does not download all files. Windows fetches contents when needed. Use `HydrateAsync(path)` for one file or `HydrateAllAsync(progress)` for all files. Bulk hydration reports progress and throws an `AggregateException` if any files fail. Background fetch failures are reported through `Error` and the configured logger.

Use a dedicated, disposable mount folder containing no user data. `CloudMountOptions.Root` is a base path; the actual `mount.Root` includes a content fingerprint. Disposing the mount disconnects it. To unregister and delete the folder, call `ISyncRootManager.Unregister(mount.Root)` after disposal. See the [lifecycle and cleanup guide](https://github.com/HRAshton/WinCfApiWrapper/blob/master/docs/architecture.md#identity-re-use-and-cleanup) before integrating cleanup into an application.

## Public API

| Type | Purpose |
| --- | --- |
| `ICloudMountFactory` / `ICloudMount` | Open, mount, inspect, hydrate, and dispose a sync root. |
| `CloudMountOptions` | Set the container, base path, and fetch/hydration concurrency limits. |
| `IContentContainer` / `ContainerEntry` | Provide a stable file and directory listing and streams for file contents. |
| `ISyncRootManager` | Unregister mounts or clean abandoned mounts under an app-owned folder. |
| `MountState`, `MountStatistics`, `MountErrorEventArgs`, `HydrationProgress` | Observe mount state, background failures, fetch activity, and bulk hydration progress. |

Public interfaces and built-in containers are in `WinCfApiWrapper.Public` and `WinCfApiWrapper.Public.Containers`. `AddWinCfApiWrapper()` is in `WinCfApiWrapper`. When the host registers `Microsoft.Extensions.Logging`, lifecycle and failure events are logged; otherwise the library uses a no-op logger.

## Guides and contributing

- [Documentation index](https://github.com/HRAshton/WinCfApiWrapper/blob/master/docs/README.md)
- [Containers](https://github.com/HRAshton/WinCfApiWrapper/blob/master/docs/containers.md): built-in formats, stream ownership, and custom container contracts.
- [Architecture and lifecycle](https://github.com/HRAshton/WinCfApiWrapper/blob/master/docs/architecture.md): hydration, errors, resume, and safe cleanup.
- [Development](https://github.com/HRAshton/WinCfApiWrapper/blob/master/docs/development.md): building, testing, fuzzing, and coverage.
- [Security model](https://github.com/HRAshton/WinCfApiWrapper/blob/master/docs/security-model.md): trust boundaries and limitations.
- [Releasing and provenance](https://github.com/HRAshton/WinCfApiWrapper/blob/master/docs/releasing.md): release cadence, package verification, and recovery.
- [Contributing](https://github.com/HRAshton/WinCfApiWrapper/blob/master/CONTRIBUTING.md): submitting issues, tests, and pull requests.
- [Security policy](https://github.com/HRAshton/WinCfApiWrapper/blob/master/SECURITY.md): private vulnerability reporting.

Report ordinary bugs and request features through [GitHub issues](https://github.com/HRAshton/WinCfApiWrapper/issues). Report vulnerabilities privately using the security policy.
