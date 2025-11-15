namespace WinCfApiWrapper.Public;

/// <summary>
/// Progress of an explicit bulk hydration run.
/// </summary>
/// <param name="Completed">Files that have completed processing.</param>
/// <param name="Total">Files included in the run.</param>
/// <param name="Failed">Files that failed to hydrate.</param>
public readonly record struct HydrationProgress(int Completed, int Total, int Failed);
