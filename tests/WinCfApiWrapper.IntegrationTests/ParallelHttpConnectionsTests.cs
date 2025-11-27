using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Logging.Abstractions;
using WinCfApiWrapper.IntegrationTests.Fixtures;
using WinCfApiWrapper.Mounting;
using WinCfApiWrapper.Public;
using WinCfApiWrapper.Public.Containers;
using Xunit;

namespace WinCfApiWrapper.IntegrationTests;

[Collection("Windows sync roots")]
[Trait("Category", "Offline")]
public sealed class ParallelHttpConnectionsTests
{
    private static readonly HttpClient SharedClient = CreateClient();

    [Fact]
    [Trait("Category", "Transport")]
    public async Task ArchiveFactory_OpensSeekableStreamsOverParallelHttpConnections()
    {
        byte[] archive = await File.ReadAllBytesAsync(FixtureArchive.LocalPath);
        await using var server = new LoopbackRangeServer(archive);
        Uri archiveUrl = server.Url;
        server.ObserveRequests(true);

        Task<Stream>[] opens = Enumerable
            .Range(0, 2)
            .Select(_ =>
                Task.Run(() => OpenArchive(SharedClient, archiveUrl, CancellationToken.None))
            )
            .ToArray();
        Stream[] streams = await Task.WhenAll(opens).WaitAsync(TimeSpan.FromSeconds(20));
        await using (streams[0])
        await using (streams[1])
        {
            Assert.All(
                streams,
                stream =>
                {
                    Assert.True(stream.CanSeek);
                    Assert.Equal(archive, ((MemoryStream)stream).ToArray());
                }
            );
            Assert.True(server.MaxConcurrent >= 2);
            Assert.True(server.DistinctConnections >= 2);
            Assert.Empty(server.Errors);
        }
    }

    [Fact]
    public async Task HydrateAll_ReadsEntriesThroughHttpArchiveFactory()
    {
        byte[] archive = await File.ReadAllBytesAsync(FixtureArchive.LocalPath);
        await using var server = new LoopbackRangeServer(archive);
        Uri archiveUrl = server.Url;
        string root = FixtureArchive.Root();
        ICloudMount mount = new CloudMountFactory(NullLogger<CloudMount>.Instance).Open(
            new CloudMountOptions
            {
                Root = root,
                FetchWorkers = 4,
                BulkHydrationConcurrency = 4,
                Container = new ZipContainer(token => OpenArchive(SharedClient, archiveUrl, token)),
            }
        );
        bool registered = false;
        try
        {
            await mount.MountAsync().WaitAsync(TimeSpan.FromSeconds(30));
            registered = true;
            server.ObserveRequests();

            await mount.HydrateAllAsync().WaitAsync(TimeSpan.FromSeconds(30));

            Assert.True(
                server.DistinctConnections >= 2,
                "Hydration did not open an HTTP connection for each entry."
            );
            Assert.Empty(server.Errors);
            Assert.Equal(
                "Hello from a checked-in ZIP fixture!\n",
                await File.ReadAllTextAsync(mount.Resolve("docs/hello.txt"))
            );
            Assert.Equal(
                "Nested content\n",
                await File.ReadAllTextAsync(mount.Resolve("docs/nested/info.txt"))
            );
        }
        finally
        {
            await mount.DisposeAsync();
            if (registered)
                FixtureArchive.CleanupRoot(root);
        }
    }

    private static HttpClient CreateClient()
    {
        return new HttpClient(
            new SocketsHttpHandler
            {
                AutomaticDecompression = DecompressionMethods.None,
                UseProxy = false,
            }
        );
    }

    private static Stream OpenArchive(HttpClient client, Uri url, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Range = new RangeHeaderValue(0, null);
        using HttpResponseMessage response = client.Send(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            token
        );
        if (response.StatusCode != HttpStatusCode.PartialContent)
            throw new IOException($"Unexpected archive response: {response.StatusCode}");

        byte[] bytes = response.Content.ReadAsByteArrayAsync(token).GetAwaiter().GetResult();

        return new MemoryStream(bytes, false);
    }
}
