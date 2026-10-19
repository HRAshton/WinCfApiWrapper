# Architecture and lifecycle

WinCfApiWrapper presents caller-supplied read-only content as a Windows Cloud Files sync root. Files start as placeholders and are hydrated when Windows requests their bytes or the application requests explicit hydration. The library does not provide bidirectional synchronization and does not write modified files back into a container.

## Components

- `ICloudMountFactory` creates an `ICloudMount` using `CloudMountOptions`. `AddWinCfApiWrapper()` registers the factory and sync-root manager with dependency injection.
- `IContentContainer` exposes a stable directory/file listing and independently readable streams. `ContainerCatalog` normalizes paths, rejects conflicting entries, and computes a fingerprint from the listing.
- `CloudMount` coordinates indexing, registration, connection, placeholder creation, fetch callbacks, shutdown, and rollback.
- `FetchCoordinator` bounds the native fetch queue and coordinates requests against the container.
- `NativeTransfer` and native interop code translate managed buffers and failures into Windows Cloud Files transfers.
- `ISyncRootManager` unregisters disconnected mounts and cleans up recognized roots in application-owned folders.

## Conceptual data flow

```text
IContentContainer.GetEntriesAsync
       |
       v
ContainerCatalog: validate + fingerprint
       |
       v
CloudMount: register/connect + create placeholders
       |
Windows requests file bytes
       |
       v
CFAPI callback -> FetchCoordinator -> IContentContainer.OpenReadAsync
       |                              |
       +------ NativeTransfer <-------+
                     |
                     v
              Windows placeholder
```

## Mount and hydration lifecycle

1. `Open` constructs a mount but does not register it.
2. `MountAsync` reads the container listing, validates file identities, derives a fingerprinted root path, registers/connects with Cloud Files, and sets up placeholders. It does **not** eagerly hydrate all content.
3. On demand, the Cloud Files callback pipeline reads the requested file content and transfers data. `HydrateAsync` and `HydrateAllAsync` explicitly request hydration.
4. `Statistics`, `State`, and `Error` communicate activity and failures. Background failures can surface via the event/logger; `HydrateAllAsync` reports per-file failures as an `AggregateException`.
5. `DisposeAsync` stops the mount's connection. It does not necessarily remove registered roots and local placeholders; use `ISyncRootManager.Unregister` when removal is intended.

`CloudMountOptions` exposes `FetchWorkers`, `FetchQueueCapacity`, and `BulkHydrationConcurrency`. Positive limits are required; larger values consume more resources and are not automatically faster.

## Identity, re-use, and cleanup

The mount path appends `.rzm-` followed by the first eight lowercase hexadecimal characters of the content-list SHA-256 fingerprint to the requested base path. This 32-bit prefix can collide between different listings. A previously registered mount can be resumed only if its expected directories/placeholders remain valid. The fingerprint is derived from declared metadata, not from a cryptographic verification of every file's current contents; unchanged upstream content during the mount's lifetime is a caller responsibility.

For controlled immutable sources, `TrustedContentId` substitutes a caller-assigned eight-digit hexadecimal identity for the listing hash. `SkipResumeValidation` additionally skips per-placeholder validation and requires that explicit identity. The caller then guarantees unchanged contents and local placeholders; source and directory enumeration still occur. Reuse an indexed container to avoid archive access on resume. `CloudMountOptions.Progress` reports startup/resume stages, file fetch bytes, explicit hydration, and shutdown. See [release feedback and performance](release-feedback.md) for examples and limits.

Root cleanup is intentionally destructive. Only provide dedicated application-owned folders to `CleanupMounts`; never a directory shared with user data. Read [security-model.md](security-model.md) before mounting third-party data or integrating remote sources.

Call `ISyncRootManager.Unregister(mount.Root)` after disposing the mount to unregister and delete its folder. At application startup, `ISyncRootManager.CleanupMounts(parentFolder)` removes recognized mount folders directly under a dedicated application-owned parent and returns the count removed. Never pass a volume root or a folder containing user data; partial cleanup failures are reported as an `AggregateException`.

## Code map

- `src/WinCfApiWrapper/Public/`: published contracts
- `src/WinCfApiWrapper/Mounting/`: lifecycle, registration, and placeholders
- `src/WinCfApiWrapper/Hydration/`: fetch scheduling and native transfers
- `src/WinCfApiWrapper/Containers/` and `Archives/`: indexing, format readers, and validation
- `tests/WinCfApiWrapper.Tests/`: unit/property tests
- `tests/WinCfApiWrapper.IntegrationTests/`: Windows native, offline, stress, and opt-in live tests
