# Trusted build-artifact performance baseline (PERF-00)

This is a **manual benchmark**, not a hard-coded performance claim. It establishes a repeatable comparison for PRs changing Windows Cloud Files operations. The repository's production application uses **trusted, immutable ZIP build artifacts** for installer validation.

## Production contract

All three settings are used together:

```csharp
var container = new ZipContainer(openArchive, limits: null, validateEntryMetadata: false);
var options = new CloudMountOptions
{
    Root = root, // Dedicated app-owned NTFS mount.
    Container = container,
    TrustedContentId = "1234abcd", // Unique for each immutable snapshot.
    SkipResumeValidation = true,
};
```

* Trusted identity bypasses hashing the **listing**; it does not skip ZIP indexing, path normalization or mandatory CFAPI identity checks.
* Skip-resume bypasses **per-file** placeholder checks; directory and registration checks still run.
* ZIP metadata validation disabled skips the selected central-record re-read; source length and snapshot membership are still checked.
* The host guarantees immutable bytes, unique IDs and an unchanged, exclusively app-owned mount directory. Reusing the ID across different snapshots can produce stale hydrated data.
* No new **mandatory** CRC/SHA, per-file resume checks, extra enumeration, network probes or security-only archive caps on this fast path. Preserve mandatory Windows/CFAPI correctness checks and existing eight-hex-character IDs.

## Running on Windows

Use Windows 10 version 1709+ with Cloud Files support, NTFS, an account permitted to register sync roots, .NET 10 SDK and .NET 8/10 runtimes.

```powershell
dotnet run --project benchmarks/WinCfApiWrapper.Benchmarks -c Release -f net8.0-windows -- --entries 10000 --bytes-per-file 1024 --random-reads 128 --output baseline-net8.json
dotnet run --project benchmarks/WinCfApiWrapper.Benchmarks -c Release -f net10.0-windows -- --entries 10000 --bytes-per-file 1024 --random-reads 128 --output baseline-net10.json

# Stress scenario: generated ZIP with 200,000 entries.
dotnet run --project benchmarks/WinCfApiWrapper.Benchmarks -c Release -f net10.0-windows -- --entries 200000 --bytes-per-file 256 --random-reads 1000 --output stress.json

# A real local build artifact.
dotnet run --project benchmarks/WinCfApiWrapper.Benchmarks -c Release -f net10.0-windows -- --archive C:\artifacts\installer.zip --random-reads 128 --output artifact.json
```

The benchmark creates a GUID-named scratch directory below `--work-parent` (default: `%TEMP%\WinCfApiWrapper.Benchmarks`). It unregisters and deletes **only its own** mount folders, never the supplied archive or an existing directory. Run benchmarks sequentially; they manipulate real native sync roots. Cleanup errors are printed to stderr without masking a benchmark failure. If native disconnect/unregister fails, the generated scratch folder is intentionally retained; inspect and clean the **dedicated benchmark folder** manually only after verifying no live mount is connected.

For real HTTP range-source tests, pass both `--archive` (the local **identical** ZIP used to define the fixture) and `--http-url` (an endpoint returning valid 206 ranges and a stable ETag/Last-Modified):

```powershell
dotnet run --project benchmarks/WinCfApiWrapper.Benchmarks -c Release -f net10.0-windows -- --archive C:\artifacts\installer.zip --http-url https://example.invalid/installer.zip --output http.json
```

`example.invalid` is deliberately nonfunctional. Supply your actual server and keep the source unchanged during the run.

## Recorded phases

| Phase | What is timed |
| --- | --- |
| `zip-index-only` | First index on a separate container (does not prime cold mount). |
| `cold-mount` | Fresh index, register/connect, create placeholders. |
| `random-file-reads` | Deterministic, seeded reads of **up to 4 KiB** from native placeholder paths (1 KiB for the default synthetic fixture). |
| `hydrate-all` | Explicit hydration, including any files already hydrated by reads. |
| `dispose-cold` | Disconnect the cold mount. |
| `warm-resume-same-container` | Reconnect using the previously indexed container. |
| `dispose-same-container` | Disconnect the reused mount. |
| `warm-resume-new-container` | Rebuild index using a new container and reconnect. |
| `dispose-new-container` | Disconnect the final mount. |

For each phase JSON contains elapsed milliseconds, **process-wide** managed allocations, working set delta, **process-lifetime** peak working set, local archive open/read bytes, and HTTP range-request counts/requested bytes. HTTP requested bytes are **not** actual transferred bytes (exclude headers, failed requests and network overhead). Other sources and background tasks may add process-wide allocations. The counters themselves add instrumentation overhead.

## Comparing two commits

1. Record each SHA, CPU, RAM, NTFS volume, Windows build, .NET version, archive and source host.
2. Use exactly the same flags, artifact, storage state and dedicated machine. Never compare HTTP to file-source timings.
3. Perform **at least five runs per commit**. Save all raw JSON, compare medians and variability, not one noisy measurement.
4. Review indexing, cold mount, both resumes, random reads, hydrate-all, shutdown, allocations and source I/O.
5. Prefer no new source I/O or hashing in trusted mode. An observed regression requires review; do not invent timing numbers or apply strict one-run thresholds in normal CI.

