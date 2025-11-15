using System.IO.Compression;
using WinCfApiWrapper.Containers.Base;

namespace WinCfApiWrapper.Public.Containers;

/// <summary>Exposes a Brotli stream as one file.</summary>
/// <param name="openStream">A factory for the compressed stream.</param>
/// <param name="entryName">The file name exposed in the container.</param>
public sealed class BrotliContainer(
    Func<CancellationToken, Stream> openStream,
    string entryName = "content"
) : CompressedContainer(openStream, entryName)
{
    /// <inheritdoc />
    protected override Stream CreateDecoder(Stream source)
    {
        return new BrotliStream(source, CompressionMode.Decompress);
    }
}
