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

    /// <summary>Gets or initializes a caller-supplied eight-digit hexadecimal snapshot identity instead of hashing the listing.</summary>
    /// <remarks>For controlled immutable sources only. Change this ID whenever any content or listing changes.</remarks>
    public string? TrustedContentId { get; init; }

    /// <summary>Gets or initializes a value indicating whether to skip per-placeholder validation when resuming a trusted mount.</summary>
    /// <remarks>Requires TrustedContentId. The caller must guarantee that the dedicated mount folder and source are unchanged.</remarks>
    public bool SkipResumeValidation { get; init; }
}