The [manual workflow](../.github/workflows/trusted-benchmark.yml) performs Windows benchmarks on both supported runtimes and uploads the JSON artifacts. Standard CI **compiles only**; it does not automatically execute costly native benchmarks.

The current PR adds instrumentation and documentation, **not a measured baseline**. Before merging this draft, run the commands above from a Windows checkout of the PR branch. GitHub Actions `workflow_dispatch` workflows generally must exist on the **default branch** before they can be launched through the Actions UI; after this PR is merged, use the manual workflow on `master` and save its raw artifacts **before the next performance-sensitive PR**.

## PERF-02 — trusted-resume directory probe experiment

The original resume implementation calls `Directory.Exists` and then
`File.GetAttributes` for every catalog directory, even when
`SkipResumeValidation = true`. The proposed single-probe implementation
uses `File.GetAttributes` alone, verifying both the directory and
non-reparse attribute on **every ancestor**. It retains the missing/invalid
directory error, native connection ordering, and per-file placeholder policy.

For a controlled local synthetic ZIP, profile both algorithms against the
**same mounted directories** from a Windows checkout:

```powershell
dotnet run --project benchmarks/WinCfApiWrapper.Benchmarks -c Release -f net8.0-windows -- --entries 10000 --bytes-per-file 1024 --random-reads 128 --files-per-directory 1 --resume-probe-passes 7 --output perf02-net8.json
dotnet run --project benchmarks/WinCfApiWrapper.Benchmarks -c Release -f net10.0-windows -- --entries 10000 --bytes-per-file 1024 --random-reads 128 --files-per-directory 1 --resume-probe-passes 7 --output perf02-net10.json
```

`DirectoryProbes` compares the former `exists-then-attributes` loop with
`single-get-attributes` (wall time and process-wide managed allocations).
The directory list comes from the mounted filesystem tree, including all
implicit ZIP parent directories rather than only explicit directory entries.
Sample order alternates to reduce filesystem cache bias. This A/B microprobe
is **not** proof of an end-to-end warm-resume improvement; keep it outside
regular timed phases. The `--resume-probe-passes` default is `0` so
existing benchmark behavior is unchanged. `--files-per-directory` defaults
to 100, matching prior synthetic layouts.

**Do not merge this optimization solely on the microprobe.** Alternatively
benchmark parent/PR commits on the same Windows host with identical ZIP
artifacts (e.g. one-file-per-directory and 100-files-per-directory), five
runs per commit, recording ZIP indexing, cold mount, warm resume, random fetch,
hydrate-all, shutdown, allocation and source I/O. The existing benchmark
separately times ZIP index and whole mount, but **does not yet separate**
catalog construction from native connection; any remaining time attribution
to those phases needs additional Windows instrumentation. Require a repeatable
warm-resume win and no cold-mount or fetch regressions before merge.

## PERF-01 — explicit hydration lock retention experiment

Explicit hydration previously stored one `SemaphoreSlim` per unique relative
path in a mount-lifetime `ConcurrentDictionary`; long-lived mounts performing
many unique hydrations retained these semaphores after operations completed.
The proposed reference-counted dictionary is protected by the existing
hydration lifecycle lock: every caller (including semaphore waiters) increments
the count **before** awaiting, and the last completed caller removes/disposes
the semaphore. A later request then creates a new slot safely. Ordinary native
`FETCH_DATA` callbacks do not use these locks and are unchanged.

Measure **before vs after** with identical Windows hosts and immutable ZIP
fixtures, five alternating runs per commit. Keep `--repeat-hydrations 0` for
the original PERF-00-compatible phase set. Opt in to an extra measured phase
for already-hydrated files:

```powershell
dotnet run --project benchmarks/WinCfApiWrapper.Benchmarks -c Release -f net8.0-windows -- --entries 10000 --bytes-per-file 1024 --random-reads 128 --repeat-hydrations 10000 --output perf01-net8.json
dotnet run --project benchmarks/WinCfApiWrapper.Benchmarks -c Release -f net10.0-windows -- --entries 10000 --bytes-per-file 1024 --random-reads 128 --repeat-hydrations 10000 --output perf01-net10.json
```

`hydrate-all` exercises many distinct keys; `rehydrate-existing-files`
exercises re-creation of locks after previous operations have finished,
reporting wall-clock time and process-wide allocations. Repeat with a larger
distinct-path workload when relevant. The optional phase occurs before cold
disposal and does not access a ZIP entry again if its placeholder is already
hydrated; compare source-open/read counts as a regression guard. Existing
baseline numbers do **not** prove this change improves throughput or peak
memory. Retained slot count after many distinct hydrations, and live slot count during
contention/cancellation are asserted by the Windows unit suite.

**Merge condition:** the after version must reduce retained keyed lock objects
without material performance regression for hydrate-all, repeated hydrations,
native fetches or shutdown; if the memory benefit is not worth added
synchronization and per-operation allocation, retain the previous simpler
implementation. No PR should claim a win without A/B Windows results.
