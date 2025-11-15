namespace WinCfApiWrapper.Public;

/// <summary>
/// Manages registration independently from mount connection lifetime.
/// </summary>
public interface ISyncRootManager
{
    /// <summary>
    /// Unregisters a disconnected mount and deletes its owned fingerprinted folder.
    /// </summary>
    /// <param name="root">The full path of the mount root to unregister.</param>
    void Unregister(string root);

    /// <summary>
    /// Unregisters and deletes fingerprinted mount folders beneath an app-owned parent. Call before starting new
    /// mounts. Returns the number cleaned; reports partial failures in an AggregateException.
    /// </summary>
    /// <param name="parentFolder">The application-owned parent folder containing mount roots.</param>
    /// <returns>The number of mount folders removed.</returns>
    int CleanupMounts(string parentFolder);
}
