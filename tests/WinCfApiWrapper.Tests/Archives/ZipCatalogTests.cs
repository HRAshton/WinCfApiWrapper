using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using WinCfApiWrapper.Archives;
using WinCfApiWrapper.Public.Containers;
using Xunit;

namespace WinCfApiWrapper.Tests.Archives;

public sealed class ZipCatalogTests
{
    [Fact]
    public void Constructor_NullSource_ThrowsArgumentNullException()
    {
        ArgumentNullException error = Assert.Throws<ArgumentNullException>(() =>
            new ZipCatalog(null!, maxEntries: 1)
        );

        Assert.Equal("stream", error.ParamName);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Constructor_NonPositiveEntryLimit_RejectsAndDisposesSource(int maxEntries)
    {
        using MemoryStream source = BuildArchive("file.txt");

        ArgumentOutOfRangeException error = Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ZipCatalog(source, maxEntries)
        );

        Assert.Equal("maxEntries", error.ParamName);
        Assert.False(source.CanRead);
    }

    [Fact]
    public void Constructor_ValidArchive_IndexesFilesAndDirectories()
    {
        using MemoryStream stream = BuildArchive("dir/a.txt", "dir/nested/b.txt");
        var catalog = new ZipCatalog(stream, maxEntries: 2);
        Assert.True(catalog.Contains("dir/a.txt"));
        Assert.Contains(@"dir\nested", catalog.Directories);
        Assert.Equal(2, catalog.Entries.Count);
        Assert.Equal(64, catalog.Fingerprint.Length);
        Assert.Same(catalog.Fingerprint, catalog.Fingerprint);
    }

    [Fact]
    public void Constructor_ValidArchive_ReleasesIndexStreamBeforeReturning()
    {
        MemoryStream stream = BuildArchive("dir/a.txt");

        var catalog = new ZipCatalog(stream, maxEntries: 1);

        Assert.False(stream.CanRead);
        Assert.True(catalog.Contains("dir/a.txt"));
    }

    [Fact]
    public void Constructor_DuplicateCaseInsensitivePath_Throws()
    {
        using MemoryStream stream = BuildArchive("a.txt", "A.txt");
        Assert.Throws<InvalidDataException>(() => new ZipCatalog(stream, maxEntries: 2));
        Assert.False(stream.CanRead);
    }

