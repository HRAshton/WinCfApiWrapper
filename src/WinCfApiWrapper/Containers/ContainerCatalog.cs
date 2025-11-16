using System.Security.Cryptography;
using System.Text;
using WinCfApiWrapper.Public;

namespace WinCfApiWrapper.Containers;

internal sealed class ContainerCatalog
{
    private readonly HashSet<string> _directories = new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, ContainerEntry> _entries = new(
        StringComparer.OrdinalIgnoreCase
    );

    public ContainerCatalog(IEnumerable<ContainerEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        foreach (ContainerEntry? entry in entries)
        {
            if (entry is null)
                throw new InvalidDataException("Container returned a null entry.");

            string name = ContainerPath.Normalize(entry.RelativePath);
            if (name.Length == 0)
                throw new InvalidDataException("Container entry path cannot be empty.");
            if (entry.Length < 0)
                throw new InvalidDataException($"Container entry has a negative length: {name}");
            ArgumentNullException.ThrowIfNull(entry.Version);
            if (entry.Version.Contains('\n'))
            {
                throw new InvalidDataException(
                    $"Container entry version contains a line break: {name}"
                );
            }

            if (!entry.IsDirectory && string.IsNullOrWhiteSpace(entry.Version))
                throw new InvalidDataException($"Container file has no content version: {name}");
            if (entry.IsDirectory && (entry.Length != 0 || entry.Version.Length != 0))
            {
                throw new InvalidDataException(
                    $"Container directory must not have a length or content version: {name}"
                );
            }

            ContainerEntry normalized = entry with { RelativePath = name };
            if (entry.IsDirectory)
                _directories.Add(name);
            else if (!_entries.TryAdd(name, normalized))
                throw new InvalidDataException($"Duplicate container path: {name}");

            string? parent = entry.IsDirectory ? name : Path.GetDirectoryName(name);
            while (!string.IsNullOrEmpty(parent))
            {
                _directories.Add(parent);
                parent = Path.GetDirectoryName(parent);
            }
        }

        foreach (string file in _entries.Keys)
        {
            if (_directories.Contains(file))
            {
                throw new InvalidDataException(
                    $"Container path is both a file and directory: {file}"
                );
            }
        }

        StringBuilder manifest = new();
        foreach (string directory in _directories.OrderBy(path => path, StringComparer.Ordinal))
            manifest.Append('D').Append('\0').Append(directory).Append('\n');

        foreach (
            ContainerEntry entry in _entries.Values.OrderBy(
                e => e.RelativePath,
                StringComparer.Ordinal
            )
        )
        {
            manifest
                .Append('F')
                .Append('\0')
                .Append(entry.RelativePath)
                .Append('\0')
                .Append(entry.Length)
                .Append('\0')
                .Append(entry.Version)
                .Append('\n');
        }

        Fingerprint = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(manifest.ToString()))
        );
    }

    public IReadOnlyDictionary<string, ContainerEntry> Entries => _entries;

    public IReadOnlyCollection<string> Directories => _directories;

    public string Fingerprint { get; }

    public bool Contains(string path)
    {
        return _entries.ContainsKey(ContainerPath.Normalize(path));
    }
}
