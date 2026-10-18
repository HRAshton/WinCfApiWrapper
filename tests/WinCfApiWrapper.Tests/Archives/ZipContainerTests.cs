using System.IO.Compression;
using System.Text;
using Microsoft.Win32.SafeHandles;
using WinCfApiWrapper.Public;
using WinCfApiWrapper.Public.Containers;
using Xunit;

namespace WinCfApiWrapper.Tests.Archives;

public sealed class ZipContainerTests
{
    [Fact]
    public async Task IndexThenOpenRead_UsesSnapshotAndReleasesEachArchiveStream()
    {
        CancellationToken testCancellationToken = TestContext.Current.CancellationToken;

        byte[] archive = BuildArchive(("docs/", null), ("docs/readme.txt", "hello"));
        var sources = new List<TrackingStream>();
        var container = new ZipContainer(_ =>
        {
            var stream = new TrackingStream(archive);
            sources.Add(stream);
            stream.Position = stream.Length;
            return stream;
        });

        IReadOnlyCollection<ContainerEntry> entries = await container.GetEntriesAsync(
            testCancellationToken
        );
        ContainerEntry directory = Assert.Single(entries, entry => entry.IsDirectory);
        ContainerEntry file = Assert.Single(entries, entry => !entry.IsDirectory);
        Assert.Equal("docs", directory.RelativePath);
        Assert.Equal("docs\\readme.txt", file.RelativePath);
        Assert.Equal(5, file.Length);
        Assert.NotEmpty(file.Version);
        Assert.Single(sources);
        Assert.True(sources[0].WasDisposed);

        Stream opened = await container.OpenReadAsync(file, testCancellationToken);
        using (var reader = new StreamReader(opened))
            Assert.Equal("hello", await reader.ReadToEndAsync(testCancellationToken));

        Assert.Equal(2, sources.Count);
        Assert.True(sources[1].WasDisposed);
    }

    [Fact]
    public async Task ConcurrentIndexRequests_ShareOneArchiveScan()
    {
        CancellationToken testCancellationToken = TestContext.Current.CancellationToken;

        byte[] archive = BuildArchive(("file.txt", "payload"));
        int opens = 0;
        var container = new ZipContainer(_ =>
        {
            Interlocked.Increment(ref opens);
            return new MemoryStream(archive);
        });

        Task<IReadOnlyCollection<ContainerEntry>> first = container
            .GetEntriesAsync(testCancellationToken)
            .AsTask();
        Task<IReadOnlyCollection<ContainerEntry>> second = container
            .GetEntriesAsync(testCancellationToken)
            .AsTask();
        IReadOnlyCollection<ContainerEntry>[] results = await Task.WhenAll(first, second);

        Assert.Equal(1, opens);
        Assert.Equal(
            results[0].OrderBy(e => e.RelativePath),
            results[1].OrderBy(e => e.RelativePath)
        );
        Assert.Single(results[0]);
    }

    [Fact]
    public async Task OpenRead_RequiresAnEntryFromTheIndexedFileSnapshot()
    {
        CancellationToken testCancellationToken = TestContext.Current.CancellationToken;

        var container = new ZipContainer(_ => new MemoryStream(BuildArchive(("file.txt", "one"))));
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await container.OpenReadAsync(
                new ContainerEntry("file.txt", 3, "version"),
                testCancellationToken
            )
        );

