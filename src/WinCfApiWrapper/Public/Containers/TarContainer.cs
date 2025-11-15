using WinCfApiWrapper.Containers;
using WinCfApiWrapper.Containers.Base;

namespace WinCfApiWrapper.Public.Containers;

/// <summary>Exposes the regular files and directories in a TAR archive.</summary>
/// <param name="openArchive">A factory that returns a readable TAR stream.</param>
public sealed class TarContainer(Func<CancellationToken, Stream> openArchive) : BufferedContainer
{
    private readonly Func<CancellationToken, Stream> _openArchive =
        openArchive ?? throw new ArgumentNullException(nameof(openArchive));

    /// <inheritdoc />
    protected override IEnumerable<BufferedEntry> Decode(CancellationToken cancellationToken)
    {
        using Stream stream = ContainerStreamSource.OpenReadable(
            _openArchive,
            cancellationToken,
            "TAR"
        );
        foreach (BufferedEntry entry in TarArchiveDecoder.Read(stream, cancellationToken))
            yield return entry;
    }
}
