using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using WinCfApiWrapper.IntegrationTests.Fixtures;
using WinCfApiWrapper.Mounting;
using WinCfApiWrapper.Public;
using WinCfApiWrapper.Public.Containers;
using Xunit;

namespace WinCfApiWrapper.IntegrationTests;

[Collection("Windows sync roots")]
[Trait("Category", "Offline")]
[Trait("Category", "Stress")]
public sealed class StressTests
{
    private static readonly byte[] Hello = "Hello from a checked-in ZIP fixture!\n"u8.ToArray();

    [Fact]
    public async Task ConcurrentReads_64Requests_ReturnCorrectContent()
    {
        string root = FixtureArchive.Root();
        ICloudMount mount = Open(root);
        bool registered = false;
        try
        {
            await mount.MountAsync();
            registered = true;
            byte[] expected = SHA256.HashData(Hello);
            Task<byte[]>[] reads = Enumerable
                .Range(0, 64)
                .Select(_ => File.ReadAllBytesAsync(mount.Resolve("docs/hello.txt")))
                .ToArray();
            byte[][] results = await Task.WhenAll(reads).WaitAsync(TimeSpan.FromSeconds(30));
            Assert.All(results, bytes => Assert.Equal(expected, SHA256.HashData(bytes)));
            Assert.InRange(mount.Statistics.ActiveFetches, 0, 2);
            Assert.Equal(0, mount.Statistics.FailedFetches);
        }
        finally
        {
            await mount.DisposeAsync();
            if (registered)
                FixtureArchive.CleanupRoot(root);
        }
    }

    [Fact]
    public async Task HydrateAll_OverlappingWithSingleHydrates_RemainsConsistent()
    {
        string root = FixtureArchive.Root();
        ICloudMount mount = Open(root);
        bool registered = false;
        try
        {
            await mount.MountAsync();
            registered = true;
            Task[] hydrations = Enumerable
                .Range(0, 64)
                .Select(_ => mount.HydrateAsync("docs/hello.txt"))
                .ToArray();
            await Task.WhenAll(Task.WhenAll(hydrations), mount.HydrateAllAsync())
                .WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(
                SHA256.HashData(Hello),
                SHA256.HashData(await File.ReadAllBytesAsync(mount.Resolve("docs/hello.txt")))
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

    [Fact]
    public async Task Stress_MountDisposeResume_10Cycles_PreservesContent()
    {
        string root = FixtureArchive.Root();
        bool registered = false;
        bool safeToUnregister = true;
        try
        {
            for (int cycle = 0; cycle < 10; cycle++)
            {
                ICloudMount mount = Open(root);
                try
                {
                    await mount.MountAsync().WaitAsync(TimeSpan.FromSeconds(30));
                    registered = true;
                    Assert.Equal(
                        SHA256.HashData(Hello),
                        SHA256.HashData(
                            await File.ReadAllBytesAsync(mount.Resolve("docs/hello.txt"))
                        )
                    );
                    Assert.Equal(
                        "Nested content\n",
                        await File.ReadAllTextAsync(mount.Resolve("docs/nested/info.txt"))
                    );
                }
                finally
                {
                    try
                    {
                        await mount.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(30));
                    }
                    catch
                    {
                        safeToUnregister = false;
                        throw;
                    }
                }
            }
        }
        finally
        {
            if (registered && safeToUnregister)
                FixtureArchive.CleanupRoot(root);
        }
    }

    private static ICloudMount Open(string root)
    {
        return new CloudMountFactory(NullLogger<CloudMount>.Instance).Open(
            new CloudMountOptions
            {
                Container = new ZipContainer(_ => File.OpenRead(FixtureArchive.LocalPath)),
                Root = root,
                FetchWorkers = 2,
                FetchQueueCapacity = 128,
                BulkHydrationConcurrency = 2,
            }
        );
    }
}
