using System.Buffers.Binary;
using System.IO.Compression;
using WinCfApiWrapper.Archives;
using WinCfApiWrapper.Public;
using WinCfApiWrapper.Public.Containers;
using Xunit;

namespace WinCfApiWrapper.Tests.Archives;

public sealed class ZipDirectoryTests
{
    [Theory]
    [InlineData(false, true)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(true, false)]
    public async Task EntryDataBeyondOriginalArchive_IsRejectedBeforeOpening(
        bool extendLocalHeader,
        bool validate
    )
    {
        byte[] archive = BuildArchive(CompressionLevel.NoCompression);
        if (extendLocalHeader)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(
                archive.AsSpan(26),
                (ushort)(archive.Length + 16 - 30)
            );
        }
        else
        {
            int central = (int)CentralOffset(archive);
            int dataStart = 30 + BinaryPrimitives.ReadUInt16LittleEndian(archive.AsSpan(26));
            uint length = (uint)(archive.Length - dataStart + 32);
            BinaryPrimitives.WriteUInt32LittleEndian(archive.AsSpan(central + 20), length);
            BinaryPrimitives.WriteUInt32LittleEndian(archive.AsSpan(central + 24), length);
        }

        using (var baseline = new ZipArchive(new MemoryStream(archive), ZipArchiveMode.Read))
            Assert.Throws<InvalidDataException>(() => baseline.Entries.Single().Open());