        ContainerEntry indexed = Assert.Single(
            await container.GetEntriesAsync(testCancellationToken)
        );
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await container.OpenReadAsync(indexed with { Length = 4 }, testCancellationToken)
        );
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await container.OpenReadAsync(indexed with { Version = "other" }, testCancellationToken)
        );
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await container.OpenReadAsync(
                new ContainerEntry("missing.txt", 3, indexed.Version),
                testCancellationToken
            )
        );
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await container.OpenReadAsync(
                new ContainerEntry("folder", 0, string.Empty, true),
                testCancellationToken
            )
        );
    }

    [Fact]
    public async Task OpenRead_WhenArchiveChangesAfterIndexing_RejectsAndReleasesStream()
    {
        CancellationToken testCancellationToken = TestContext.Current.CancellationToken;

        byte[] original = BuildArchive(("file.txt", "original"));
        byte[] changed = BuildArchive(("file.txt", "replacement"));
        var sources = new List<TrackingStream>();
        var container = new ZipContainer(_ =>
        {
            byte[] current = sources.Count == 0 ? original : changed;
            var stream = new TrackingStream(current);
            sources.Add(stream);
            return stream;
        });
        ContainerEntry entry = Assert.Single(
            await container.GetEntriesAsync(testCancellationToken)
        );

        await Assert.ThrowsAsync<IOException>(async () =>
            await container.OpenReadAsync(entry, testCancellationToken)
        );

        Assert.Equal(2, sources.Count);
        Assert.All(sources, source => Assert.True(source.WasDisposed));
    }

    [Fact]
    public async Task GetEntries_CanceledBeforeIndexing_DoesNotOpenArchive()
    {
        int opens = 0;
        var container = new ZipContainer(_ =>
        {
            opens++;
            return new MemoryStream(BuildArchive(("file.txt", "content")));
        });
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await container.GetEntriesAsync(cancellation.Token)
        );

        Assert.Equal(0, opens);
    }

    [Fact]
    public async Task OpenRead_CanceledAfterIndexing_DoesNotOpenSecondArchive()
    {
        int opens = 0;
        var container = new ZipContainer(_ =>
        {
            opens++;
            return new MemoryStream(BuildArchive(("file.txt", "content")));
        });
        ContainerEntry entry = Assert.Single(
            await container.GetEntriesAsync(TestContext.Current.CancellationToken)
        );
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await container.OpenReadAsync(entry, cancellation.Token)
        );

        Assert.Equal(1, opens);
    }

    [Fact]
    public async Task OpenRead_EntryStreamIsReadOnlyAndReleasesOwnerOnDispose()
    {
        CancellationToken testCancellationToken = TestContext.Current.CancellationToken;

        byte[] archive = BuildArchive(("file.txt", "payload"));
        TrackingStream? source = null;
        var container = new ZipContainer(_ => source = new TrackingStream(archive));
        ContainerEntry entry = Assert.Single(
            await container.GetEntriesAsync(testCancellationToken)
        );
        Stream opened = await container.OpenReadAsync(entry, testCancellationToken);

        Assert.Throws<NotSupportedException>(() => opened.Seek(0, SeekOrigin.Begin));
        Assert.Throws<NotSupportedException>(() => opened.SetLength(0));
        Assert.Throws<NotSupportedException>(() => opened.Write([1], 0, 1));
        Assert.Throws<NotSupportedException>(() => opened.Flush());

        await opened.DisposeAsync();
        await opened.DisposeAsync();

        Assert.NotNull(source);
        Assert.True(source.WasDisposed);
    }

    [Fact]
    public async Task OpenRead_SupportsTheReadPatternsUsedByCallers()
    {
        CancellationToken testCancellationToken = TestContext.Current.CancellationToken;

        var container = new ZipContainer(_ => new MemoryStream(
            BuildArchive(("file.txt", "payload"))
        ));
        ContainerEntry entry = Assert.Single(
            await container.GetEntriesAsync(testCancellationToken)
        );

        await using Stream opened = await container.OpenReadAsync(entry, testCancellationToken);
        byte[] first = new byte[2];
        Assert.Equal(2, opened.Read(first, 0, first.Length));
        Assert.Equal("pa", Encoding.UTF8.GetString(first));
        Assert.Equal("yl", ReadTwoBytes(opened));

        byte[] next = new byte[1];
        Assert.Equal(1, await opened.ReadAsync(next.AsMemory(), testCancellationToken));
        Assert.Equal("o", Encoding.UTF8.GetString(next));

        byte[] remaining = new byte[2];
        Assert.Equal(
            2,
            await opened.ReadAsync(remaining, 0, remaining.Length, testCancellationToken)
        );
        Assert.Equal("ad", Encoding.UTF8.GetString(remaining));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OpenRead_Dispose_ReleasesSharedArchiveHandle(bool disposeAsync)
    {
        CancellationToken testCancellationToken = TestContext.Current.CancellationToken;

        string path = Path.Combine(Path.GetTempPath(), $"WinCfApiWrapper.{Guid.NewGuid():N}.zip");
        var handles = new List<SafeFileHandle>();
        try
        {
            await File.WriteAllBytesAsync(
                path,
                BuildArchive(("file.txt", "payload")),
                testCancellationToken
            );
            var container = new ZipContainer(_ =>
            {
                FileStream stream = File.Open(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read | FileShare.Delete
                );
                handles.Add(stream.SafeFileHandle);
                return stream;
            });
            ContainerEntry entry = Assert.Single(
                await container.GetEntriesAsync(testCancellationToken)
            );
            Assert.True(Assert.Single(handles).IsClosed);

            await using (
                Stream opened = await container.OpenReadAsync(entry, testCancellationToken)
            )
            {
                Assert.Equal(2, handles.Count);
                Assert.False(handles[1].IsClosed);
                Assert.Equal((int)'p', opened.ReadByte());
                Assert.Throws<IOException>(() =>
                {
                    using FileStream exclusive = File.Open(
                        path,
                        FileMode.Open,
                        FileAccess.ReadWrite,
                        FileShare.None
                    );
                });

                if (disposeAsync)
                    await opened.DisposeAsync();
                else
                    opened.Dispose();

                Assert.All(handles, handle => Assert.True(handle.IsClosed));
                using FileStream exclusive = File.Open(
                    path,
                    FileMode.Open,
                    FileAccess.ReadWrite,
                    FileShare.None
                );
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string ReadTwoBytes(Stream stream)
    {
        Span<byte> next = stackalloc byte[2];
        Assert.Equal(2, stream.Read(next));
        return Encoding.UTF8.GetString(next);
    }

    private static byte[] BuildArchive(params (string Name, string? Content)[] entries)
    {
        using var source = new MemoryStream();
        using (var archive = new ZipArchive(source, ZipArchiveMode.Create, true))
        {
            foreach ((string name, string? content) in entries)
            {
                ZipArchiveEntry entry = archive.CreateEntry(name);
                if (content is not null)
                {
                    using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
                    writer.Write(content);
                }
            }
        }

        return source.ToArray();
    }

    private sealed class TrackingStream(byte[] content) : MemoryStream(content)
    {
        public bool WasDisposed { get; private set; }

        protected override void Dispose(bool disposing)
        {
            WasDisposed |= disposing;
            base.Dispose(disposing);
        }
    }
}
