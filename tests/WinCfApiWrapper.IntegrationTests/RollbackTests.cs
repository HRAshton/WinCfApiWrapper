using Microsoft.Extensions.Logging.Abstractions;
using WinCfApiWrapper.IntegrationTests.Fixtures;
using WinCfApiWrapper.Mounting;
using WinCfApiWrapper.Public;
using WinCfApiWrapper.Public.Containers;
using Xunit;

namespace WinCfApiWrapper.IntegrationTests;

[Collection("Windows sync roots")]
[Trait("Category", "Offline")]
public sealed class RollbackTests
{
    [Fact]
    public async Task Mount_NativeUnregisterFailure_PreservesRecoverableRoot()
    {
        string root = FixtureArchive.Root();
        try
        {
            await using var failed = new CloudMount(
                new CloudMountOptions
                {
                    Container = new ZipContainer(_ => File.OpenRead(FixtureArchive.LocalPath)),
                    Root = root,
                },
                new FaultingNativeMountOperations(true)
            );
            AggregateException error = await Assert.ThrowsAsync<AggregateException>(() =>
                failed.MountAsync(TestContext.Current.CancellationToken)
            );
            Assert.Contains("unregistration failure", error.ToString());
            Assert.True(Directory.Exists(FixtureArchive.MountedRoot(root)));
            Assert.NotEmpty(
                Directory.EnumerateFiles(
                    FixtureArchive.MountedRoot(root),
                    "*",
                    SearchOption.AllDirectories
                )
            );
        }
        finally
        {
            if (Directory.Exists(FixtureArchive.MountedRoot(root)))
            {
                new SyncRootManager(NullLogger<SyncRootManager>.Instance).Unregister(
                    FixtureArchive.MountedRoot(root)
                );
            }
        }

        Assert.False(Directory.Exists(FixtureArchive.MountedRoot(root)));
    }

    [Fact]
    public async Task Mount_NativePlaceholderFailure_LeavesRootReusable()
    {
        CancellationToken testCancellationToken = TestContext.Current.CancellationToken;

        string root = FixtureArchive.Root();
        await using (
            var failed = new CloudMount(
                new CloudMountOptions
                {
                    Container = new ZipContainer(_ => File.OpenRead(FixtureArchive.LocalPath)),
                    Root = root,
                },
                new FaultingNativeMountOperations()
            )
        )
        {
            IOException error = await Assert.ThrowsAsync<IOException>(() =>
                failed.MountAsync(testCancellationToken)
            );
            Assert.Contains("Injected failure", error.Message);
        }

        Assert.False(Directory.Exists(FixtureArchive.MountedRoot(root)));

        // A real second registration proves the first registration was released.
        var fresh = new CloudMount(
            new CloudMountOptions
            {
                Container = new ZipContainer(_ => File.OpenRead(FixtureArchive.LocalPath)),
                Root = root,
            }
        );
        bool registered = false;
        try
        {
            await fresh.MountAsync(testCancellationToken);
            registered = true;
            Assert.Equal(MountState.Mounted, fresh.State);
        }
        finally
        {
            await fresh.DisposeAsync();
            if (registered)
                FixtureArchive.CleanupRoot(root);
        }
    }
}
