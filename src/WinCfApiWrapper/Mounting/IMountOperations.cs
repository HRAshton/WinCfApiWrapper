using Microsoft.Win32.SafeHandles;
using WinCfApiWrapper.Public;
using Windows.Win32.Storage.CloudFilters;

namespace WinCfApiWrapper.Mounting;

/// <summary>
/// Provides the native operations used to manage a Cloud Files mount.
/// </summary>
internal interface IMountOperations
{
    /// <summary>
    /// Registers a directory as a Cloud Files sync root.
    /// </summary>
    /// <param name="root">The full path of the sync root to register.</param>
    void Register(string root);

    /// <summary>
    /// Unregisters a directory from Cloud Files.
    /// </summary>
    /// <param name="root">The full path of the sync root to unregister.</param>
    void Unregister(string root);

    /// <summary>
    /// Connects the sync root to the Cloud Files callback system.
    /// </summary>
    /// <param name="root">The full path of the sync root to connect.</param>
    /// <param name="callbacks">The callbacks to register for the connection.</param>
    /// <returns>The connection key used to disconnect the sync root.</returns>
    long Connect(string root, CF_CALLBACK_REGISTRATION[] callbacks);

    /// <summary>
    /// Disconnects a sync root from the Cloud Files callback system.
    /// </summary>
    /// <param name="key">The connection key returned by <see cref="Connect" />.</param>
    void Disconnect(long key);

    /// <summary>
    /// Creates a placeholder for a container entry.
    /// </summary>
    /// <param name="parent">The full path of the placeholder's parent directory.</param>
    /// <param name="fileName">The name of the placeholder to create.</param>
    /// <param name="entry">The container metadata for the placeholder.</param>
    void CreatePlaceholder(string parent, string fileName, ContainerEntry entry);

    /// <summary>
    /// Validates that a placeholder matches its container entry.
    /// </summary>
    /// <param name="path">The full path of the placeholder to validate.</param>
    /// <param name="entry">The container metadata expected for the placeholder.</param>
    void ValidatePlaceholder(string path, ContainerEntry entry);

    /// <summary>
    /// Checks whether a directory is registered as a Cloud Files sync root.
    /// </summary>
    /// <param name="root">The full path of the directory to check.</param>
    /// <returns><see langword="true" /> if the directory is registered; otherwise, <see langword="false" />.</returns>
    bool IsRegistered(string root);

    /// <summary>
    /// Verifies the registered root is attributed to this Cloud Files provider.
    /// Called during explicit cleanup only; a suffix alone is not ownership evidence.
    /// </summary>
    /// <param name="root">The full path of the registered sync root.</param>
    /// <returns><see langword="true" /> when the registration is owned by this provider; otherwise, <see langword="false" />.</returns>
    bool IsOwnedSyncRoot(string root);

    /// <summary>
    /// Hydrates the contents associated with a file handle.
    /// </summary>
    /// <param name="handle">The file handle to hydrate.</param>
    /// <param name="cancellationToken">A token that cancels the hydration operation.</param>
    /// <returns>A task that completes when hydration finishes.</returns>
    Task HydrateAsync(SafeFileHandle handle, CancellationToken cancellationToken);
}
