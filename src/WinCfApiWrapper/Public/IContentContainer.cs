namespace WinCfApiWrapper.Public;

/// <summary>Provides a snapshot of files and independent streams for their contents.</summary>
/// <remarks>
/// The mount calls <see cref="GetEntriesAsync" /> once and may call <see cref="OpenReadAsync" /> concurrently for
/// entries returned by that snapshot. Each opened stream must be readable, start at the beginning of the file, and be
/// independent of other streams. The mount disposes every returned stream. The container instance remains owned by
/// the caller. Implementations should honor cancellation and enforce finite I/O timeouts,
/// especially for remote sources. The mount cannot safely terminate arbitrarily blocked provider code.
/// </remarks>
public interface IContentContainer
{
    /// <summary>Lists the files and directories in the container.</summary>
    /// <param name="cancellationToken">A token that cancels listing.</param>
    /// <returns>A stable snapshot of the container's entries.</returns>
    ValueTask<IReadOnlyCollection<ContainerEntry>> GetEntriesAsync(
        CancellationToken cancellationToken = default
    );

    /// <summary>Opens a file from the indexed snapshot.</summary>
    /// <param name="entry">A normalized, non-directory entry from the indexed snapshot.</param>
    /// <param name="cancellationToken">A token that cancels opening the file.</param>
    /// <returns>A readable stream positioned at the start of the file.</returns>
    ValueTask<Stream> OpenReadAsync(
        ContainerEntry entry,
        CancellationToken cancellationToken = default
    );
}
