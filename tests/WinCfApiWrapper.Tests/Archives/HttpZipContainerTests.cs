using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
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
        ZipContainer container = ZipContainer.FromHttp(client, new Uri("https://archive.test/files.zip"));
        CancellationToken token = TestContext.Current.CancellationToken;
        ContainerEntry[] entries = (await container.GetEntriesAsync(token)).ToArray();
        handler.Ranges.Clear();

        await Task.WhenAll(entries.Take(12).Select(async entry =>
        {
            await using Stream input = await container.OpenReadAsync(entry, token);
            using var output = new MemoryStream();
            await input.CopyToAsync(output, token);
            Assert.Equal(payload, output.ToArray());
        }));

        long downloaded = handler.Ranges.Sum(range => range.End - range.Start + 1);
        Assert.InRange(downloaded, payload.Length * 12L, (payload.Length + 1024L) * 12);
        Assert.All(handler.Ranges, range => Assert.InRange(range.End - range.Start + 1, 1, 64 * 1024));
        Assert.All(handler.Validators, validator => Assert.Equal("\"snapshot\"", validator));
        Assert.NotEmpty(handler.Validators);
    }

    [Fact]
    public async Task LargeEntry_StreamsInBoundedRequests()
    {
        byte[] payload = new byte[(3 * 1024 * 1024) + 123];
        new Random(21).NextBytes(payload);
        using var handler = new RangeHandler(BuildArchive(payload, 1));
        using var client = new HttpClient(handler);
        ZipContainer container = ZipContainer.FromHttp(client, new Uri("https://archive.test/files.zip"));
        CancellationToken token = TestContext.Current.CancellationToken;
        ContainerEntry entry = Assert.Single(await container.GetEntriesAsync(token));
        handler.Ranges.Clear();

        await using Stream input = await container.OpenReadAsync(entry, token);
        using var output = new MemoryStream();
        await input.CopyToAsync(output, token);

        Assert.Equal(payload, output.ToArray());
        Assert.True(handler.Ranges.Count > 48);
        Assert.All(handler.Ranges, range => Assert.InRange(range.End - range.Start + 1, 1, 64 * 1024));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MetadataValidation_CanBeDisabledForTrustedSources(bool validate)
    {
        using var handler = new RangeHandler(BuildArchive(new byte[10 * 1024], 200));
        using var client = new HttpClient(handler);
        ZipContainer container = ZipContainer.FromHttp(client, new Uri("https://archive.test/files.zip"), validateEntryMetadata: validate);
        CancellationToken token = TestContext.Current.CancellationToken;
        ContainerEntry entry = (await container.GetEntriesAsync(token)).First();
        handler.Ranges.Clear();

        await using Stream input = await container.OpenReadAsync(entry, token);
        Assert.Equal(validate, handler.Ranges.Any(range => range.Start >= 200 * 10 * 1024));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task InvalidRangeOrChangedVersion_IsRejected(bool fullResponse, bool changedVersion)
    {
        using var handler = new RangeHandler(BuildArchive([1, 2, 3], 1));
        using var client = new HttpClient(handler);
        ZipContainer container = ZipContainer.FromHttp(client, new Uri("https://archive.test/files.zip"));
        ContainerEntry entry = Assert.Single(await container.GetEntriesAsync(TestContext.Current.CancellationToken));
        handler.FullResponse = fullResponse;
        handler.ChangeVersionAfterProbe = changedVersion;

        await Assert.ThrowsAsync<IOException>(async () =>
            await container.OpenReadAsync(entry, TestContext.Current.CancellationToken));
    }

    private static byte[] BuildArchive(byte[] payload, int count)
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, true))
        {
            for (int i = 0; i < count; i++)
            {
                using Stream entry = archive.CreateEntry($"file-{i}.bin", CompressionLevel.NoCompression).Open();
                entry.Write(payload);
            }
        }

        return output.ToArray();
    }

    private sealed class RangeHandler(byte[] archive) : HttpMessageHandler
    {
        public ConcurrentQueue<(long Start, long End)> Ranges { get; } = new();

        public ConcurrentQueue<string> Validators { get; } = new();

        public bool FullResponse { get; set; }

        public bool ChangeVersionAfterProbe { get; set; }

        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RangeItemHeaderValue range = Assert.Single(request.Headers.Range!.Ranges);
            long start = range.From!.Value;
            long end = range.To!.Value;
            Ranges.Enqueue((start, end));
            if (request.Headers.IfRange is { } validator)
                Validators.Enqueue(validator.ToString());
            var response = new HttpResponseMessage(FullResponse ? HttpStatusCode.OK : HttpStatusCode.PartialContent)
            {
                Content = new ByteArrayContent(archive, (int)start, checked((int)(end - start + 1))),
            };
            response.Headers.ETag = new EntityTagHeaderValue(ChangeVersionAfterProbe && end != 0 ? "\"changed\"" : "\"snapshot\"");
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(start, end, archive.Length);
            return response;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(Send(request, cancellationToken));
        }
    }
}
