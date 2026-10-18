using System.IO.Compression;
using System.Text;
using WinCfApiWrapper.Containers.Base;
using WinCfApiWrapper.Public;
using Xunit;

namespace WinCfApiWrapper.Tests.Archives;

public sealed class ArchiveCompatibilityTests
{
    [Fact]
    public async Task BufferedSubclass_ParameterlessBaseConstructor_IndexesAndReads()
    {
        var container = new LegacyBufferedContainer(new BufferedEntry("file.txt", [1, 2, 3]));
        CancellationToken token = TestContext.Current.CancellationToken;

        ContainerEntry entry = Assert.Single(await container.GetEntriesAsync(token));
        Assert.Equal("file.txt", entry.RelativePath);
        Assert.Equal(3, entry.Length);
        await using Stream opened = await container.OpenReadAsync(entry, token);
        using var contents = new MemoryStream();
        await opened.CopyToAsync(contents, token);
        Assert.Equal(new byte[] { 1, 2, 3 }, contents.ToArray());
    }

    [Fact]
    public async Task CompressedSubclass_TwoArgumentBaseConstructor_IndexesAndReleasesSource()
    {
        using var encoded = new MemoryStream();
        using (var compressor = new GZipStream(encoded, CompressionLevel.Optimal, true))
            compressor.Write(Encoding.UTF8.GetBytes("payload"));

        using var source = new MemoryStream(encoded.ToArray());
        var container = new LegacyCompressedContainer(_ => source, "file.txt");
        CancellationToken token = TestContext.Current.CancellationToken;

        ContainerEntry entry = Assert.Single(await container.GetEntriesAsync(token));
        Assert.False(source.CanRead);
        Assert.Equal("file.txt", entry.RelativePath);
        await using Stream opened = await container.OpenReadAsync(entry, token);
        using var reader = new StreamReader(opened);
        Assert.Equal("payload", await reader.ReadToEndAsync(token));
    }

    [Fact]
    public void CompressedSubclass_RejectsMissingFactoryAndEmptyName()
    {
        Assert.Throws<ArgumentNullException>(() => new LegacyCompressedContainer(null!, "file.txt"));
        Assert.Throws<ArgumentException>(() => new LegacyCompressedContainer(_ => new MemoryStream(), string.Empty));
    }

    [Fact]
    public async Task BufferedSubclass_RejectsDuplicateDecodedPaths()
    {
        var container = new LegacyBufferedContainer(
            new BufferedEntry("file.txt", [1]),
            new BufferedEntry("FILE.TXT", [2])
        );

        InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await container.GetEntriesAsync(TestContext.Current.CancellationToken)
        );

        Assert.Contains("Duplicate container path", error.Message);
    }

    private sealed class LegacyBufferedContainer(params BufferedEntry[] entries) : BufferedContainer
    {
        protected override IEnumerable<BufferedEntry> Decode(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return entries;
        }
    }

    private sealed class LegacyCompressedContainer(
        Func<CancellationToken, Stream> openStream,
        string entryName
    ) : CompressedContainer(openStream, entryName)
    {
        protected override Stream CreateDecoder(Stream source)
        {
            return new GZipStream(source, CompressionMode.Decompress);
        }
    }
}
