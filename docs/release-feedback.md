# Release feedback: archive access and mount latency

Priority is correctness and request amplification first, followed by trusted startup and UI progress. The table distinguishes library behavior from behavior supplied by the host's archive stream.

| Feedback | Priority | Confirmed cause and resolution |
| --- | --- | --- |
| 1. Every file fetches the ZIP index | P1 | Confirmed: constructing `ZipArchive` per file reread and parsed the entire directory. The container now retains central records once and presents only the selected record to .NET for file opens. Default consistency checking rereads only that record. |
| 2. Every file downloads the archive | P1 | A factory that downloads into memory does this before ZIP decoding starts, including the previous HTTP integration fixture. `ZipContainer.FromHttp` supplies actual seekable byte-range reads; that fixture now uses it. Existing custom factories retain their behavior. |
| 3. Small files repeatedly fetch overlapping 1 MiB blocks | P1 | There was no 1 MiB buffer in the library; that policy belongs to the supplied stream. The built-in HTTP path requests only consumed ranges, with a 64 KiB maximum. A regression test reads twelve 10 KiB files from a 200-entry ZIP and limits total post-index download to less than 11 KiB per file including probes and metadata. |
| 4. Hydrated reads contact the archive | P1 | Normal reads of fully hydrated placeholders belong to Windows and should not invoke a fetch callback. A native regression test hydrates a file, makes the source unavailable, then reads and hydrates it again and resumes with the same indexed container without any archive opens. |
| 7. Large files are not chunked and time out | P1 | Native transfers already used 64 KiB chunks, but an eager factory could block them behind a full-archive download. The HTTP path also caps source reads at 64 KiB. Regression tests cover a multi-megabyte entry and incremental native chunk completions/progress. Seekable custom file streams now seek directly to the requested offset instead of reading and discarding the prefix. |
| 5. Disable hash and consistency checks for stable environments | P2 | `TrustedContentId` bypasses the listing hash; `SkipResumeValidation` skips per-file placeholder checks and requires that explicit identity. ZIP metadata validation can separately be disabled. Defaults retain validation. |
| 6. `IProgress` for mount and long operations | P2 | `CloudMountOptions.Progress` accepts `IProgress<OperationProgress>` for indexing, creating/validating placeholders, fetch bytes, explicit hydration, and shutdown. Existing hydrate-all file-count progress remains available. |

## UI progress

Create `Progress<T>` on the UI thread so it posts updates to the UI synchronization context:

```csharp
var progress = new Progress<OperationProgress>(update =>
{
    try
    {
        // A null Total means show an indeterminate spinner.
        // Fetch updates count bytes; placeholder updates count entries.
        UpdateStatus(update.Stage, update.Completed, update.Total, update.RelativePath);
    }
    catch (Exception error)
    {
        logger.LogError(error, "Progress UI update failed");
    }
});
var options = new CloudMountOptions
{
    Root = @"C:\Cloud\Sample",
    Container = container,
    Progress = progress,
};
```

Stages are `Indexing`, `CreatingPlaceholders`, `Validating`, `Mounted`, `Fetch`, `Hydrating`, `Stopping`, and `Stopped`. Concurrent fetches report separately by `RelativePath`; fetch byte counts are relative to each requested range. Indexing reports an indeterminate start, and explicit hydration reports start/completion even when Windows already has the contents.

Intermediate placeholder creation and resume-validation updates are throttled to at most ten reports per second. The phase start and final count bypass the throttle, as do lifecycle transitions such as `Mounted` and `Stopped`. Suppressed updates do not allocate progress records or enqueue UI callbacks. Counts may jump and paths identify only the sampled entry, so this feed is not a per-entry audit log. Fetch and explicit-hydration notifications retain their per-operation reporting.

The mount catches and logs exceptions thrown synchronously by `IProgress.Report`. `Progress<T>` posts callbacks asynchronously; exceptions in those callbacks occur on their target context and cannot be caught by the mount. Handle exceptions inside the UI callback, as shown above. Completion/failure/cancellation is determined by the operation task and `Error`, not by assuming a final progress event always arrives.

## Controlled immutable sources

```csharp
var container = new ZipContainer(openArchive, limits: null, validateEntryMetadata: false);
var options = new CloudMountOptions
{
    Container = container,
    Root = @"C:\Cloud\Sample",
    TrustedContentId = "1234abcd", // Eight hexadecimal digits, unique for this snapshot.
    SkipResumeValidation = true,
    Progress = progress,
};
```

The host must guarantee immutable contents and an unchanged, dedicated mount folder. Assign a different ID for every changed snapshot; reusing it can expose stale local files. Path validation, entry membership, identity length limits, registered-root checks, directory checks, and archive length checks remain active. The hash being skipped is a **metadata listing hash**, not a full-archive content hash; normal mounting never computes the latter.

This avoids hashing and per-file native validation, but it cannot make initial enumeration or creating thousands of placeholders instantaneous. Reusing the same `ZipContainer` retains its index across mount instances and avoids source access on resume. A new container or process must index the archive again; there is no persistent on-disk index cache.

## Remaining format and transport limits

Deflated ZIP entries are sequential streams: opening a late range still requires decompressing the prefix. Bounded requests and transfers prevent full-archive buffering but do not provide constant-time random access inside a compressed entry. TAR and single-file compressed containers still buffer their decoded contents during indexing under the existing resource budgets. Use ZIP or a custom seekable `IContentContainer` for large streaming workloads.

A slow server can still exceed the application's timeout; chunking bounds request size rather than guaranteeing latency. Hosts still control cancellation and HTTP client timeouts. Native hydration and offline-resume behavior must be validated on Windows with permission to register Cloud Files sync roots.

## Validation

Validated on .NET 8 and .NET 10: 282 unit/property/archive tests per runtime with the archive stress tests enabled, and 18 offline native integration tests per runtime. This includes 200,000-entry ZIP indexing/opening, small-file byte budgets, large-entry bounded reads, selected-record validation, regenerated timestamps, native chunk progress, trusted resume, and offline reads of hydrated files. Progress tests model 200,000 entries with a deterministic clock, preserve final counts, and exercise real `Progress<T>` dispatch through a queued synchronization context, including callback exception handling. Optional live internet tests were not run.
