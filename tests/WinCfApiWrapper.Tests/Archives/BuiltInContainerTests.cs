using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using WinCfApiWrapper.Public;
using WinCfApiWrapper.Public.Containers;
using Xunit;

namespace WinCfApiWrapper.Tests.Archives;

public sealed class BuiltInContainerTests
{
    public static TheoryData<
        Func<Func<CancellationToken, Stream>, IContentContainer>,
        Func<Stream, Stream>
    > CompressedContainers =>
        new()
        {
            {
                open => new GZipContainer(open, "payload.bin"),
                stream => new GZipStream(stream, CompressionLevel.Optimal, true)
            },
            {
                open => new BrotliContainer(open, "payload.bin"),
                stream => new BrotliStream(stream, CompressionLevel.Optimal, true)
            },
            {
                open => new DeflateContainer(open, "payload.bin"),
                stream => new DeflateStream(stream, CompressionLevel.Optimal, true)
            },
            {
                open => new ZLibContainer(open, "payload.bin"),
                stream => new ZLibStream(stream, CompressionLevel.Optimal, true)
            },
        };

    [Theory]
    [MemberData(nameof(CompressedContainers))]
    public async Task CompressedContainer_IndexesAndReadsSingleFile(
        Func<Func<CancellationToken, Stream>, IContentContainer> createContainer,
        Func<Stream, Stream> createEncoder
    )
    {
        CancellationToken testCancellationToken = TestContext.Current.CancellationToken;

        byte[] source = Encoding.UTF8.GetBytes("compressed payload");
        byte[] encoded = Encode(source, createEncoder);
        IContentContainer container = createContainer(_ => new MemoryStream(encoded));

        IReadOnlyCollection<ContainerEntry> entries = await container.GetEntriesAsync(
            testCancellationToken
        );
        ContainerEntry entry = Assert.Single(entries);
        Assert.Equal("payload.bin", entry.RelativePath);
        Assert.Equal(source.Length, entry.Length);

        await using Stream contents = await container.OpenReadAsync(entry, testCancellationToken);
        using var reader = new StreamReader(contents);
        Assert.Equal("compressed payload", await reader.ReadToEndAsync(testCancellationToken));
    }

    [Fact]
    public async Task CompressedContainer_RejectsCorruptInput()
    {
        var container = new GZipContainer(_ => new MemoryStream([1, 2, 3]));

        await Assert.ThrowsAnyAsync<InvalidDataException>(async () =>
            await container.GetEntriesAsync(TestContext.Current.CancellationToken)
        );
    }

    [Fact]
    public async Task BufferedContainer_OpenReadRequiresItsIndexedSnapshot()
    {
        CancellationToken testCancellationToken = TestContext.Current.CancellationToken;

        byte[] encoded = Encode(
            Encoding.UTF8.GetBytes("payload"),
            stream => new GZipStream(stream, CompressionLevel.Optimal, true)
        );
        var container = new GZipContainer(_ => new MemoryStream(encoded));
        var unindexedEntry = new ContainerEntry("payload.bin", 7, "version");

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await container.OpenReadAsync(unindexedEntry, testCancellationToken)
        );

        ContainerEntry entry = Assert.Single(
            await container.GetEntriesAsync(testCancellationToken)
        );
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await container.OpenReadAsync(entry with { Version = "stale" }, testCancellationToken)
        );
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await container.OpenReadAsync(
                entry with
                {
                    Length = entry.Length + 1,
                },
                testCancellationToken
            )
        );

        await using Stream contents = await container.OpenReadAsync(entry, testCancellationToken);
        using var reader = new StreamReader(contents);
        Assert.Equal("payload", await reader.ReadToEndAsync(testCancellationToken));
    }

    [Fact]
    public void TarContainer_NullArchiveFactory_IsRejected()
    {
        ArgumentNullException exception = Assert.Throws<ArgumentNullException>(() =>
            new TarContainer(null!)
        );
        Assert.Equal("openArchive", exception.ParamName);
    }

    [Fact]
    public void TarGZipContainer_NullArchiveFactory_IsRejected()
    {
        ArgumentNullException exception = Assert.Throws<ArgumentNullException>(() =>
            new TarGZipContainer(null!)
        );
        Assert.Equal("openArchive", exception.ParamName);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TarContainer_IndexesDirectoriesAndReadsFiles(bool gzip)
    {
        CancellationToken testCancellationToken = TestContext.Current.CancellationToken;

        byte[] archive = BuildTar(gzip);
        IContentContainer container = gzip
            ? new TarGZipContainer(_ => new MemoryStream(archive))
            : new TarContainer(_ => new MemoryStream(archive));

        IReadOnlyCollection<ContainerEntry> entries = await container.GetEntriesAsync(
            testCancellationToken
        );
        Assert.Contains(entries, entry => entry.IsDirectory && entry.RelativePath == "docs");
        ContainerEntry file = Assert.Single(entries, entry => !entry.IsDirectory);
        Assert.Equal("docs\\hello.txt", file.RelativePath);
        Assert.Equal(5, file.Length);

        await using Stream contents = await container.OpenReadAsync(file, testCancellationToken);
        using var reader = new StreamReader(contents);
        Assert.Equal("hello", await reader.ReadToEndAsync(testCancellationToken));
    }

    [Fact]
    public async Task TarContainer_RejectsUnsafeEntryPath()
    {
        byte[] archive = BuildTar(false, "../escape.txt");
        var container = new TarContainer(_ => new MemoryStream(archive));

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await container.GetEntriesAsync(TestContext.Current.CancellationToken)
        );
    }

    [Fact]
    public async Task TarContainer_RejectsLinksInsteadOfExposingThemAsFiles()
    {
        using var output = new MemoryStream();
        using (var writer = new TarWriter(output, true))
        {
            writer.WriteEntry(
                new PaxTarEntry(TarEntryType.SymbolicLink, "link") { LinkName = "target" }
            );
        }

        var container = new TarContainer(_ => new MemoryStream(output.ToArray()));

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await container.GetEntriesAsync(TestContext.Current.CancellationToken)
        );
    }

    private static byte[] Encode(byte[] content, Func<Stream, Stream> createEncoder)
    {
        using var output = new MemoryStream();
        using (Stream encoder = createEncoder(output))
            encoder.Write(content);
        return output.ToArray();
    }

    private static byte[] BuildTar(bool gzip, string path = "docs/hello.txt")
    {
        using var output = new MemoryStream();
        Stream archiveStream = gzip
            ? new GZipStream(output, CompressionLevel.Optimal, true)
            : output;
        using (archiveStream)
        using (var writer = new TarWriter(archiveStream, true))
        {
            if (path == "docs/hello.txt")
                writer.WriteEntry(new PaxTarEntry(TarEntryType.Directory, "docs"));

            writer.WriteEntry(
                new PaxTarEntry(TarEntryType.RegularFile, path)
                {
                    DataStream = new MemoryStream(Encoding.UTF8.GetBytes("hello")),
                }
            );
        }

        return output.ToArray();
    }
}
