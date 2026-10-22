using System.IO.Compression;
using WinCfApiWrapper.Containers;
using WinCfApiWrapper.Containers.Base;

namespace WinCfApiWrapper.Public.Containers;

/// <summary>Exposes the regular files and directories in a GZip-compressed TAR archive.</summary>
/// <param name="openArchive">A factory that returns a readable compressed TAR stream.</param>
/// <param name="limits">Optional limits on total decoded bytes and entries.</param>
public sealed class TarGZipContainer(
    Func<CancellationToken, Stream> openArchive,
    ArchiveResourceLimits? limits = null
) : BufferedContainer(limits)
{
    private readonly Func<CancellationToken, Stream> _openArchive =
        openArchive ?? throw new ArgumentNullException(nameof(openArchive));

    /// <summary>Initializes a new instance of the <see cref="TarGZipContainer" /> class.</summary>
    /// <param name="openArchive">A factory that returns a readable archive stream.</param>
    public TarGZipContainer(Func<CancellationToken, Stream> openArchive)
        : this(openArchive, null) { }

    /// <inheritdoc />
    protected override IEnumerable<BufferedEntry> Decode(CancellationToken cancellationToken)
    {
        using Stream source = ContainerStreamSource.OpenReadable(
            _openArchive,
            cancellationToken,
            "TAR.GZ"
        );
        using var gzip = new GZipStream(source, CompressionMode.Decompress);
        foreach (BufferedEntry entry in TarArchiveDecoder.Read(gzip, cancellationToken, Limits))
            yield return entry;
    }
}
