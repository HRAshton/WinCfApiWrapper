# Content containers

A container supplies a *stable read-only snapshot* of paths and file streams. The library handles Windows sync-root registration and on-demand hydration; the container is responsible for identifying file contents consistently.

## Built-in containers

- `ZipContainer` exposes ZIP entries on demand. Its factory must return a **new, readable, seekable stream** on every invocation, including concurrent calls. A remote range stream is supported if it behaves like a seekable stream.
- `TarContainer` and `TarGZipContainer` expose regular TAR files/directories.
- `GZipContainer`, `BrotliContainer`, `DeflateContainer`, and `ZLibContainer` expose one decompressed file (default name `content`).

TAR and single-file compressed formats decode and buffer content in memory while indexing. The built-in containers accept optional `ArchiveResourceLimits` (default: 250,000 archive entries and 256 MiB total buffered decoded file bytes). Exceeding either limit throws `InvalidDataException`; limits must be positive.

For example, `new TarGZipContainer(openArchive, new ArchiveResourceLimits { MaxEntries = 500, MaxBufferedBytes = 32L * 1024 * 1024 })` reduces the budget. Single-file compressors accept `limits:` as their third constructor argument (after `entryName`). `ZipContainer` indexes archives of **200,000 files by default**, with headroom for explicit directory entries (250,000 entries total). It enforces the entry-count limit but **not** the buffered-byte limit, because ZIP files are streamed during hydration; large ZIP entries require separate upstream controls. The ZIP index and Windows placeholder creation still require memory and time proportional to the number of files. These budgets are not exact process memory/CPU limits. Prefer ZIP for large data or seekable range sources, and isolate hostile archives when appropriate. See [security-model.md](security-model.md).

## `IContentContainer` contract

`GetEntriesAsync(CancellationToken)` returns a collection of `ContainerEntry` objects. Each entry has:

| Field | Requirement |
| --- | --- |
| `RelativePath` | Safe container-relative Windows path; either slash style accepted |
| `Length` | Exact file byte length, nonnegative |
| `Version` | Nonempty stable identifier for each file; **must change when bytes change** |
| `IsDirectory` | Directory entries require length `0` and empty version |

The library checks invalid/reserved Windows names, traversal, conflicting paths, and case-insensitive duplicates. Directory ancestors are derived automatically; explicit directory entries are optional.

`OpenReadAsync(ContainerEntry, CancellationToken)` may run concurrently. Return a **fresh, independent, readable stream positioned at the beginning of the requested file**, for the entry in the original snapshot. It need not be seekable. The mount owns and disposes the returned stream; the caller still owns the container instance. Honor cancellation when possible and do not mutate the listing or underlying bytes during a mount.

## Minimal in-memory example

```csharp
using System.Security.Cryptography;
using WinCfApiWrapper.Public;

public sealed class StaticFiles : IContentContainer
{
    private readonly Dictionary<string, byte[]> _files;
    private readonly ContainerEntry[] _entries;

    public StaticFiles(IReadOnlyDictionary<string, byte[]> files)
    {
        // Snapshot caller input: external mutations must not change mounted bytes.
        _files = files.ToDictionary(
            pair => pair.Key,
            pair => (byte[])pair.Value.Clone(),
            StringComparer.OrdinalIgnoreCase);

        _entries = _files.Select(pair => new ContainerEntry(
            pair.Key,
            pair.Value.LongLength,
            Convert.ToHexString(SHA256.HashData(pair.Value)))).ToArray();
    }

    public ValueTask<IReadOnlyCollection<ContainerEntry>> GetEntriesAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<IReadOnlyCollection<ContainerEntry>>(_entries);
    }

    public ValueTask<Stream> OpenReadAsync(
        ContainerEntry entry,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_files.TryGetValue(entry.RelativePath, out var bytes))
            throw new FileNotFoundException("Unknown container entry", entry.RelativePath);
        return ValueTask.FromResult<Stream>(new MemoryStream(bytes, writable: false));
    }
}
```

Use validated, unique paths when constructing the example. For remote storage, copy metadata into a stable snapshot, and ensure reads refer to the same object version, such as an immutable object ID or enforced ETag. Handle changed/deleted objects as failures; do not silently serve a new version under a previously advertised identity.

## Testing custom containers

Exercise case-colliding names, `..` segments, Windows reserved devices, file/directory conflicts, empty or unstable versions, wrong lengths, concurrent reads, cancellation, truncated streams, and changed remote content. Tests should verify that cleanup never targets directories containing user data.

For mounting examples, see [the project README](../README.md#quick-start).
