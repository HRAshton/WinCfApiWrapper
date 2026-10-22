namespace WinCfApiWrapper.Public.Containers;

/// <summary>Bounds decoded data and archive entry counts for built-in containers.</summary>
/// <remarks>
/// Buffered formats enforce the entry and buffered-byte limits while decoding.
/// ZIP enforces the entry count and optionally the cumulative central-directory
/// metadata-byte limit; ZIP file data is streamed during hydration. These are not
/// exact process-memory budgets; other allocations may use additional memory.
/// </remarks>
public sealed record ArchiveResourceLimits
{
    /// <summary>Gets the maximum number of archive entries, including directory entries (default 250,000).</summary>
    public int MaxEntries { get; init; } = 250_000;

    /// <summary>Gets the maximum total decoded bytes held by buffered containers (default 256 MiB).</summary>
    public long MaxBufferedBytes { get; init; } = 256L * 1024 * 1024;

    /// <summary>
    /// Gets an optional cumulative ZIP central-directory metadata-byte budget.
    /// Null (the default) disables this budget, including for trusted artifacts.
    /// </summary>
    /// <remarks>
    /// Counts raw central records including directory entries. Does not bound
    /// ZipArchive's own allocations or total process memory.
    /// </remarks>
    public long? MaxCentralDirectoryMetadataBytes { get; init; }

    internal static ArchiveResourceLimits Resolve(ArchiveResourceLimits? limits)
    {
        ArchiveResourceLimits effective = limits ?? new ArchiveResourceLimits();
        if (effective.MaxEntries <= 0)
            throw new ArgumentOutOfRangeException(nameof(limits), "MaxEntries must be positive.");
        if (effective.MaxBufferedBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(limits), "MaxBufferedBytes must be positive.");
        if (effective.MaxCentralDirectoryMetadataBytes is <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(limits),
                "MaxCentralDirectoryMetadataBytes must be positive when provided."
            );
        }
        return effective;
    }
}
