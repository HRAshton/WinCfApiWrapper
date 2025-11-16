namespace WinCfApiWrapper.Archives;

internal sealed record ZipEntryInfo(
    string FullName,
    string OriginalName,
    long Length,
    long CompressedLength,
    uint Crc32
);
