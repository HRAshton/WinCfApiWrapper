namespace WinCfApiWrapper.Archives;

internal sealed record ZipEntryInfo(
    string FullName,
    string OriginalName,
    long Length,
    long CompressedLength,
    uint Crc32
)
{
    public long ArchiveLength { get; init; }
    public long DirectoryOffset { get; init; }
    public byte[]? DirectoryRecord { get; init; }
}
