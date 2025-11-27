using Microsoft.Extensions.Logging.Abstractions;
using WinCfApiWrapper.IntegrationTests.Fixtures;
using WinCfApiWrapper.Mounting;
using WinCfApiWrapper.Public;
using WinCfApiWrapper.Public.Containers;
using Xunit;

namespace WinCfApiWrapper.IntegrationTests;

[Collection("Windows sync roots")]
[Trait("Category", "Offline")]
public sealed class MountingTests
{
    [Fact]
    public async Task Mount_DoesNotPreventSourceArchiveDeletion()
    {
        string root = FixtureArchive.Root();
        string source = Path.Combine(Path.GetDirectoryName(root)!, Path.GetFileName(root) + ".zip");
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        File.Copy(FixtureArchive.LocalPath, source);
        ICloudMount mount = new CloudMountFactory(NullLogger<CloudMount>.Instance).Open(
            new CloudMountOptions
            {
                Container = new ZipContainer(_ => File.OpenRead(source)),
                Root = root,
            }
        );
        bool registered = false;
        try
        {
            await mount.MountAsync();
            registered = true;
            await mount.HydrateAsync("docs/hello.txt");

            File.Delete(source);
            Assert.False(File.Exists(source));
        }
        finally
        {
            await mount.DisposeAsync();
            try
            {
                if (registered)
                    FixtureArchive.CleanupRoot(root);
            }
            finally
            {
                if (File.Exists(source))
                    File.Delete(source);
            }
        }
    }

    [Fact]
    public async Task DisposedAndUnregisteredMount_DoesNotPreventRootDeletion()
    {
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
            await mount.MountAsync();
            registered = true;
            await mount.HydrateAsync("docs/hello.txt");
            await mount.DisposeAsync();

            new SyncRootManager(NullLogger<SyncRootManager>.Instance).Unregister(mount.Root);
            Assert.False(Directory.Exists(mount.Root));
            registered = false;
        }
        finally
        {
            await mount.DisposeAsync();
            if (registered)
                FixtureArchive.CleanupRoot(root);
        }
    }

    [Fact]
    public async Task Mount_CreatesPlaceholdersAndReadsBytes()
    {
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
            await mount.MountAsync();
            registered = true;
            Assert.Equal(MountState.Mounted, mount.State);
            Assert.True(mount.Contains("docs/hello.txt"));
            Assert.Equal(
                "Hello from a checked-in ZIP fixture!\n",
                await File.ReadAllTextAsync(mount.Resolve("docs/hello.txt"))
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
    public async Task Mount_LocalFileCreation_SurvivesResumeWithoutChangingArchiveCatalog()
    {
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
            await mount.MountAsync();
            registered = true;
            string localDirectory = Path.Combine(mount.Root, "local");
            Directory.CreateDirectory(localDirectory);
            string localFile = Path.Combine(localDirectory, "note.txt");
            await File.WriteAllTextAsync(localFile, "local only");
            Assert.False(mount.Contains("local/note.txt"));

            await mount.DisposeAsync();
            await using ICloudMount resumed = new CloudMountFactory(
                NullLogger<CloudMount>.Instance
            ).Open(
                new CloudMountOptions
                {
                    Container = new ZipContainer(_ => File.OpenRead(FixtureArchive.LocalPath)),
                    Root = root,
                }
            );
            await resumed.MountAsync();
            Assert.Equal("local only", await File.ReadAllTextAsync(localFile));
            Assert.Equal(
                "Hello from a checked-in ZIP fixture!\n",
                await File.ReadAllTextAsync(resumed.Resolve("docs/hello.txt"))
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
