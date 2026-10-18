using Microsoft.Extensions.Logging.Abstractions;
using WinCfApiWrapper.IntegrationTests.Fixtures;
using WinCfApiWrapper.Mounting;
using WinCfApiWrapper.Public;
using WinCfApiWrapper.Public.Containers;
using Xunit;

namespace WinCfApiWrapper.IntegrationTests;

[Collection("Windows sync roots")]
[Trait("Category", "Offline")]
public sealed class HydrationTests
{
    [Fact]
    public async Task HydrateAllAsync_AllFilesBecomeReadable()
    {
        CancellationToken testCancellationToken = TestContext.Current.CancellationToken;

        string root = FixtureArchive.Root();
        ICloudMount mount = new CloudMountFactory(NullLogger<CloudMount>.Instance).Open(
            new CloudMountOptions
            {
                Container = new ZipContainer(_ => File.OpenRead(FixtureArchive.LocalPath)),
                Root = root,
            }
        );
        bool registered = false;
        try
        {
            await mount.MountAsync(testCancellationToken);
            registered = true;
            await mount.HydrateAsync("docs/hello.txt", testCancellationToken);
            await mount.HydrateAllAsync(cancellationToken: testCancellationToken);
            Assert.Equal(
                "Hello from a checked-in ZIP fixture!\n",
                await File.ReadAllTextAsync(mount.Resolve("docs/hello.txt"), testCancellationToken)
            );
            Assert.Equal(
                "Nested content\n",
                await File.ReadAllTextAsync(
                    mount.Resolve("docs/nested/info.txt"),
                    testCancellationToken
                )
            );
            Assert.Equal(0, mount.Statistics.FailedFetches);
        }
        finally
        {
            await mount.DisposeAsync();
            if (registered)
                FixtureArchive.CleanupRoot(root);
        }
    }
}
