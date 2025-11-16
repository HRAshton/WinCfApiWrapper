using System.Formats.Tar;
using WinCfApiWrapper.Containers.Base;

namespace WinCfApiWrapper.Containers;

internal static class TarArchiveDecoder
{
    public static IEnumerable<BufferedEntry> Read(
        Stream stream,
        CancellationToken cancellationToken
    )
    {
        using var reader = new TarReader(stream, true);
        while (reader.GetNextEntry() is { } entry)
        {
            cancellationToken.ThrowIfCancellationRequested();
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

            using var content = new MemoryStream();
            if (!isDirectory && entry.DataStream is { } data)
            {
                CopyTo(data, content, cancellationToken);
            }

            string name = entry.Name;
            while (name.StartsWith("./", StringComparison.Ordinal))
                name = name[2..];
            if (name.Length != 0)
                yield return new BufferedEntry(name, content.ToArray(), isDirectory);
        }
    }

    private static void CopyTo(
        Stream source,
        Stream destination,
        CancellationToken cancellationToken
    )
    {
        byte[] buffer = new byte[81920];
        int read;
        while ((read = source.Read(buffer, 0, buffer.Length)) != 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            destination.Write(buffer, 0, read);
        }
    }
}
