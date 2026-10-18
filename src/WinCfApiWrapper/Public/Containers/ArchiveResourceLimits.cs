namespace WinCfApiWrapper.Public.Containers;

/// <summary>Bounds decoded data and archive entry counts for built-in containers.</summary>
/// <remarks>
/// Buffered formats enforce both limits while decoding. ZIP enforces only <see cref="MaxEntries" /> because
/// ZIP entry data is streamed during hydration. The buffered byte limit is not an exact cap on process
/// memory; streams, buffers, metadata, and allocations may use additional memory.
/// </remarks>
public sealed record ArchiveResourceLimits
{
    /// <summary>Gets the maximum number of archive entries, including directory entries.</summary>
    public int MaxEntries { get; init; } = 10_000;

    /// <summary>Gets the maximum total decoded bytes held by buffered containers (default 256 MiB).</summary>
    public long MaxBufferedBytes { get; init; } = 256L * 1024 * 1024;

    internal static ArchiveResourceLimits Resolve(ArchiveResourceLimits? limits)
    {
        ArchiveResourceLimits effective = limits ?? new ArchiveResourceLimits();
        if (effective.MaxEntries <= 0)
            throw new ArgumentOutOfRangeException(nameof(limits), "MaxEntries must be positive.");
        if (effective.MaxBufferedBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(limits), "MaxBufferedBytes must be positive.");
        return effective;
    }
}