        MemoryStream? openedSource = null;
        var container = new ZipContainer(
            _ => openedSource = new MemoryStream(archive),
            null,
            validate
        );
        CancellationToken token = TestContext.Current.CancellationToken;
        ContainerEntry entry = Assert.Single(await container.GetEntriesAsync(token));

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await container.OpenReadAsync(entry, token)
        );

        Assert.NotNull(openedSource);
        Assert.False(openedSource.CanRead);
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(false, 32)]
    [InlineData(true, 0)]
    [InlineData(true, 32)]
    public async Task TrailingBytes_MatchZipArchiveCompatibility(bool zip64, int commentLength)
    {
        byte[] original = zip64 ? BuildZip64Archive() : BuildArchive();
        byte[] archive = new byte[original.Length + commentLength + 16];
        original.CopyTo(archive, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(
            archive.AsSpan(original.Length - 2),
            (ushort)commentLength
        );
        archive.AsSpan(original.Length).Fill(0x7e);
        string expected;
        using (var baseline = new ZipArchive(new MemoryStream(archive), ZipArchiveMode.Read))
        using (var reader = new StreamReader(baseline.Entries.Single().Open()))
            expected = reader.ReadToEnd();

        var container = new ZipContainer(_ => new MemoryStream(archive));
        CancellationToken token = TestContext.Current.CancellationToken;
        ContainerEntry entry = Assert.Single(await container.GetEntriesAsync(token));
        await using Stream input = await container.OpenReadAsync(entry, token);
        using var contents = new StreamReader(input);

        Assert.Equal("payload", expected);
        Assert.Equal(expected, await contents.ReadToEndAsync(token));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task EntryValidation_ReusesNativeHeaderReads_WithoutFetchingOtherDirectoryRecords(
        bool validate
    )
    {
        byte[] payload = new byte[10 * 1024];
        new Random(17).NextBytes(payload);
        using var output = new MemoryStream();
        using (var writer = new ZipArchive(output, ZipArchiveMode.Create, true))
        {
            using (
                Stream file = writer.CreateEntry("file.txt", CompressionLevel.NoCompression).Open()
            )
                file.Write(payload);
            for (int i = 0; i < 100; i++)
                writer.CreateEntry($"other-{i}.txt");
        }

        byte[] archive = output.ToArray();
        CancellationToken token = TestContext.Current.CancellationToken;
        using var baselineSource = new ReadTrackingStream(archive);
        using var baseline = new ZipArchive(baselineSource, ZipArchiveMode.Read, true);
        ZipArchiveEntry baselineEntry = baseline.GetEntry("file.txt")!;
        baselineSource.ResetCounts();
        using (Stream input = baselineEntry.Open())
            await input.CopyToAsync(Stream.Null, token);

        var catalog = new ZipCatalog(new MemoryStream(archive), maxEntries: 101);
        ZipEntryInfo expected = catalog.Entries["file.txt"];
        using var source = new ReadTrackingStream(archive);
        using var reader = new ZipEntryReader(ZipDirectory.OpenEntry(source, expected, validate));
        await using Stream contents = reader.OpenEntry(expected);
        using var actual = new MemoryStream();
        await contents.CopyToAsync(actual, token);

        Assert.Equal(payload, actual.ToArray());
        Assert.Equal(
            baselineSource.BytesRead + (validate ? expected.DirectoryRecord!.Length : 0),
            source.BytesRead
        );
        Assert.Equal(baselineSource.ReadCalls + (validate ? 1 : 0), source.ReadCalls);
    }

    [Fact]
    public async Task Zip64Directory_SmallArchiveWithSentinelOffsets_IndexesAndReadsContent()
    {
        byte[] archive = BuildZip64Archive();
        var container = new ZipContainer(_ => new MemoryStream(archive));
        CancellationToken token = TestContext.Current.CancellationToken;

        ContainerEntry entry = Assert.Single(await container.GetEntriesAsync(token));
        await using Stream input = await container.OpenReadAsync(entry, token);
        using var reader = new StreamReader(input);

        Assert.Equal("file.txt", entry.RelativePath);
        Assert.Equal("payload", await reader.ReadToEndAsync(token));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(64)]
    public void MissingEndRecord_IsRejected(int length)
    {
        using var source = new MemoryStream(new byte[length]);

        InvalidDataException error = Assert.Throws<InvalidDataException>(() =>
            new ZipDirectory(source)
        );

        Assert.Equal("ZIP end record is missing.", error.Message);
    }

    [Fact]
    public void EndRecordWithComment_FindsTheDirectoryBeforeTheComment()
    {
        byte[] original = BuildArchive();
        byte[] commented = new byte[original.Length + 32];
        original.CopyTo(commented, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(commented.AsSpan(original.Length - 2), 32);
        using var source = new MemoryStream(commented);

        var directory = new ZipDirectory(source);
        (long offset, byte[] record) = directory.ReadEntry();

        Assert.Equal(CentralOffset(original), offset);
        Assert.Equal(0x02014b50u, BinaryPrimitives.ReadUInt32LittleEndian(record));
    }

    [Theory]
    [InlineData(true, "ZIP64 locator is missing.")]
    [InlineData(false, "ZIP64 end record is missing.")]
    public void CorruptZip64Record_IsRejected(bool corruptLocator, string expectedMessage)
    {
        byte[] archive = BuildZip64Archive();
        int signature = corruptLocator ? archive.Length - 42 : archive.Length - 98;
        archive[signature] = 0;
        using var source = new MemoryStream(archive);

        InvalidDataException error = Assert.Throws<InvalidDataException>(() =>
            new ZipDirectory(source)
        );

        Assert.Equal(expectedMessage, error.Message);
    }

    [Fact]
    public void CorruptCentralRecord_IsRejected()
    {
        byte[] archive = BuildArchive();
        archive[(int)CentralOffset(archive)] = 0;
        using var source = new MemoryStream(archive);
        var directory = new ZipDirectory(source);

        InvalidDataException error = Assert.Throws<InvalidDataException>(() =>
            directory.ReadEntry()
        );

        Assert.Equal("ZIP central record is missing.", error.Message);
    }

    [Theory]
    [InlineData(8, true)]
    [InlineData(16, true)]
    [InlineData(8, false)]
    [InlineData(16, false)]
    public async Task ChangedCentralMetadata_IsRejectedUnlessValidationIsDisabled(
        int fieldOffset,
        bool validate
    )
    {
        byte[] original = BuildArchive();
        byte[] changed = (byte[])original.Clone();
        changed[(int)CentralOffset(original) + fieldOffset] ^= 1;
        int opens = 0;
        MemoryStream? openedSource = null;
        var container = new ZipContainer(
            _ => openedSource = new MemoryStream(opens++ == 0 ? original : changed),
            limits: null,
            validateEntryMetadata: validate
        );
        CancellationToken token = TestContext.Current.CancellationToken;
        ContainerEntry entry = Assert.Single(await container.GetEntriesAsync(token));

        if (validate)
        {
            IOException error = await Assert.ThrowsAsync<IOException>(async () =>
                await container.OpenReadAsync(entry, token)
            );
            Assert.Contains("Archive entry changed", error.Message);
        }
        else
        {
            await using Stream input = await container.OpenReadAsync(entry, token);
            using var reader = new StreamReader(input);
            Assert.Equal("payload", await reader.ReadToEndAsync(token));
        }

        Assert.NotNull(openedSource);
        Assert.False(openedSource.CanRead);
    }

    [Fact]
    public void EntryView_IsSeekableReadOnlyAndOwnsItsSource()
    {
        byte[] archive = BuildArchive();
        ZipEntryInfo entry = SnapshotEntry(archive);
        var source = new MemoryStream(archive);
        using (Stream view = ZipDirectory.OpenEntry(source, entry, true))
        {
            Assert.True(view.CanRead);
            Assert.True(view.CanSeek);
            Assert.False(view.CanWrite);
            Assert.True(view.Length > source.Length);
            Assert.Equal(4, view.Seek(4, SeekOrigin.Begin));
            byte[] bytes = new byte[4];
            Assert.Equal(bytes.Length, view.Read(bytes, 0, bytes.Length));
            Assert.Equal(archive.AsSpan(4, 4).ToArray(), bytes);
            Assert.Equal(10, view.Seek(2, SeekOrigin.Current));
            Assert.Equal(10, view.Position);

            Assert.Equal(view.Length - 22, view.Seek(-22, SeekOrigin.End));
            view.ReadExactly(bytes);
            Assert.Equal(new byte[] { 0x50, 0x4b, 0x05, 0x06 }, bytes);
            view.Position = view.Length;
            Assert.Equal(0, view.Read(bytes, 0, bytes.Length));

            Assert.Throws<ArgumentOutOfRangeException>(() => view.Position = -1);
            Assert.Throws<ArgumentOutOfRangeException>(() => view.Seek(0, (SeekOrigin)99));
            Assert.Throws<NotSupportedException>(() => view.Flush());
            Assert.Throws<NotSupportedException>(() => view.SetLength(0));
            Assert.Throws<NotSupportedException>(() => view.Write(bytes, 0, bytes.Length));
        }

        Assert.False(source.CanRead);
    }

    [Fact]
    public async Task EntryView_AsyncReadsOriginalBytesAndVirtualDirectory_AndHonorsCancellation()
    {
        byte[] archive = BuildArchive();
        ZipEntryInfo entry = SnapshotEntry(archive);
        using var source = new MemoryStream(archive);
        await using Stream view = ZipDirectory.OpenEntry(source, entry, true);
        CancellationToken token = TestContext.Current.CancellationToken;
        byte[] bytes = new byte[4];

        Assert.Equal(bytes.Length, await view.ReadAsync(bytes.AsMemory(), token));
        Assert.Equal(archive.AsSpan(0, 4).ToArray(), bytes);
        view.Seek(-22, SeekOrigin.End);
        Assert.Equal(bytes.Length, await view.ReadAsync(bytes.AsMemory(), token));
        Assert.Equal(new byte[] { 0x50, 0x4b, 0x05, 0x06 }, bytes);
        view.Position = view.Length;
        Assert.Equal(0, await view.ReadAsync(bytes.AsMemory(), token));

        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await view.ReadExactlyAsync(bytes.AsMemory(), canceled.Token)
        );
    }

    private static ZipEntryInfo SnapshotEntry(byte[] archive)
    {
        var catalog = new ZipCatalog(new MemoryStream(archive), maxEntries: 1);
        return Assert.Single(catalog.Entries.Values);
    }

    private static long CentralOffset(byte[] archive)
    {
        return BinaryPrimitives.ReadUInt32LittleEndian(archive.AsSpan(archive.Length - 6));
    }

    private static byte[] BuildArchive(CompressionLevel compressionLevel = CompressionLevel.Optimal)
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, true))
        using (
            var writer = new StreamWriter(archive.CreateEntry("file.txt", compressionLevel).Open())
        )
            writer.Write("payload");
        return output.ToArray();
    }

    private static byte[] BuildZip64Archive()
    {
        byte[] original = BuildArchive();
        int endOffset = original.Length - 22;
        long centralOffset = CentralOffset(original);
        uint centralSize = BinaryPrimitives.ReadUInt32LittleEndian(original.AsSpan(endOffset + 12));
        using var output = new MemoryStream();
        output.Write(original.AsSpan(0, endOffset));
        using var writer = new BinaryWriter(output, System.Text.Encoding.UTF8, true);

        // A tiny ZIP64 fixture exercises 64-bit directory offsets without allocating a multi-gigabyte archive.
        writer.Write(0x06064b50u);
        writer.Write(44L);
        writer.Write((ushort)45);
        writer.Write((ushort)45);
        writer.Write(0u);
        writer.Write(0u);
        writer.Write(1L);
        writer.Write(1L);
        writer.Write((long)centralSize);
        writer.Write(centralOffset);
        writer.Write(0x07064b50u);
        writer.Write(0u);
        writer.Write((long)endOffset);
        writer.Write(1u);
        byte[] end = original.AsSpan(endOffset).ToArray();
        BinaryPrimitives.WriteUInt16LittleEndian(end.AsSpan(8), ushort.MaxValue);
        BinaryPrimitives.WriteUInt16LittleEndian(end.AsSpan(10), ushort.MaxValue);
        BinaryPrimitives.WriteUInt32LittleEndian(end.AsSpan(12), uint.MaxValue);
        BinaryPrimitives.WriteUInt32LittleEndian(end.AsSpan(16), uint.MaxValue);
        writer.Write(end);
        return output.ToArray();
    }

    private sealed class ReadTrackingStream(byte[] bytes) : MemoryStream(bytes)
    {
        public long BytesRead { get; private set; }

        public int ReadCalls { get; private set; }

        public void ResetCounts()
        {
            BytesRead = 0;
            ReadCalls = 0;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int read = base.Read(buffer, offset, count);
            if (read > 0)
            {
                BytesRead += read;
                ReadCalls++;
            }

            return read;
        }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Read(buffer.Span));
        }
    }
}
