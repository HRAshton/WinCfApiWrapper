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

The benchmark creates a GUID-named scratch directory below \`--work-parent\` (default: \`%TEMP%\WinCfApiWrapper.Benchmarks\`). It unregisters and deletes **only its own** mount folders, never the supplied archive or an existing directory. Run benchmarks sequentially; they manipulate real native sync roots. On failed cleanup, inspect and remove the **dedicated benchmark folder** manually.

For real HTTP range-source tests, pass both \`--archive\` (the local **identical** ZIP used to define the fixture) and \`--http-url\` (an endpoint returning valid 206 ranges and a stable ETag/Last-Modified):

```powershell
dotnet run --project benchmarks/WinCfApiWrapper.Benchmarks -c Release -f net10.0-windows -- --archive C:\artifacts\installer.zip --http-url https://example.invalid/installer.zip --output http.json
```

\`example.invalid\` is deliberately nonfunctional. Supply your actual server and keep the source unchanged during the run.

## Recorded phases

| Phase | What is timed |
| --- | --- |
| \`zip-index-only\` | First index on a separate container (does not prime cold mount). |
| \`cold-mount\` | Fresh index, register/connect, create placeholders. |
| \`random-file-reads\` | Deterministic, seeded 4 KiB reads of native placeholder paths. |
| \`hydrate-all\` | Explicit hydration, including any files already hydrated by reads. |
| \`dispose-cold\` | Disconnect the cold mount. |
| \`warm-resume-same-container\` | Reconnect using the previously indexed container. |
| \`dispose-same-container\` | Disconnect the reused mount. |
| \`warm-resume-new-container\` | Rebuild index using a new container and reconnect. |
| \`dispose-new-container\` | Disconnect the final mount. |

For each phase JSON contains elapsed milliseconds, **process-wide** managed allocations, working set delta, **process-lifetime** peak working set, local archive open/read bytes, and HTTP range-request counts/requested bytes. HTTP requested bytes are **not** actual transferred bytes (exclude headers, failed requests and network overhead). Other sources and background tasks may add process-wide allocations. The counters themselves add instrumentation overhead.

## Comparing two commits

1. Record each SHA, CPU, RAM, NTFS volume, Windows build, .NET version, archive and source host.
2. Use exactly the same flags, artifact, storage state and dedicated machine. Never compare HTTP to file-source timings.
3. Perform **at least five runs per commit**. Save all raw JSON, compare medians and variability, not one noisy measurement.
4. Review indexing, cold mount, both resumes, random reads, hydrate-all, shutdown, allocations and source I/O.
5. Prefer no new source I/O or hashing in trusted mode. An observed regression requires review; do not invent timing numbers or apply strict one-run thresholds in normal CI.

The [manual workflow](../.github/workflows/trusted-benchmark.yml) performs Windows benchmarks on both supported runtimes and uploads the JSON artifacts. Standard CI **compiles only**; it does not automatically execute costly native benchmarks.

The current PR adds instrumentation and documentation, **not a measured baseline**. Run the manual workflow on this PR's SHA and save its artifacts before merging further optimization PRs.
