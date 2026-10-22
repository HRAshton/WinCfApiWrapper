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
/// <param name="limits">Optional archive limits, including an opt-in ZIP central-directory metadata-byte budget; ZIP file contents remain streamed.</param>
/// <param name="validateEntryMetadata">Whether to reread the selected entry's metadata before opening it.</param>
public sealed class ZipContainer(
    Func<CancellationToken, Stream> openArchive,
    ArchiveResourceLimits? limits = null,
    bool validateEntryMetadata = true
) : IContentContainer
{
    private readonly SemaphoreSlim _indexLock = new(1, 1);
    private readonly ArchiveResourceLimits _limits = ArchiveResourceLimits.Resolve(limits);

    private readonly ArchiveStreamSource _source = new(
        openArchive ?? throw new ArgumentNullException(nameof(openArchive))
    );

    private ZipCatalog? _catalog;

    /// <summary>Initializes a new instance of the <see cref="ZipContainer" /> class.</summary>
    /// <param name="openArchive">A factory for independent readable, seekable streams.</param>
    public ZipContainer(Func<CancellationToken, Stream> openArchive)
        : this(openArchive, null) { }

    /// <summary>Initializes a new instance of the <see cref="ZipContainer"/> class with resource limits.</summary>
    /// <param name="openArchive">A factory for independent readable, seekable streams.</param>
    /// <param name="limits">Optional archive resource limits.</param>
    public ZipContainer(Func<CancellationToken, Stream> openArchive, ArchiveResourceLimits? limits)
        : this(openArchive, limits, true) { }

    /// <summary>Creates a container that reads bounded HTTP byte ranges without downloading the archive.</summary>
    /// <param name="client">A caller-owned client with automatic decompression disabled.</param>
    /// <param name="uri">The archive URL; the server must support ranges and version validators.</param>
    /// <param name="limits">Optional archive resource limits.</param>
    /// <param name="validateEntryMetadata">Whether to reread the selected central record; disable only for immutable sources.</param>
    /// <param name="rangeReadTimeout">Optional positive deadline per HTTP range request (including the initial probe and response body). Null preserves the HttpClient/caller timeout behavior.</param>
    /// <returns>A ZIP container backed by independent range streams.</returns>
    /// <remarks>
    /// One initial probe pins the archive length and validator for every stream in this container.
    /// All requests use SendAsync and honor the client's default HTTP version and version policy.
    /// Prefer a strong ETag: Last-Modified cannot detect replacements within its timestamp precision.
    /// A deadline cancels cooperative I/O; it cannot forcibly interrupt a handler that ignores cancellation.
    /// Caller cancellation remains an OperationCanceledException; deadline expiration reports TimeoutException.
    /// </remarks>
    public static ZipContainer FromHttp(
        HttpClient client,
        Uri uri,
        ArchiveResourceLimits? limits = null,
        bool validateEntryMetadata = true,
        TimeSpan? rangeReadTimeout = null
    )
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(uri);
        var source = new HttpArchiveSource(client, uri, rangeReadTimeout);
        return new ZipContainer(source.OpenRead, limits, validateEntryMetadata);
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyCollection<ContainerEntry>> GetEntriesAsync(
        CancellationToken cancellationToken = default
    )
    {
        await _indexLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _catalog ??= await Task.Run(
                    () =>
                        new ZipCatalog(
                            _source.OpenRead(cancellationToken),
                            _limits.MaxEntries,
                            _limits.MaxCentralDirectoryMetadataBytes
                        ),
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

        var reader = new ZipEntryReader(
            ZipDirectory.OpenEntry(
                _source.OpenRead(cancellationToken),
                expectedEntry,
                validateEntryMetadata
            )
        );
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
