using WinCfApiWrapper.Containers;
using WinCfApiWrapper.Containers.Base;

namespace WinCfApiWrapper.Public.Containers;

/// <summary>Exposes the regular files and directories in a TAR archive.</summary>
/// <param name="openArchive">A factory that returns a readable TAR stream.</param>
/// <param name="limits">Optional limits on total decoded bytes and entries.</param>
public sealed class TarContainer(
    Func<CancellationToken, Stream> openArchive,
    ArchiveResourceLimits? limits = null
) : BufferedContainer(limits)
{
    private readonly Func<CancellationToken, Stream> _openArchive =
        openArchive ?? throw new ArgumentNullException(nameof(openArchive));

    /// <summary>Initializes a new instance of the <see cref="TarContainer" /> class.</summary>
    /// <param name="openArchive">A factory that returns a readable archive stream.</param>
    public TarContainer(Func<CancellationToken, Stream> openArchive)
        : this(openArchive, null) { }

    /// <inheritdoc />
    protected override IEnumerable<BufferedEntry> Decode(CancellationToken cancellationToken)
    {
        using Stream stream = ContainerStreamSource.OpenReadable(
            _openArchive,
            cancellationToken,
            "TAR"
        );
        foreach (BufferedEntry entry in TarArchiveDecoder.Read(stream, cancellationToken, Limits))
            yield return entry;
    }
}
