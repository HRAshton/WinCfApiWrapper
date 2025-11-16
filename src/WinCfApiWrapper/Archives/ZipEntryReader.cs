using System.IO.Compression;

namespace WinCfApiWrapper.Archives;

internal sealed class ZipEntryReader : IDisposable
{
    private readonly ZipArchive _archive;
    private readonly Stream _stream;

    public ZipEntryReader(Stream stream)
    {
        _stream = stream;
        try
        {
            _archive = new ZipArchive(stream, ZipArchiveMode.Read, true);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        _archive.Dispose();
        _stream.Dispose();
    }

    public Stream OpenEntry(ZipEntryInfo expected)
    {
        ZipArchiveEntry actual =
            _archive.GetEntry(expected.OriginalName)
            ?? throw new FileNotFoundException($"ZIP entry missing: {expected.FullName}");
        bool entryChanged =
            actual.Length != expected.Length
            || actual.CompressedLength != expected.CompressedLength
            || actual.Crc32 != expected.Crc32;
        if (entryChanged)
            throw new IOException($"Archive entry changed: {expected.FullName}");

        return actual.Open();
    }
}
