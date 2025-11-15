namespace WinCfApiWrapper.Public;

/// <summary>Describes a file or directory exposed by a content container.</summary>
/// <param name="RelativePath">A relative path using either slash style.</param>
/// <param name="Length">The file length in bytes; ignored for directories.</param>
/// <param name="Version">A nonempty stable value that changes when this file's contents change.</param>
/// <param name="IsDirectory">Whether the entry represents a directory.</param>
public sealed record ContainerEntry(
    string RelativePath,
    long Length,
    string Version,
    bool IsDirectory = false
);
