using WinCfApiWrapper.Public.Containers;

namespace WinCfApiWrapper.Containers.Base;

/// <summary>Base class for containers that decode one compressed stream into a single file.</summary>
public abstract class CompressedContainer : BufferedContainer
{
    private readonly string _entryName;
    private readonly Func<CancellationToken, Stream> _openCompressedStream;

    /// <summary>Initializes a new instance of the <see cref="CompressedContainer" /> class.</summary>
    /// <param name="openCompressedStream">A factory that returns a readable compressed stream.</param>
    /// <param name="entryName">The file name exposed inside the mount.</param>
    /// <param name="limits">Optional decoding resource limits.</param>
    protected CompressedContainer(
        Func<CancellationToken, Stream> openCompressedStream,
        string entryName = "content",
        ArchiveResourceLimits? limits = null
    )
        : base(limits)
    {
        _openCompressedStream =
            openCompressedStream ?? throw new ArgumentNullException(nameof(openCompressedStream));
        _entryName = ContainerPath.Normalize(entryName);
        if (_entryName.Length == 0)
            throw new ArgumentException("A file name is required.", nameof(entryName));
    }

    /// <summary>Creates the decoder for this compression format.</summary>
    /// <param name="source">The compressed input stream.</param>
    /// <returns>A stream that yields the decoded content.</returns>
    protected abstract Stream CreateDecoder(Stream source);

    /// <inheritdoc />
    protected sealed override IEnumerable<BufferedEntry> Decode(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using Stream source = ContainerStreamSource.OpenReadable(
            _openCompressedStream,
            cancellationToken,
            "Compressed"
        );
        using Stream decoder = CreateDecoder(source);
        using var content = new MemoryStream();
        byte[] buffer = new byte[81920];
        int read;
        while ((read = decoder.Read(buffer, 0, buffer.Length)) != 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (read > Limits.MaxBufferedBytes - content.Length)
                throw new InvalidDataException("Compressed content exceeds maximum buffered byte count.");
            content.Write(buffer, 0, read);
        }

        cancellationToken.ThrowIfCancellationRequested();
        yield return new BufferedEntry(_entryName, content.ToArray());
    }
}
