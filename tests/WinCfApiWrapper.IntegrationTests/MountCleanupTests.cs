using Microsoft.Extensions.Logging.Abstractions;
using WinCfApiWrapper.IntegrationTests.Fixtures;
using WinCfApiWrapper.Mounting;
using WinCfApiWrapper.Public;
using WinCfApiWrapper.Public.Containers;
using Xunit;

namespace WinCfApiWrapper.IntegrationTests;

[Collection("Windows sync roots")]
[Trait("Category", "Offline")]
public sealed class MountCleanupTests
{
    [Fact]
    public async Task CleanupMounts_RemovesExistingMountAndLeavesPathReusable()
    {
        CancellationToken testCancellationToken = TestContext.Current.CancellationToken;

        string parent = Path.Combine(
            Path.GetTempPath(),
            "WinCfApiWrapper.IntegrationTests",
            Guid.NewGuid().ToString("N")
        );
        string baseRoot = Path.Combine(parent, "mount");
        Directory.CreateDirectory(parent);
        var manager = new SyncRootManager(NullLogger<SyncRootManager>.Instance);
        string? actualRoot = null;
        try
        {
            await using (
                var mount = new CloudMount(
                    new CloudMountOptions
                    {
                        Container = new ZipContainer(_ => File.OpenRead(FixtureArchive.LocalPath)),
                        Root = baseRoot,
                    }
                )
            )
            {
                await mount.MountAsync(testCancellationToken);
                actualRoot = mount.Root;
            }

            Assert.Equal(1, manager.CleanupMounts(parent));
            Assert.False(Directory.Exists(actualRoot));
            Assert.Equal(0, manager.CleanupMounts(parent));

            await using (
                var fresh = new CloudMount(
                    new CloudMountOptions
                    {
                        Container = new ZipContainer(_ => File.OpenRead(FixtureArchive.LocalPath)),
                        Root = baseRoot,
                    }
                )
            )
            {
                await fresh.MountAsync(testCancellationToken);
                Assert.Equal(actualRoot, fresh.Root);
            }
        }
        finally
        {
            if (actualRoot is not null && Directory.Exists(actualRoot))
                manager.Unregister(actualRoot);

            Directory.Delete(parent, true);
        }
    }
}
