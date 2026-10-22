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
    public async Task HydratedFile_ReadAndHydrateAgain_DoNotOpenArchive()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        string root = FixtureArchive.Root();
        int opens = 0;
        bool unavailable = false;
        var container = new ZipContainer(_ =>
        {
            Interlocked.Increment(ref opens);
            if (unavailable)
                throw new IOException("Archive is offline.");
            return File.OpenRead(FixtureArchive.LocalPath);
        });
        var options = new CloudMountOptions { Container = container, Root = root };
        bool registered = false;
        try
        {
            await using (var mount = new CloudMount(options))
            {
                await mount.MountAsync(token);
                registered = true;
                await mount.HydrateAsync("docs/hello.txt", token);
                int afterHydration = opens;
                unavailable = true;
                await mount.HydrateAsync("docs/hello.txt", token);
                Assert.Equal("Hello from a checked-in ZIP fixture!\n", await File.ReadAllTextAsync(mount.Resolve("docs/hello.txt"), token));
                Assert.Equal(afterHydration, opens);
            }

            int beforeResume = opens;
            await using var resumed = new CloudMount(options);
            await resumed.MountAsync(token);
            Assert.Equal("Hello from a checked-in ZIP fixture!\n", await File.ReadAllTextAsync(resumed.Resolve("docs/hello.txt"), token));
            Assert.Equal(beforeResume, opens);
        }
        finally
        {
            if (registered)
                FixtureArchive.CleanupRoot(root);
        }
    }

    [Fact]
    public async Task NativeFetchCallbacks_SurviveOrphanedMountDuringNativeFetch()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        (string root, WeakReference<CloudMount> weakMount) = await OpenOrphanedMountAsync(token);
        CloudMount? recovered = null;
        try
        {
            // The helper has returned: the caller owns only a root path and a weak reference.
            // A fresh GUID mount has not had its placeholders hydrated by this test.
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);

            // Resolve the path without recovering the CloudMount instance first.
            // This read must dispatch a real CFAPI FETCH_DATA callback after the GC.
            string file = Path.Combine(root, "docs", "hello.txt");
            Assert.Equal(
                "Hello from a checked-in ZIP fixture!\n",
                await File.ReadAllTextAsync(file, token)
            );

            recovered = Assert.IsType<CloudMount>(
                weakMount.TryGetTarget(out CloudMount? target) ? target : null
            );
            Assert.True(recovered.CallbacksRooted);
            Assert.True(recovered.Statistics.FetchAttempts > 0);
            Assert.True(recovered.Statistics.BytesTransferred > 0);
            Assert.Equal(0, recovered.Statistics.FailedFetches);
        }
        finally
        {
            // If the owner was collected, it cannot be safely disconnected; preserve the
            // directory for diagnosis rather than unregistering a potentially live root.
            if (recovered is null && weakMount.TryGetTarget(out CloudMount? owner))
                recovered = owner;

            if (recovered is not null)
            {
                await recovered.DisposeAsync();
                Assert.False(recovered.CallbacksRooted);
                new SyncRootManager(NullLogger<SyncRootManager>.Instance).Unregister(root);
            }
        }
    }

    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.NoInlining
    )]
    private static async Task<(string Root, WeakReference<CloudMount> Mount)> OpenOrphanedMountAsync(
        CancellationToken token
    )
    {
        var mount = new CloudMount(
            new CloudMountOptions
            {
                Container = new ZipContainer(
                    _ => File.OpenRead(FixtureArchive.LocalPath),
                    validateEntryMetadata: false
                ),
                Root = FixtureArchive.Root(),
                TrustedContentId = "a11ce123",
                SkipResumeValidation = true,
            }
        );

        try
        {
            await mount.MountAsync(token);
            return (mount.Root, new WeakReference<CloudMount>(mount));
        }
        catch
        {
            await mount.DisposeAsync();
            throw;
        }
    }

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
