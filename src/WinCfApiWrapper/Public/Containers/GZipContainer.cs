using System.IO.Compression;
using WinCfApiWrapper.Containers.Base;

namespace WinCfApiWrapper.Public.Containers;

/// <summary>Exposes a GZip stream as one file.</summary>
/// <param name="openStream">A factory for the compressed stream.</param>
/// <param name="entryName">The file name exposed in the container.</param>
/// <param name="limits">Optional decoding limits.</param>
public sealed class GZipContainer(
    Func<CancellationToken, Stream> openStream,
    string entryName = "content",
    ArchiveResourceLimits? limits = null
) : CompressedContainer(openStream, entryName, limits)
{
    /// <inheritdoc />
    protected override Stream CreateDecoder(Stream source)
    {
        return new GZipStream(source, CompressionMode.Decompress);
    }
}