    [Fact]
    public void Constructor_FileConflictsWithDirectory_Throws()
    {
        using MemoryStream stream = BuildArchive("folder", "folder/file.txt");
        Assert.Throws<InvalidDataException>(() => new ZipCatalog(stream, maxEntries: 2));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Constructor_DeepPathFailsWithoutExpandingThousandsOfParents(bool directory)
    {
        string path = string.Concat(Enumerable.Repeat("a/", 29_000)) + "f";
        if (directory)
            path += "/";

        using MemoryStream archive = BuildArchive(path);
        Assert.Throws<InvalidDataException>(() => new ZipCatalog(archive, maxEntries: 1));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Constructor_Utf8IdentityBudgetRespectsByteBoundary(bool directory)
    {
        string prefix = string.Join('/', Enumerable.Repeat(new string('é', 200), 10));
        string name = prefix + "/" + new string('x', 86);
        Assert.Equal(4096, Encoding.UTF8.GetByteCount(name));
        string suffix = directory ? "/" : string.Empty;

        using (MemoryStream accepted = BuildArchive(name + suffix))
        {
            var catalog = new ZipCatalog(accepted, maxEntries: 1);
            Assert.Equal(
                1,
                directory
                    ? catalog.Directories.Count(d => d == name.Replace('/', '\\'))
                    : catalog.Entries.Count
            );
        }

        using MemoryStream rejected = BuildArchive(name + "y" + suffix);
        Assert.Throws<InvalidDataException>(() => new ZipCatalog(rejected, maxEntries: 1));
    }

    [Fact]
    public void Constructor_SameArchive_ProducesStableFingerprint()
    {
        byte[] bytes;
        using (MemoryStream stream = BuildArchive("a.txt"))
            bytes = stream.ToArray();

        var first = new ZipCatalog(new MemoryStream(bytes), maxEntries: 1);
        var second = new ZipCatalog(new MemoryStream(bytes), maxEntries: 1);
        Assert.Equal(first.Fingerprint, second.Fingerprint);
    }

    [Fact]
    public void Fingerprint_IncludesEmptyDirectories()
    {
        using MemoryStream firstStream = BuildArchive();
        using var secondStream = new MemoryStream();
        using (var archive = new ZipArchive(secondStream, ZipArchiveMode.Create, true))
            archive.CreateEntry("empty/");

        secondStream.Position = 0;

        var first = new ZipCatalog(firstStream, maxEntries: 1);
        var second = new ZipCatalog(secondStream, maxEntries: 1);
        Assert.NotEqual(first.Fingerprint, second.Fingerprint);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void CentralMetadataBudget_NonPositiveLimit_IsRejectedAtCatalogBoundary(long limit)
    {
        using MemoryStream source = BuildArchive("file.txt");
        ArgumentOutOfRangeException error = Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ZipCatalog(source, maxEntries: 1, maxCentralDirectoryMetadataBytes: limit)
        );
        Assert.Equal("maxCentralDirectoryMetadataBytes", error.ParamName);
    }

    [Fact]
    public void CentralMetadataBudget_CountsEveryDirectoryAndFileRecord()
    {
        byte[] bytes;
        using (MemoryStream archive = BuildArchive("folder/", "folder/a.txt", "folder/b.txt"))
            bytes = archive.ToArray();

        using var source = new MemoryStream(bytes);
        var reader = new ZipDirectory(source);
        long sum = 0;
        for (int i = 0; i < 3; i++)
            sum += reader.ReadEntry().Record.Length;

        Assert.Throws<InvalidDataException>(() =>
            new ZipCatalog(
                new MemoryStream(bytes),
                maxEntries: 3,
                maxCentralDirectoryMetadataBytes: sum - 1
            )
        );

        var accepted = new ZipCatalog(
            new MemoryStream(bytes),
            maxEntries: 3,
            maxCentralDirectoryMetadataBytes: sum
        );
        Assert.Equal(2, accepted.Entries.Count);
        Assert.Contains("folder", accepted.Directories);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CentralMetadataBudget_LargeCommentOrExtra_IsRejectedBeforeRecordAllocation(
        bool useExtra
    )
    {
        byte[] original;
        using (MemoryStream archive = BuildArchive("file.txt"))
            original = archive.ToArray();

        byte[] bytes = AddCentralMetadata(
            original,
            extraBytes: useExtra ? (ushort)60000 : (ushort)0,
            commentBytes: useExtra ? (ushort)0 : (ushort)60000
        );
        int recordLength;
        using (var source = new MemoryStream(bytes))
        {
            var directory = new ZipDirectory(source);
            recordLength = directory.ReadEntry().Record.Length;
            Assert.True(recordLength > 60000);
        }

        using (var source = new MemoryStream(bytes))
        {
            var directory = new ZipDirectory(source);
            InvalidDataException error = Assert.Throws<InvalidDataException>(() =>
                directory.ReadEntry(recordLength - 1)
            );
            Assert.Contains("central-directory metadata", error.Message);
            (long offset, byte[] record) = directory.ReadEntry(recordLength);
            Assert.True(offset >= 0);
            Assert.Equal(recordLength, record.Length);
        }

        var limited = new ZipContainer(
            _ => new MemoryStream(bytes),
            new ArchiveResourceLimits { MaxCentralDirectoryMetadataBytes = recordLength - 1 }
        );
        InvalidDataException failure = await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await limited.GetEntriesAsync(TestContext.Current.CancellationToken)
        );
        Assert.Contains("central-directory metadata", failure.Message);

        var accepted = new ZipContainer(
            _ => new MemoryStream(bytes),
            new ArchiveResourceLimits { MaxCentralDirectoryMetadataBytes = recordLength }
        );
        Assert.Single(await accepted.GetEntriesAsync(TestContext.Current.CancellationToken));

        // No additional security-only cap in the exact trusted production settings.
        var trusted = new ZipContainer(
            _ => new MemoryStream(bytes),
            limits: null,
            validateEntryMetadata: false
        );
        Assert.Single(await trusted.GetEntriesAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public void CentralMetadataBudget_LargeLongLimitDoesNotOverflow()
    {
        using MemoryStream archive = BuildArchive("one", "two");
        var catalog = new ZipCatalog(
            new MemoryStream(archive.ToArray()),
            maxEntries: 2,
            maxCentralDirectoryMetadataBytes: long.MaxValue
        );
        Assert.Equal(2, catalog.Entries.Count);
    }

    private static byte[] AddCentralMetadata(
        byte[] original,
        ushort extraBytes,
        ushort commentBytes
    )
    {
        int end = original.Length - 22;
        int central = checked(
            (int)BinaryPrimitives.ReadUInt32LittleEndian(original.AsSpan(end + 16))
        );
        int nameLength = BinaryPrimitives.ReadUInt16LittleEndian(original.AsSpan(central + 28));
        Assert.Equal(0, BinaryPrimitives.ReadUInt16LittleEndian(original.AsSpan(central + 30)));
        Assert.Equal(0, BinaryPrimitives.ReadUInt16LittleEndian(original.AsSpan(central + 32)));
        Assert.Equal(end, central + 46 + nameLength);

        int added = extraBytes + commentBytes;
        byte[] result = new byte[original.Length + added];
        original.AsSpan(0, end).CopyTo(result);
        original.AsSpan(end).CopyTo(result.AsSpan(end + added));

        if (extraBytes != 0)
        {
            Assert.True(extraBytes >= 4);

            // A well-formed optional extra field: header ID, payload length, bytes.
            BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(end), 0xCAFE);
            BinaryPrimitives.WriteUInt16LittleEndian(
                result.AsSpan(end + 2),
                (ushort)(extraBytes - 4)
            );
        }

        result.AsSpan(end + extraBytes, commentBytes).Fill((byte)'C');
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(central + 30), extraBytes);
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(central + 32), commentBytes);
        uint oldSize = BinaryPrimitives.ReadUInt32LittleEndian(original.AsSpan(end + 12));
        BinaryPrimitives.WriteUInt32LittleEndian(
            result.AsSpan(end + added + 12),
            checked(oldSize + (uint)added)
        );
        return result;
    }

    private static MemoryStream BuildArchive(params string[] names)
    {
        var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, true))
        {
            foreach (string name in names)
            {
                using var writer = new StreamWriter(archive.CreateEntry(name).Open());
                writer.Write("hello");
            }
        }

        stream.Position = 0;

        return stream;
    }
}
