namespace WinCfApiWrapper.Public;

/// <summary>
/// Represents one container-backed Windows sync root. Local changes are not written to the container.
/// </summary>
public interface ICloudMount : IAsyncDisposable
{
    /// <summary>
    /// Raised when background hydration fails; handlers must not throw.
    /// </summary>
    event EventHandler<MountErrorEventArgs>? Error;

    /// <summary>
    /// Gets the filesystem path of the fingerprinted sync root, available after the container is indexed by
    /// MountAsync.
    /// </summary>
    string Root { get; }

    /// <summary>
    /// Gets the mount lifecycle state.
    /// </summary>
    MountState State { get; }

    /// <summary>
    /// Gets the current native fetch statistics.
    /// </summary>
    MountStatistics Statistics { get; }

    /// <summary>
    /// Mounts the container and creates its placeholders.
    /// </summary>
    /// <param name="cancellationToken">A token that cancels mounting.</param>
    /// <returns>A task that completes when placeholders are ready, not when all contents are downloaded.</returns>
    Task MountAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Downloads content of one file to the local placeholder.
    /// </summary>
    /// <param name="relativePath">The container-relative path of the file to hydrate.</param>
    /// <param name="cancellationToken">A token that cancels hydration.</param>
    /// <returns>A task that completes when the file is hydrated.</returns>
    Task HydrateAsync(string relativePath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Hydrates all files, collecting failures into an AggregateException.
    /// </summary>
    /// <param name="progress">An optional receiver for hydration progress.</param>
    /// <param name="cancellationToken">A token that cancels hydration.</param>
    /// <returns>A task that completes when all files have been processed.</returns>
    Task HydrateAllAsync(
        IProgress<HydrationProgress>? progress = null,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Checks whether a relative path is a container file.
    /// </summary>
    /// <param name="relativePath">The container-relative path to check.</param>
    /// <returns><see langword="true" /> if the path identifies a file in the container.</returns>
    bool Contains(string relativePath);

    /// <summary>
    /// Returns a full local path within this mount.
    /// </summary>
    /// <param name="relativePath">The container-relative path to resolve.</param>
    /// <returns>The full path to the corresponding local placeholder.</returns>
    string Resolve(string relativePath);
}
