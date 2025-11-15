namespace WinCfApiWrapper.Public;

/// <summary>
/// Snapshot of native hydration activity since mounting.
/// </summary>
/// <param name="FetchAttempts">Total native fetch attempts.</param>
/// <param name="FailedFetches">Native fetch attempts that failed.</param>
/// <param name="BytesTransferred">Total bytes successfully transferred.</param>
/// <param name="ActiveFetches">Fetch operations currently in progress.</param>
public readonly record struct MountStatistics(
    long FetchAttempts,
    long FailedFetches,
    long BytesTransferred,
    int ActiveFetches
);
