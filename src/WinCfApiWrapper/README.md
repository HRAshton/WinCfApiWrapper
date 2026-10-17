# WinCfApiWrapper

Expose a read-only content source as a Windows Cloud Files sync root. The library creates file placeholders and fetches
their contents on demand.

## Public API

| Type | Purpose |
| --- | --- |
| `ICloudMountFactory` / `ICloudMount` | Open, mount, inspect, hydrate, and dispose a sync root. |
| `CloudMountOptions` | Set the container, base path, and fetch/hydration concurrency limits. |
| `IContentContainer` / `ContainerEntry` | Provide a stable file and directory listing and streams for file contents. |
| `ISyncRootManager` | Unregister mounts or clean abandoned mounts under an app-owned folder. |
| `MountState`, `MountStatistics`, `MountErrorEventArgs`, `HydrationProgress` | Observe mount state, background failures, fetch activity, and bulk hydration progress. |

Register the factory and sync-root manager with `IServiceCollection` using `AddWinCfApiWrapper()` from `WinCfApiWrapper`.
Public interfaces and built-in containers are in `WinCfApiWrapper.Public` and `WinCfApiWrapper.Public.Containers`.
When the host registers `Microsoft.Extensions.Logging`, lifecycle and failure events are logged; otherwise the library
uses a no-op logger.

## Requirements

- Windows 10 version 1709 or later (build 16299+)
- .NET 8 or later

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

`MountAsync` indexes the source, creates or validates placeholders, and connects the sync root; it does not download all
files. Windows fetches file data when needed. Use `HydrateAsync(path)` to request one file or
`HydrateAllAsync(progress)` to download all files. Bulk hydration reports progress and throws an `AggregateException`
if any files fail. Background fetch failures are reported through `Error` and the configured logger. `Contains(path)`,
`Resolve(path)`, `State`, and `Statistics` expose mount state and activity.

## Containers

Built-ins are in `WinCfApiWrapper.Public.Containers`:

- `ZipContainer` indexes ZIP metadata and reads entries on demand. Its stream factory must return a new readable,
  seekable stream for every call; calls can overlap.
- `TarContainer` and `TarGZipContainer` read TAR and TAR.GZ archives.
- `GZipContainer`, `BrotliContainer`, `DeflateContainer`, and `ZLibContainer` expose a compressed stream as one file;
  pass `entryName` to choose its path (default `content`).

TAR and single-file compressed containers decode their content into memory when indexed. ZIP is preferable for large
archives or sources that support range reads.

For other sources, implement `IContentContainer`. Return a stable listing from `GetEntriesAsync`; each file needs a
safe relative path, exact byte length, and a nonempty `Version` that changes whenever its content changes. Directories
use length zero and an empty version. `OpenReadAsync` may run concurrently and must return a new readable stream at
the beginning of the requested file. The mount disposes returned streams, but the container remains caller-owned.
Keep its listing and content unchanged for the mount's lifetime. Paths are validated for traversal, reserved Windows
names, duplicates, and file/directory conflicts before registration.

## Paths and cleanup

`CloudMountOptions.Root` is a base path; the library appends a content fingerprint to choose the actual `mount.Root`.
Unchanged content can resume an existing mount after its placeholders are validated. The mount folder is disposable,
read-only, and must not contain user data. Dispose the mount to disconnect it. To unregister and delete its folder,
call `ISyncRootManager.Unregister(root)` after disposal.

For app-owned temporary mount folders, call `ISyncRootManager.CleanupMounts(parentFolder)` at startup. It removes
recognized mount folders directly under that dedicated parent and returns the count removed. Do not pass a volume
root or a folder containing user data; partial cleanup failures are reported as an `AggregateException`.

## Build

```powershell
dotnet build src/WinCfApiWrapper/WinCfApiWrapper.csproj -c Release
dotnet test tests/WinCfApiWrapper.Tests -c Release
dotnet test tests/WinCfApiWrapper.Tests -c Release --settings tests/WinCfApiWrapper.Tests/coverage.runsettings '--collect:Code Coverage;Format=Cobertura'
```

The coverage run excludes generated Windows bindings and native Cloud Files adapters. Native integration tests require
Windows Cloud Files support and permission to register sync roots.
