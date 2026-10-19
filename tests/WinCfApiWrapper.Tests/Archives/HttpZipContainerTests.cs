using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using WinCfApiWrapper.Archives;
using WinCfApiWrapper.Public;
using WinCfApiWrapper.Public.Containers;
using Xunit;

namespace WinCfApiWrapper.Tests.Archives;

public sealed class HttpZipContainerTests
{
    [Fact]
    public async Task SmallFiles_ReadOnlyTheirOwnMetadataAndContent_AfterOneIndex()
    {
        byte[] payload = new byte[10 * 1024];
        new Random(17).NextBytes(payload);
        byte[] archive = BuildArchive(payload, 200);
        using var handler = new RangeHandler(archive);
        using var client = new HttpClient(handler);
        ZipContainer container = ZipContainer.FromHttp(
            client,
            new Uri("https://archive.test/files.zip")
        );
        CancellationToken token = TestContext.Current.CancellationToken;
        ContainerEntry[] entries = (await container.GetEntriesAsync(token)).ToArray();
        handler.Ranges.Clear();

        await Task.WhenAll(
            entries
                .Take(12)
                .Select(async entry =>
                {
                    await using Stream input = await container.OpenReadAsync(entry, token);
                    using var output = new MemoryStream();
                    await input.CopyToAsync(output, token);
                    Assert.Equal(payload, output.ToArray());
                })
        );

        long downloaded = handler.Ranges.Sum(range => range.End - range.Start + 1);
        Assert.InRange(downloaded, payload.Length * 12L, (payload.Length + 1024L) * 12);
        Assert.All(
            handler.Ranges,
            range => Assert.InRange(range.End - range.Start + 1, 1, 64 * 1024)
        );
        Assert.All(handler.Validators, validator => Assert.Equal("\"snapshot\"", validator));
        Assert.NotEmpty(handler.Validators);
        Assert.Equal(1, handler.ProbeCount);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task ArchiveReplacement_WithUnchangedDirectory_IsRejectedAcrossStreams(
        bool strongEtag,
        bool validate
    )
    {
        byte[] original = BuildArchive([11, 22, 33, 44], 1);
        byte[] changed = (byte[])original.Clone();
        int dataOffset =
            30
            + BinaryPrimitives.ReadUInt16LittleEndian(original.AsSpan(26))
            + BinaryPrimitives.ReadUInt16LittleEndian(original.AsSpan(28));
        changed[dataOffset] ^= 0xff;
        int directoryOffset = (int)
            BinaryPrimitives.ReadUInt32LittleEndian(original.AsSpan(original.Length - 6));
        Assert.Equal(original[directoryOffset..], changed[directoryOffset..]);
        DateTimeOffset modified = new(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);
        using var handler = new RangeHandler(original)
        {
            ETag = strongEtag ? "\"snapshot\"" : null,
            Modified = strongEtag ? null : modified,
        };
        using var client = new HttpClient(handler);
        ZipContainer container = ZipContainer.FromHttp(
            client,
            new Uri("https://archive.test/files.zip"),
            validateEntryMetadata: validate
        );
        CancellationToken token = TestContext.Current.CancellationToken;
        ContainerEntry entry = Assert.Single(await container.GetEntriesAsync(token));
        handler.Validators.Clear();
        handler.Archive = changed;
        handler.ETag = strongEtag ? "\"replacement\"" : null;
        handler.Modified = strongEtag ? null : modified.AddSeconds(1);

        await Assert.ThrowsAsync<IOException>(async () =>
            await container.OpenReadAsync(entry, token)
        );

        Assert.NotEmpty(handler.Validators);
        string expectedValidator = strongEtag ? "\"snapshot\"" : modified.ToString("R");
        Assert.All(handler.Validators, validator => Assert.Equal(expectedValidator, validator));
        Assert.Equal(1, handler.ProbeCount);
    }

    [Fact]
    public async Task AsyncOnlyHandler_SupportsSynchronousZipReadsAndHttp2ClientDefaults()
    {
        byte[] payload = [11, 22, 33, 44, 55, 66];
        using var responses = new RangeHandler(BuildArchive(payload, 1));
        using var handler = new AsyncOnlyRangeHandler(responses);
        using var client = new HttpClient(handler)
        {
            DefaultRequestVersion = HttpVersion.Version20,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };
        ZipContainer container = ZipContainer.FromHttp(
            client,
            new Uri("https://archive.test/files.zip")
        );
        CancellationToken token = TestContext.Current.CancellationToken;
        ContainerEntry entry = Assert.Single(await container.GetEntriesAsync(token));
        await using Stream input = await container.OpenReadAsync(entry, token);
        byte[] actual = new byte[payload.Length];
        Assert.Equal(2, input.Read(actual, 0, 2));
        await input.ReadExactlyAsync(actual.AsMemory(2), token);

        Assert.Equal(payload, actual);
        Assert.NotEmpty(responses.Versions);
        Assert.All(
            responses.Versions,
            version =>
            {
                Assert.Equal(HttpVersion.Version20, version.Version);
                Assert.Equal(HttpVersionPolicy.RequestVersionExact, version.Policy);
            }
        );
        Assert.Equal(1, responses.ProbeCount);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("W/\"weak\"")]
    public async Task FailedProbe_DoesNotPinAnInvalidSnapshot_AndCanBeRetried(string? etag)
    {
        using var handler = new RangeHandler(BuildArchive([11, 22], 1)) { ETag = etag };
        using var client = new HttpClient(handler);
        ZipContainer container = ZipContainer.FromHttp(
            client,
            new Uri("https://archive.test/files.zip")
        );
        CancellationToken token = TestContext.Current.CancellationToken;
        await Assert.ThrowsAsync<IOException>(async () => await container.GetEntriesAsync(token));
        handler.ETag = "\"snapshot\"";

        ContainerEntry entry = Assert.Single(await container.GetEntriesAsync(token));
        await using Stream input = await container.OpenReadAsync(entry, token);
        using var actual = new MemoryStream();
        await input.CopyToAsync(actual, token);

        Assert.Equal(new byte[] { 11, 22 }, actual.ToArray());
        Assert.Equal(2, handler.ProbeCount);
    }

    [Fact]
    public async Task SynchronousReads_WithNonPumpingContext_SupportAsyncOnlyHandler()
    {
        await Task.Run(
                () =>
                {
                    using var responses = new RangeHandler(BuildArchive([1, 2, 3], 1));
                    using var handler = new AsyncOnlyRangeHandler(responses);
                    using var client = new HttpClient(handler);
                    var source = new HttpArchiveSource(
                        client,
                        new Uri("https://archive.test/files.zip")
                    );
                    SynchronizationContext? previous = SynchronizationContext.Current;
                    SynchronizationContext.SetSynchronizationContext(new NonPumpingContext());
                    try
                    {
                        using Stream input = source.OpenRead(default);
                        byte[] actual = new byte[4];
                        Assert.Equal(4, input.Read(actual, 0, actual.Length));
                        Assert.Equal(responses.Archive[..4], actual);
                        input.Position = 0;
                        Span<byte> span = stackalloc byte[4];
                        Assert.Equal(4, input.Read(span));
                        Assert.Equal(actual, span.ToArray());
                        Assert.Equal(1, responses.ProbeCount);
                    }
                    finally
                    {
                        SynchronizationContext.SetSynchronizationContext(previous);
                    }
                },
                TestContext.Current.CancellationToken
            )
            .WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ConcurrentStreams_ShareOneProbe_AndHonorReadCancellation()
    {
        using var responses = new RangeHandler(BuildArchive([1, 2, 3], 1));
        using var handler = new AsyncOnlyRangeHandler(responses);
        using var client = new HttpClient(handler);
        var source = new HttpArchiveSource(client, new Uri("https://archive.test/files.zip"));
        await Task.WhenAll(
            Enumerable
                .Range(0, 12)
                .Select(_ =>
                    Task.Run(
                        () =>
                        {
                            using Stream input = source.OpenRead(default);
                            Assert.Equal(responses.Archive.Length, input.Length);
                        },
                        TestContext.Current.CancellationToken
                    )
                )
        );
        Assert.Equal(1, responses.ProbeCount);
        using var lifetime = new CancellationTokenSource();
        using var read = new CancellationTokenSource();
        using Stream stream = source.OpenRead(lifetime.Token);
        byte[] data = new byte[2];
        Assert.Equal(2, await stream.ReadAsync(data, 0, 2, read.Token));
        read.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            _ = await stream.ReadAsync(data.AsMemory(), read.Token)
        );
        lifetime.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => stream.Read(data, 0, 2));
    }

