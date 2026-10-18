using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using WinCfApiWrapper.Public;
using WinCfApiWrapper.Public.Containers;
using Xunit;

namespace WinCfApiWrapper.Tests.Archives;

public sealed class ArchiveResourceLimitsTests
{
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
        Assert.Equal(2, (await accepted.GetEntriesAsync(TestContext.Current.CancellationToken)).Count);
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

    private static IContentContainer CreateTar(bool gzip, byte[] bytes, ArchiveResourceLimits limits)
        => gzip
            ? new TarGZipContainer(_ => new MemoryStream(bytes), limits)
            : new TarContainer(_ => new MemoryStream(bytes), limits);

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
}
