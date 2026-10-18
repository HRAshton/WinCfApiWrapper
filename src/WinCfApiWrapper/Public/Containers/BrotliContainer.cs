using System.IO.Compression;
using WinCfApiWrapper.Containers.Base;

namespace WinCfApiWrapper.Public.Containers;

/// <summary>Exposes a Brotli stream as one file.</summary>
/// <param name="openStream">A factory for the compressed stream.</param>
/// <param name="entryName">The file name exposed in the container.</param>
/// <param name="limits">Optional decoding limits.</param>
public sealed class BrotliContainer(
    Func<CancellationToken, Stream> openStream,
    string entryName = "content",
    ArchiveResourceLimits? limits = null
) : CompressedContainer(openStream, entryName, limits)
{
    /// <summary>Initializes a new instance of the <see cref="BrotliContainer" /> class.</summary>
    /// <param name="openStream">A factory that returns compressed data.</param>
    /// <param name="entryName">The file name exposed inside the mount.</param>
    public BrotliContainer(Func<CancellationToken, Stream> openStream, string entryName)
        : this(openStream, entryName, null) { }

    /// <inheritdoc />
    protected override Stream CreateDecoder(Stream source)
    {
        return new BrotliStream(source, CompressionMode.Decompress);
    }
}
