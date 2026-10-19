namespace WinCfApiWrapper.Public;

/// <summary>Progress for a mount operation. A null total indicates an indeterminate stage.</summary>
/// <param name="Stage">The current operation stage.</param>
/// <param name="Completed">Completed items, or bytes for the Fetch stage.</param>
/// <param name="Total">Total items or bytes, when known.</param>
/// <param name="RelativePath">The affected file, when applicable.</param>
public sealed record OperationProgress(
    string Stage,
    long Completed,
    long? Total = null,
    string? RelativePath = null
);
