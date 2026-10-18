using WinCfApiWrapper.Archives;

namespace WinCfApiWrapper.Public.Containers;

/// <summary>Exposes a ZIP archive as a content container.</summary>
/// <param name="openArchive">
/// A factory for independent readable, seekable streams positioned anywhere; the container resets each stream to its
/// beginning and disposes it after use.
/// </param>
/// <remarks>
/// The factory may be called concurrently and must return a new stream on every call. For remote archives, it can
/// return a seekable range stream.
/// </remarks>
/// <param name="limits">Optional archive entry limit; ZIP file contents remain streamed.</param>
public sealed class ZipContainer(
    Func<CancellationToken, Stream> openArchive,
    ArchiveResourceLimits? limits = null
) : IContentContainer
{
    private readonly SemaphoreSlim _indexLock = new(1, 1);
    private readonly ArchiveResourceLimits _limits = ArchiveResourceLimits.Resolve(limits);

    private readonly ArchiveStreamSource _source = new(
        openArchive ?? throw new ArgumentNullException(nameof(openArchive))
    );

    private ZipCatalog? _catalog;

    /// <inheritdoc />
    public async ValueTask<IReadOnlyCollection<ContainerEntry>> GetEntriesAsync(
        CancellationToken cancellationToken = default
    )
    {
        await _indexLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _catalog ??= await Task.Run(
                    () => new ZipCatalog(_source.OpenRead(cancellationToken), _limits.MaxEntries),
                    cancellationToken
                )
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
        }
        finally
        {
            _indexLock.Release();
        }

        ZipCatalog catalog = _catalog!;
        var entries = new List<ContainerEntry>(catalog.Entries.Count + catalog.Directories.Count);
        entries.AddRange(
            catalog.Directories.Select(path => new ContainerEntry(path, 0, string.Empty, true))
        );
        entries.AddRange(
            catalog.Entries.Values.Select(entry => new ContainerEntry(
                entry.FullName,
                entry.Length,
                $"{entry.CompressedLength}\0{entry.Crc32}"
            ))
        );

        return entries;
    }

    /// <inheritdoc />
    public ValueTask<Stream> OpenReadAsync(
        ContainerEntry entry,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(entry);
        cancellationToken.ThrowIfCancellationRequested();
        ZipCatalog catalog =
            _catalog
            ?? throw new InvalidOperationException(
                "The ZIP container must be indexed before opening entries."
            );
        bool entryMatchesSnapshot =
            catalog.Entries.TryGetValue(entry.RelativePath, out ZipEntryInfo? expected)
            && expected.Length == entry.Length
            && $"{expected.CompressedLength}\0{expected.Crc32}" == entry.Version;
        if (!entryMatchesSnapshot)
        {
            throw new InvalidDataException(
                $"Entry does not belong to this ZIP snapshot: {entry.RelativePath}"
            );
        }

        ZipEntryInfo expectedEntry = expected!;

        var reader = new ZipEntryReader(_source.OpenRead(cancellationToken));
        try
        {
            Stream stream = reader.OpenEntry(expectedEntry);
            return ValueTask.FromResult<Stream>(new OwnedEntryStream(stream, reader));
        }
        catch
        {
            reader.Dispose();
            throw;
        }
    }

    private sealed class OwnedEntryStream(Stream inner, IDisposable owner) : Stream
    {
        private bool _disposed;

        public override bool CanRead => inner.CanRead;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => inner.Length;

        public override long Position
        {
            get => inner.Position;
            set => throw new NotSupportedException();
        }

        public override async ValueTask DisposeAsync()
        {
            if (!_disposed)
            {
                _disposed = true;
                try
                {
                    await inner.DisposeAsync().ConfigureAwait(false);
                }
                finally
                {
                    owner.Dispose();
                }
            }

            await base.DisposeAsync().ConfigureAwait(false);
            GC.SuppressFinalize(this);
        }

        public override void Flush()
        {
            throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            return inner.Read(buffer, offset, count);
        }

        public override int Read(Span<byte> buffer)
        {
            return inner.Read(buffer);
        }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default
        )
        {
            return inner.ReadAsync(buffer, cancellationToken);
        }

        public override Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken
        )
        {
            return inner.ReadAsync(buffer, offset, count, cancellationToken);
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException();
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                DisposeOwned();

            base.Dispose(disposing);
        }

        private void DisposeOwned()
        {
            if (_disposed)
                return;

            _disposed = true;
            try
            {
                inner.Dispose();
            }
            finally
            {
                owner.Dispose();
            }
        }
    }
}
