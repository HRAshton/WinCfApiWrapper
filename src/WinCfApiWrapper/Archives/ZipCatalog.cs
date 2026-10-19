using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using WinCfApiWrapper.Containers;

namespace WinCfApiWrapper.Archives;

internal sealed class ZipCatalog
{
    private readonly HashSet<string> _directories = new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, ZipEntryInfo> _entries = new(
        StringComparer.OrdinalIgnoreCase
    );

    private string? _fingerprint;

    public ZipCatalog(Stream stream, int maxEntries)
    {
        using Stream ownedStream = new BufferedStream(stream ?? throw new ArgumentNullException(nameof(stream)), 64 * 1024);
        if (maxEntries <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxEntries));
        using var archive = new ZipArchive(ownedStream, ZipArchiveMode.Read, true);
        var directoryReader = new ZipDirectory(ownedStream);
        int count = 0;
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            if (++count > maxEntries)
                throw new InvalidDataException("ZIP archive exceeds maximum entry count.");
            (long offset, byte[] record) = directoryReader.ReadEntry();
            bool isDirectory = entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\');
            string name = ContainerPath.Normalize(entry.FullName);
            if (name.Length == 0)
                continue;

            if (isDirectory)
            {
                _directories.Add(name);
            }
            else
            {
                bool isDuplicate = !_entries.TryAdd(
                    name,
                    new ZipEntryInfo(
                        name,
                        entry.FullName,
                        entry.Length,
                        entry.CompressedLength,
                        entry.Crc32
                    )
                    {
                        ArchiveLength = ownedStream.Length,
                        DirectoryOffset = offset,
                        DirectoryRecord = record,
                    }
                );
                if (isDuplicate)
                    throw new InvalidDataException($"Duplicate ZIP path: {name}");
            }

            string? parent = isDirectory ? name : Path.GetDirectoryName(name);
            while (!string.IsNullOrEmpty(parent))
            {
                _directories.Add(parent);
                parent = Path.GetDirectoryName(parent);
            }
        }

        foreach (string file in _entries.Keys)
        {
            if (_directories.Contains(file))
                throw new InvalidDataException($"ZIP path is both a file and directory: {file}");
        }
    }

    public IReadOnlyDictionary<string, ZipEntryInfo> Entries => _entries;

    public IReadOnlyCollection<string> Directories => _directories;

    public string Fingerprint => _fingerprint ??= ComputeFingerprint();

    public bool Contains(string path)
    {
        return _entries.ContainsKey(ContainerPath.Normalize(path));
    }

    private string ComputeFingerprint()
    {
        StringBuilder manifest = new();
        foreach (string directory in _directories.OrderBy(name => name, StringComparer.Ordinal))
            manifest.Append('D').Append('\0').Append(directory).Append('\n');

        foreach (
            ZipEntryInfo entry in _entries.Values.OrderBy(e => e.FullName, StringComparer.Ordinal)
        )
        {
            manifest
                .Append('F')
                .Append('\0')
                .Append(entry.FullName)
                .Append('\0')
                .Append(entry.Length)
                .Append('\0')
                .Append(entry.CompressedLength)
                .Append('\0')
                .Append(entry.Crc32)
                .Append('\n');
        }

        return Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(manifest.ToString()))
        );
    }
}
