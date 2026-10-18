using System.Formats.Tar;
using WinCfApiWrapper.Containers.Base;
using WinCfApiWrapper.Public.Containers;

namespace WinCfApiWrapper.Containers;

internal static class TarArchiveDecoder
{
    public static IEnumerable<BufferedEntry> Read(
        Stream stream,
        CancellationToken cancellationToken,
        ArchiveResourceLimits limits
    )
    {
        using var reader = new TarReader(stream, true);
        int count = 0;
        long totalBytes = 0;
        while (reader.GetNextEntry() is { } entry)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++count > limits.MaxEntries)
                throw new InvalidDataException("TAR archive exceeds maximum entry count.");
            bool isDirectory = entry.EntryType == TarEntryType.Directory;
            bool isRegularFile =
                entry.EntryType
                is TarEntryType.RegularFile
                    or TarEntryType.V7RegularFile
                    or TarEntryType.ContiguousFile;
            if (!isDirectory && !isRegularFile)
            {
                throw new InvalidDataException(
                    $"Unsupported TAR entry type {entry.EntryType}: {entry.Name}"
                );
            }

            long remaining = limits.MaxBufferedBytes - totalBytes;
            if (!isDirectory && entry.Length > remaining)
                throw new InvalidDataException("TAR archive exceeds maximum buffered byte count.");

            using var content = new MemoryStream();
            if (!isDirectory && entry.DataStream is { } data)
            {
                totalBytes += CopyTo(data, content, cancellationToken, remaining);
            }

            string name = entry.Name;
            while (name.StartsWith("./", StringComparison.Ordinal))
                name = name[2..];
            if (name.Length != 0)
                yield return new BufferedEntry(name, content.ToArray(), isDirectory);
        }
    }

    private static long CopyTo(
        Stream source,
        Stream destination,
        CancellationToken cancellationToken,
        long maximumBytes
    )
    {
        byte[] buffer = new byte[81920];
        long copied = 0;
        int read;
        while ((read = source.Read(buffer, 0, buffer.Length)) != 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (read > maximumBytes - copied)
                throw new InvalidDataException("TAR archive exceeds maximum buffered byte count.");
            destination.Write(buffer, 0, read);
            copied += read;
        }

        return copied;
    }
}
