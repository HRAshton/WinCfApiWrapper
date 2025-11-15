namespace WinCfApiWrapper.Public;

/// <summary>
/// Configuration for a content-container-backed Cloud Files mount.
/// </summary>
public sealed record CloudMountOptions
{
    /// <summary>
    /// Gets or initializes the content container to expose through Cloud Files.
    /// </summary>
    public required IContentContainer Container { get; init; }

    /// <summary>
    /// Gets or initializes the base path for the dedicated NTFS sync root. The container fingerprint is appended to
    /// its name.
    /// </summary>
    public required string Root { get; init; }

    /// <summary>
    /// Gets or initializes the maximum number of concurrent native callback fetch operations.
    /// </summary>
    public int FetchWorkers { get; init; } = 4;

    /// <summary>
    /// Gets or initializes the maximum number of queued native callback requests; excess requests fail promptly.
    /// </summary>
    public int FetchQueueCapacity { get; init; } = 128;

    /// <summary>
    /// Gets or initializes the maximum number of concurrent explicit native hydrations, including hydrate-all requests.
    /// </summary>
    public int BulkHydrationConcurrency { get; init; } = 4;
}
