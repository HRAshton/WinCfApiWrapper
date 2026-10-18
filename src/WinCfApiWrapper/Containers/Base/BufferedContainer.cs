using System.Security.Cryptography;
using WinCfApiWrapper.Public;
using WinCfApiWrapper.Public.Containers;

namespace WinCfApiWrapper.Containers.Base;

/// <summary>Indexes formats that must be fully decoded before their entries can be read independently.</summary>
public abstract class BufferedContainer : IContentContainer
{
    private readonly SemaphoreSlim _indexLock = new(1, 1);
    private ContainerCatalog? _catalog;
    private Dictionary<string, byte[]>? _contents;

    /// <summary>Initializes a buffered container with default resource limits.</summary>
    protected BufferedContainer()
        : this(null) { }

    /// <summary>Initializes a buffered container with validated resource limits.</summary>
    /// <param name="limits">Optional limits on decoded bytes and entries.</param>
    protected BufferedContainer(ArchiveResourceLimits? limits)
    {
        Limits = ArchiveResourceLimits.Resolve(limits);
    }

    /// <summary>Gets the decoding limits for this container.</summary>
    protected ArchiveResourceLimits Limits { get; }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyCollection<ContainerEntry>> GetEntriesAsync(
        CancellationToken cancellationToken = default
    )
    {
        await EnsureIndexedAsync(cancellationToken).ConfigureAwait(false);
        ContainerCatalog catalog = _catalog!;
        return catalog
            .Directories.Select(path => new ContainerEntry(path, 0, string.Empty, true))
            .Concat(catalog.Entries.Values)
            .ToArray();
    }

    /// <inheritdoc />
    public async ValueTask<Stream> OpenReadAsync(
        ContainerEntry entry,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(entry);
        cancellationToken.ThrowIfCancellationRequested();
        if (_catalog is null)
        {
            throw new InvalidOperationException(
                "The container must be indexed before opening entries."
            );
        }

        bool matchesSnapshot =
            _catalog.Entries.TryGetValue(entry.RelativePath, out ContainerEntry? expected)
            && expected.Length == entry.Length
            && expected.Version == entry.Version;
        if (!matchesSnapshot)
        {
            throw new InvalidDataException(
                $"Entry does not belong to this container snapshot: {entry.RelativePath}"
            );
        }

        await EnsureIndexedAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return new MemoryStream(_contents![entry.RelativePath], false);
    }

    /// <summary>Decodes this container into independently readable entry contents.</summary>
    /// <param name="cancellationToken">Cancels decoding.</param>
    /// <returns>The decoded files and directories.</returns>
    protected abstract IEnumerable<BufferedEntry> Decode(CancellationToken cancellationToken);

    private async Task EnsureIndexedAsync(CancellationToken cancellationToken)
    {
        await _indexLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_catalog is not null)
                return;

            IReadOnlyCollection<BufferedEntry> decoded = await Task.Run(
                    () => Decode(cancellationToken).ToArray(),
                    cancellationToken
                )
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            Dictionary<string, byte[]> contents = new(StringComparer.OrdinalIgnoreCase);
            List<ContainerEntry> entries = [];
            long totalBytes = 0;
            foreach (BufferedEntry item in decoded)
            {
                if (entries.Count >= Limits.MaxEntries)
                    throw new InvalidDataException("Container exceeds maximum entry count.");
                if (item.Content.LongLength > Limits.MaxBufferedBytes - totalBytes)
                    throw new InvalidDataException("Container exceeds maximum buffered byte count.");
                totalBytes += item.Content.LongLength;

                string path = ContainerPath.Normalize(item.Path);
                string version = item.IsDirectory
                    ? string.Empty
                    : Convert.ToHexString(SHA256.HashData(item.Content));
                var entry = new ContainerEntry(
                    path,
                    item.Content.LongLength,
                    version,
                    item.IsDirectory
                );
                entries.Add(entry);
                if (!item.IsDirectory && !contents.TryAdd(path, item.Content))
                    throw new InvalidDataException($"Duplicate container path: {path}");
            }

            ContainerCatalog catalog = new(entries);
            _contents = contents;
            _catalog = catalog;
        }
        finally
        {
            _indexLock.Release();
        }
    }
}
