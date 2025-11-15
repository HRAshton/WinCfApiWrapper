namespace WinCfApiWrapper.Public;

/// <summary>
/// Reports a background fetch error.
/// </summary>
public sealed class MountErrorEventArgs : EventArgs
{
    /// <summary>
    /// Initializes a new instance of the <see cref="MountErrorEventArgs" /> class.
    /// </summary>
    /// <param name="relativePath">The container path involved, when it is known.</param>
    /// <param name="exception">The failure reported by the fetch callback or worker.</param>
    public MountErrorEventArgs(string? relativePath, Exception exception)
    {
        RelativePath = relativePath;
        Exception = exception;
    }

    /// <summary>
    /// Gets the container path involved, or <see langword="null" /> when unavailable.
    /// </summary>
    public string? RelativePath { get; }

    /// <summary>
    /// Gets the failure reported by the fetch callback or worker.
    /// </summary>
    public Exception Exception { get; }
}
