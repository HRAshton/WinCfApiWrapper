namespace WinCfApiWrapper.Public;

/// <summary>
/// Current mount lifecycle state.
/// </summary>
public enum MountState
{
    /// <summary>
    /// The mount has not been started.
    /// </summary>
    Created,

    /// <summary>
    /// Startup is in progress.
    /// </summary>
    Mounting,

    /// <summary>
    /// The sync root is connected and ready.
    /// </summary>
    Mounted,

    /// <summary>
    /// Hydration is draining and the connection is being closed.
    /// </summary>
    Stopping,

    /// <summary>
    /// Resources were released after disposal.
    /// </summary>
    Stopped,

    /// <summary>
    /// Startup or shutdown failed; disposal may be retried after a disconnect failure.
    /// </summary>
    Faulted,
}
