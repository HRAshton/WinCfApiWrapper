namespace WinCfApiWrapper.Containers.Base;

/// <summary>Holds the decoded contents of one file or directory.</summary>
/// <param name="Path">The relative entry path.</param>
/// <param name="Content">The file bytes, or an empty array for a directory.</param>
/// <param name="IsDirectory">Whether this is a directory.</param>
public sealed record BufferedEntry(string Path, byte[] Content, bool IsDirectory = false);