    [Fact]
    public async Task LargeEntry_StreamsInBoundedRequests()
    {
        byte[] payload = new byte[(3 * 1024 * 1024) + 123];
        new Random(21).NextBytes(payload);
        using var handler = new RangeHandler(BuildArchive(payload, 1));
        using var client = new HttpClient(handler);
        ZipContainer container = ZipContainer.FromHttp(
            client,
            new Uri("https://archive.test/files.zip")
        );
        CancellationToken token = TestContext.Current.CancellationToken;
        ContainerEntry entry = Assert.Single(await container.GetEntriesAsync(token));
        handler.Ranges.Clear();

        await using Stream input = await container.OpenReadAsync(entry, token);
        using var output = new MemoryStream();
        await input.CopyToAsync(output, token);

        Assert.Equal(payload, output.ToArray());
        Assert.True(handler.Ranges.Count > 48);
        Assert.All(
            handler.Ranges,
            range => Assert.InRange(range.End - range.Start + 1, 1, 64 * 1024)
        );
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MetadataValidation_CanBeDisabledForTrustedSources(bool validate)
    {
        using var handler = new RangeHandler(BuildArchive(new byte[10 * 1024], 200));
        using var client = new HttpClient(handler);
        ZipContainer container = ZipContainer.FromHttp(
            client,
            new Uri("https://archive.test/files.zip"),
            validateEntryMetadata: validate
        );
        CancellationToken token = TestContext.Current.CancellationToken;
        ContainerEntry entry = (await container.GetEntriesAsync(token)).First();
        handler.Ranges.Clear();

        await using Stream input = await container.OpenReadAsync(entry, token);
        Assert.Equal(validate, handler.Ranges.Any(range => range.Start >= 200 * 10 * 1024));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task InvalidRangeOrChangedVersion_IsRejected(
        bool fullResponse,
        bool changedVersion
    )
    {
        using var handler = new RangeHandler(BuildArchive([1, 2, 3], 1));
        using var client = new HttpClient(handler);
        ZipContainer container = ZipContainer.FromHttp(
            client,
            new Uri("https://archive.test/files.zip")
        );
        ContainerEntry entry = Assert.Single(
            await container.GetEntriesAsync(TestContext.Current.CancellationToken)
        );
        handler.FullResponse = fullResponse;
        handler.ChangeVersionAfterProbe = changedVersion;

        await Assert.ThrowsAsync<IOException>(async () =>
            await container.OpenReadAsync(entry, TestContext.Current.CancellationToken)
        );
    }

    private static byte[] BuildArchive(byte[] payload, int count)
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, true))
        {
            for (int i = 0; i < count; i++)
            {
                using Stream entry = archive
                    .CreateEntry($"file-{i}.bin", CompressionLevel.NoCompression)
                    .Open();
                entry.Write(payload);
            }
        }

