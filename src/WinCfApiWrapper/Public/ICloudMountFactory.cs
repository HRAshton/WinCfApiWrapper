namespace WinCfApiWrapper.Public;

/// <summary>
/// Creates independent mounts. Call MountAsync before accessing files.
/// </summary>
public interface ICloudMountFactory
{
    /// <summary>
    /// Creates an unmounted instance configured for the specified content container and sync root.
    /// </summary>
    /// <param name="options">The content container, sync root path, and mount options.</param>
    /// <returns>A mount that must be started with <see cref="ICloudMount.MountAsync" />.</returns>
    ICloudMount Open(CloudMountOptions options);
}
