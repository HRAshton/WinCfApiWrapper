# Architecture and lifecycle

WinCfApiWrapper presents caller-supplied read-only content as a Windows Cloud Files sync root. Files start as placeholders and are hydrated when Windows requests their bytes or the application requests explicit hydration. The library does not provide bidirectional synchronization and does not write modified files back into a container.

## Components

- `ICloudMountFactory` creates an `ICloudMount` using `CloudMountOptions`. `AddWinCfApiWrapper()` registers the factory and sync-root manager with dependency injection.
- `IContentContainer` exposes a stable directory/file listing and independently readable streams. `ContainerCatalog` normalizes paths, rejects conflicting entries, and computes a fingerprint from the listing. During indexing, generic and ZIP catalogs check the existing 4,096-byte CFAPI UTF-8 identity limit before expanding directory parents; shared ancestors are indexed once. Oversized archive paths fail before potentially quadratic prefix expansion. No additional archive/network reads or opt-in security depth cap are introduced.
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
4. `Statistics`, `State`, and `Error` communicate activity and failures. Background failures can surface via the event/logger; `HydrateAllAsync` reports per-file failures as an `AggregateException`. Synchronous exceptions thrown by bulk-hydration progress observers are logged as warnings and do not interrupt file processing or mask cancellation/file failures; callbacks posted asynchronously by `Progress<T>` must handle their own exceptions.
5. `DisposeAsync` stops the mount's connection. It does not necessarily remove registered roots and local placeholders; use `ISyncRootManager.Unregister` when removal is intended.

The native `FETCH_DATA` boundary checks that the callback payload contains the required offset/length fields and validates the requested range before accessing those fields or queuing a request. Older valid payload layouts are accepted even if newer optional fields are absent. Ranges with no recoverable file overlap are reported without issuing `CfExecute`. A positive range beginning within the logical file but running past EOF receives exactly one aligned, failed `TRANSFER_DATA` for its valid overlap; this can fail pending reads rather than leaving them outstanding. Malformed identities with a valid range receive one native failure completion. An unsuccessful `CfExecute` is not blindly retried. Successful fetches do not incur archive I/O or allocations from these guards.

A positive opt-in `CloudMountOptions.ShutdownTimeout` sets one observation budget for fetch-worker and explicit-hydration drains (null by default). If third-party content I/O ignores cancellation and this wait expires, `DisposeAsync` raises `TimeoutException`, logs the stalled phase plus active/pending operation counts, and marks the mount `Faulted`; CFAPI remains connected and delegates, catalog and coordinator remain alive. Releasing the stall and retrying `DisposeAsync` allows a safe disconnect. No timeout can safely terminate a blocked third-party stream, and this option does not impose provider I/O deadlines.

The native connection roots its managed `CloudMount` owner and both callback delegates for the entire CFAPI connection lifetime. The root is established before `CfConnectSyncRoot` and released only after successful `CfDisconnectSyncRoot`; a failed disconnect retains it for a subsequent `DisposeAsync` retry. **Always explicitly dispose the mount**: dropping the last managed reference without disconnecting deliberately leaves native callback state alive and cannot release its resources automatically. The root is a connection-lifecycle cost, not per-fetch work.

`CloudMountOptions` exposes `FetchWorkers`, `FetchQueueCapacity`, and `BulkHydrationConcurrency`. Positive limits are required; larger values consume more resources and are not automatically faster.

## Identity, re-use, and cleanup

Automatic listing fingerprints now use a versioned `.rzm-v2-` mount suffix followed by the first **32 lowercase hex digits (128 bits)** of the existing SHA-256 digest. Unlike the earlier eight-digit (32-bit) automatic suffix, this avoids the demonstrated short-prefix collision without adding hashing, archive reads, or network calls. Explicit eight-hex `TrustedContentId` values still use the existing `.rzm-xxxxxxxx` name unchanged.

Legacy automatic `.rzm-xxxxxxxx` directories are **not silently migrated, resumed, or deleted** when the new automatic path is created. Their names cannot be distinguished from eight-digit trusted mount names. When using a dedicated app-owned parent, clean obsolete roots **explicitly** with `ISyncRootManager.Unregister(oldRoot)` or `CleanupMounts(parentFolder)` after ensuring they are disconnected and not needed by a trusted mount. Both legacy and v2 suffixes are recognized by explicit cleanup. A previously registered v2 root can be resumed only if its expected directories/placeholders remain valid. The fingerprint is derived from declared metadata, not cryptographic verification of file bytes; immutable sources remain the caller's responsibility.

For controlled immutable sources, `TrustedContentId` substitutes a caller-assigned eight-digit hexadecimal identity for the listing hash. `SkipResumeValidation` additionally skips per-placeholder validation and requires that explicit identity. The caller then guarantees unchanged contents and local placeholders; source and directory enumeration still occur. Reuse an indexed container to avoid archive access on resume. `CloudMountOptions.Progress` reports startup/resume stages, file fetch bytes, explicit hydration, and shutdown. See [release feedback and performance](release-feedback.md) for examples and limits.

The production trusted build-artifact configuration enables `TrustedContentId`, `SkipResumeValidation = true` and ZIP `validateEntryMetadata: false` together. Trusted resume still checks directories, and a newly constructed ZIP container still indexes its source. The [TrustedID benchmark](trusted-benchmark.md) measures these separate costs and establishes performance review requirements; no additional validation is implied by this documentation.

Root cleanup is intentionally destructive. Only provide dedicated application-owned folders to `CleanupMounts`; never a directory shared with user data. Cleanup now requires provider-name matching for registered roots or a retained sibling pending-cleanup marker in the private parent for incomplete unregistered rollbacks, and refuses directory reparse points. All checks occur at cleanup, not during normal mount/fetch. Neither the marker nor pathname checks replace a protected NTFS parent ACL or atomically stop hostile path-swap races. Read [security-model.md](security-model.md) before mounting third-party data or integrating remote sources.

Call `ISyncRootManager.Unregister(mount.Root)` after disposing the mount to unregister and delete its folder. At application startup, `ISyncRootManager.CleanupMounts(parentFolder)` removes recognized mount folders directly under a dedicated application-owned parent and returns the count removed. Never pass a volume root or a folder containing user data; partial cleanup failures are reported as an `AggregateException`.

## Code map

- `src/WinCfApiWrapper/Public/`: published contracts
- `src/WinCfApiWrapper/Mounting/`: lifecycle, registration, and placeholders
- `src/WinCfApiWrapper/Hydration/`: fetch scheduling and native transfers
- `src/WinCfApiWrapper/Containers/` and `Archives/`: indexing, format readers, and validation
- `tests/WinCfApiWrapper.Tests/`: unit/property tests
- `tests/WinCfApiWrapper.IntegrationTests/`: Windows native, offline, stress, and opt-in live tests