        return output.ToArray();
    }

    private sealed class RangeHandler(byte[] archive) : HttpMessageHandler
    {
        private int _probes;

        public byte[] Archive { get; set; } = archive;

        public string? ETag { get; set; } = "\"snapshot\"";

        public DateTimeOffset? Modified { get; set; }

        public int ProbeCount => Volatile.Read(ref _probes);

        public ConcurrentQueue<(long Start, long End)> Ranges { get; } = new();

        public ConcurrentQueue<string> Validators { get; } = new();

        public ConcurrentQueue<(Version Version, HttpVersionPolicy Policy)> Versions { get; } =
            new();

        public bool FullResponse { get; set; }

        public bool ChangeVersionAfterProbe { get; set; }

        public HttpResponseMessage Respond(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            RangeItemHeaderValue range = Assert.Single(request.Headers.Range!.Ranges);
            long start = range.From!.Value;
            long end = range.To!.Value;
            Ranges.Enqueue((start, end));
            if (request.Headers.IfRange is { } validator)
                Validators.Enqueue(validator.ToString());
            else
                Interlocked.Increment(ref _probes);
            Versions.Enqueue((request.Version, request.VersionPolicy));
            byte[] current = Archive;
            var response = new HttpResponseMessage(
                FullResponse ? HttpStatusCode.OK : HttpStatusCode.PartialContent
            )
            {
                Content = new ByteArrayContent(
                    current,
                    (int)start,
                    checked((int)(end - start + 1))
                ),
            };
            string? etag = ChangeVersionAfterProbe && end != 0 ? "\"changed\"" : ETag;
            response.Headers.ETag = etag is null ? null : EntityTagHeaderValue.Parse(etag);
            response.Content.Headers.LastModified = Modified;
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(
                start,
                end,
                current.Length
            );
            return response;
        }

        protected override HttpResponseMessage Send(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            return Respond(request, cancellationToken);
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            return Task.FromResult(Respond(request, cancellationToken));
        }
    }

    private sealed class AsyncOnlyRangeHandler(RangeHandler responses) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            await Task.Yield();
            return responses.Respond(request, cancellationToken);
        }
    }

    private sealed class NonPumpingContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback callback, object? state) =>
            throw new InvalidOperationException(
                "A synchronous read must not capture this context."
            );
    }
}
