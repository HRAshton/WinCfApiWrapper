using WinCfApiWrapper.IntegrationTests.Fixtures;
using WinCfApiWrapper.Mounting;
using WinCfApiWrapper.Public;
using WinCfApiWrapper.Public.Containers;
using Xunit;

namespace WinCfApiWrapper.IntegrationTests;

[Collection("Windows sync roots")]
[Trait("Category", "Offline")]
public sealed class FetchFailureTests
{
    [Fact]
    public async Task Fetch_EntryReadFails_ConsumerReceivesIoError()
    {
        string root = FixtureArchive.Root();
        var mount = new CloudMount(
            new CloudMountOptions
            {
                Container = new ZipContainer(_ => File.OpenRead(FixtureArchive.LocalPath)),
                Root = root,
            },
            new NativeMountOperations(),
            openEntry: _ => throw new IOException("Injected archive read failure.")
        );
        bool registered = false;
        try
        {
            await mount.MountAsync();
            registered = true;
            await Assert.ThrowsAnyAsync<IOException>(() =>
                File.ReadAllTextAsync(Path.Combine(mount.Root, "docs", "hello.txt"))
                    .WaitAsync(TimeSpan.FromSeconds(5))
            );
        }
        finally
        {
            await mount.DisposeAsync();
            if (registered)
                FixtureArchive.CleanupRoot(root);
        }
    }
}
