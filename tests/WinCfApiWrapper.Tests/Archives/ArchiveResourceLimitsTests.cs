using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using WinCfApiWrapper.Containers.Base;
using WinCfApiWrapper.Public;
using WinCfApiWrapper.Public.Containers;
using Xunit;

namespace WinCfApiWrapper.Tests.Archives;

public sealed class ArchiveResourceLimitsTests
{
    public static bool RunArchiveStress =>
        Environment.GetEnvironmentVariable("RZM_RUN_ARCHIVE_STRESS") == "1";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Tar_RootDirectoryCountsAgainstEntryBudget(bool gzip)
    {
        using var output = new MemoryStream();
        Stream archiveStream = gzip
            ? new GZipStream(output, CompressionLevel.Optimal, true)
            : output;
        using (var writer = new TarWriter(archiveStream, true))
        {
            writer.WriteEntry(new PaxTarEntry(TarEntryType.Directory, "./"));
            using var content = new MemoryStream([1]);
            writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "file.txt")
            {
                DataStream = content,
            });
        }

        if (gzip)
            archiveStream.Dispose();

        byte[] bytes = output.ToArray();
        var limits = new ArchiveResourceLimits { MaxEntries = 1, MaxBufferedBytes = 1 };
        IContentContainer rejected = CreateTar(gzip, bytes, limits);
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await rejected.GetEntriesAsync(TestContext.Current.CancellationToken)
        );

        IContentContainer accepted = CreateTar(gzip, bytes, limits with { MaxEntries = 2 });
        ContainerEntry entry = Assert.Single(
            await accepted.GetEntriesAsync(TestContext.Current.CancellationToken)
        );
        Assert.Equal("file.txt", entry.RelativePath);
        Assert.Equal(1, entry.Length);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Tar_BoundsTotalDecodedBytesAndEntryCount(bool gzip)
    {
        byte[] source = MakeTar(gzip);
        IContentContainer bytesLimited = CreateTar(gzip, source, new ArchiveResourceLimits
        {
            MaxEntries = 2,
            MaxBufferedBytes = 7,
        });
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await bytesLimited.GetEntriesAsync(TestContext.Current.CancellationToken));

        IContentContainer entriesLimited = CreateTar(gzip, source, new ArchiveResourceLimits
        {
            MaxEntries = 1,
            MaxBufferedBytes = 8,
        });
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await entriesLimited.GetEntriesAsync(TestContext.Current.CancellationToken));

        IContentContainer accepted = CreateTar(gzip, source, new ArchiveResourceLimits
        {
            MaxEntries = 2,
            MaxBufferedBytes = 8,
        });
        Assert.Equal(2, (await accepted.GetEntriesAsync(TestContext.Current.CancellationToken)).Count);
    }

    [Fact]
    public async Task CompressedStream_BoundsDecodedBytes()
    {
        using var output = new MemoryStream();
        using (var compressor = new GZipStream(output, CompressionLevel.Optimal, true))
            compressor.Write(new byte[64]);

        byte[] encoded = output.ToArray();
        var rejected = new GZipContainer(
            _ => new MemoryStream(encoded),
            limits: new ArchiveResourceLimits { MaxBufferedBytes = 63 });
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await rejected.GetEntriesAsync(TestContext.Current.CancellationToken));

        var accepted = new GZipContainer(
            _ => new MemoryStream(encoded),
            limits: new ArchiveResourceLimits { MaxBufferedBytes = 64 });
        Assert.Equal(64, Assert.Single(
            await accepted.GetEntriesAsync(TestContext.Current.CancellationToken)).Length);
    }

    [Theory]
    [InlineData("Brotli")]
    [InlineData("Deflate")]
    [InlineData("ZLib")]
    public async Task OtherCompressedFormats_EnforceDecodedByteLimit(string format)
    {
        using var output = new MemoryStream();
        using (Stream compressor = format switch
        {
            "Brotli" => new BrotliStream(output, CompressionLevel.Optimal, true),
            "Deflate" => new DeflateStream(output, CompressionLevel.Optimal, true),
            "ZLib" => new ZLibStream(output, CompressionLevel.Optimal, true),
            _ => throw new ArgumentOutOfRangeException(nameof(format)),
        })
        {
            compressor.Write(new byte[64]);
        }

        byte[] data = output.ToArray();
        var limits = new ArchiveResourceLimits { MaxBufferedBytes = 63 };
        IContentContainer container = format switch
        {
            "Brotli" => new BrotliContainer(_ => new MemoryStream(data), limits: limits),
            "Deflate" => new DeflateContainer(_ => new MemoryStream(data), limits: limits),
            "ZLib" => new ZLibContainer(_ => new MemoryStream(data), limits: limits),
            _ => throw new ArgumentOutOfRangeException(nameof(format)),
        };

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await container.GetEntriesAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Zip_BoundsEntriesButStreamsBytes()
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, true))
        {
            foreach (string name in new[] { "one", "two" })
            {
                using var writer = new StreamWriter(archive.CreateEntry(name).Open());
                writer.Write("payload");
            }
        }

        byte[] data = output.ToArray();
        var limits = new ArchiveResourceLimits { MaxEntries = 1, MaxBufferedBytes = 1 };
        var rejected = new ZipContainer(_ => new MemoryStream(data), limits);
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await rejected.GetEntriesAsync(TestContext.Current.CancellationToken));

        var accepted = new ZipContainer(_ => new MemoryStream(data),
            limits with { MaxEntries = 2 });
        IReadOnlyCollection<ContainerEntry> entries =
            await accepted.GetEntriesAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, entries.Count);

        await using Stream bytes = await accepted.OpenReadAsync(
            entries.Single(entry => entry.RelativePath == "one"),
            TestContext.Current.CancellationToken
        );
        using var reader = new StreamReader(bytes);
        Assert.Equal("payload", await reader.ReadToEndAsync(TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(10_000, 10_000, true)]
    [InlineData(10_001, 10_000, false)]
    [InlineData(12_000, 12_000, true)]
    public async Task Zip_EnforcesConfiguredEntryBudget(int fileCount, int maxEntries, bool accepted)
    {
        byte[] archive = MakeZip(fileCount);
        var container = new ZipContainer(
            _ => new MemoryStream(archive),
            new ArchiveResourceLimits { MaxEntries = maxEntries }
        );

        if (!accepted)
        {
            await Assert.ThrowsAsync<InvalidDataException>(async () =>
                await container.GetEntriesAsync(TestContext.Current.CancellationToken)
            );
            return;
        }

        IReadOnlyCollection<ContainerEntry> entries = await container.GetEntriesAsync(
            TestContext.Current.CancellationToken
        );
        Assert.Equal(fileCount, entries.Count);
        Assert.Contains(entries, entry => entry.RelativePath == $"file-{fileCount - 1:D6}.txt");
    }

    [Fact(
        Skip = "Set RZM_RUN_ARCHIVE_STRESS=1 to run archive stress tests.",
        SkipUnless = nameof(RunArchiveStress)
    )]
    [Trait("Category", "ArchiveStress")]
    public async Task Zip_DefaultLimits_IndexTwoHundredThousandFiles()
    {
        const int fileCount = 200_000;

        byte[] archive = MakeZip(fileCount);
        var container = new ZipContainer(_ => new MemoryStream(archive));

        IReadOnlyCollection<ContainerEntry> entries = await container.GetEntriesAsync(
            TestContext.Current.CancellationToken
        );

        Assert.Equal(fileCount, entries.Count);
        Assert.Contains(entries, entry => entry.RelativePath == "file-199999.txt");
    }

    [Fact]
    public void RejectsNonPositiveLimits()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new TarContainer(_ => new MemoryStream(),
                new ArchiveResourceLimits { MaxEntries = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new GZipContainer(_ => new MemoryStream(),
                limits: new ArchiveResourceLimits { MaxBufferedBytes = 0 }));
    }

    [Fact]
    public async Task BufferedContainer_DefensivelyChecksEntryCount()
    {
        var limits = new ArchiveResourceLimits { MaxEntries = 1 };
        var container = new TestBufferedContainer(
            limits,
            new BufferedEntry("one", [1]),
            new BufferedEntry("two", [2])
        );

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await container.GetEntriesAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task BufferedContainer_DefensivelyChecksAggregateBytes()
    {
        var limits = new ArchiveResourceLimits { MaxBufferedBytes = 1 };
        var container = new TestBufferedContainer(
            limits,
            new BufferedEntry("one", [1]),
            new BufferedEntry("two", [2])
        );

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await container.GetEntriesAsync(TestContext.Current.CancellationToken));
    }

    private static IContentContainer CreateTar(bool gzip, byte[] bytes, ArchiveResourceLimits limits)
        => gzip
            ? new TarGZipContainer(_ => new MemoryStream(bytes), limits)
            : new TarContainer(_ => new MemoryStream(bytes), limits);

    private static byte[] MakeZip(int fileCount)
    {
        using var output = new MemoryStream();
        using (var writer = new ZipArchive(output, ZipArchiveMode.Create, true))
        {
            for (int i = 0; i < fileCount; i++)
                writer.CreateEntry($"file-{i:D6}.txt");
        }

        return output.ToArray();
    }

    private static byte[] MakeTar(bool gzip)
    {
        using var output = new MemoryStream();
        Stream compressed = gzip
            ? new GZipStream(output, CompressionLevel.Optimal, true)
            : output;
        using (compressed)
        using (var writer = new TarWriter(compressed, true))
        {
            for (int i = 0; i < 2; i++)
            {
                using var data = new MemoryStream(Encoding.UTF8.GetBytes("1234"));
                writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, $"file{i}.txt")
                {
                    DataStream = data,
                });
            }
        }

        return output.ToArray();
    }

    private sealed class TestBufferedContainer(
        ArchiveResourceLimits limits,
        params BufferedEntry[] decoded
    ) : BufferedContainer(limits)
    {
        protected override IEnumerable<BufferedEntry> Decode(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return decoded;
        }
    }
}
