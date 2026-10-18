using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32.SafeHandles;
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
        CancellationToken testCancellationToken = TestContext.Current.CancellationToken;

        string root = FixtureArchive.Root();
        string source = Path.Combine(Path.GetDirectoryName(root)!, Path.GetFileName(root) + ".zip");
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        File.Copy(FixtureArchive.LocalPath, source);
        ICloudMount mount = new CloudMountFactory(NullLogger<CloudMount>.Instance).Open(
            new CloudMountOptions
            {
                Container = new ZipContainer(_ =>
                    File.Open(
                        source,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.Read | FileShare.Delete
                    )
                ),
                Root = root,
            }
        );
        bool registered = false;
        try
        {
            await mount.MountAsync(testCancellationToken);
            registered = true;
            await mount.HydrateAsync("docs/hello.txt", testCancellationToken);

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
    public async Task Dispose_AfterHydration_ReleasesSourceArchiveHandles()
    {
        CancellationToken testCancellationToken = TestContext.Current.CancellationToken;

        string root = FixtureArchive.Root();
        string source = Path.Combine(Path.GetDirectoryName(root)!, Path.GetFileName(root) + ".zip");
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        File.Copy(FixtureArchive.LocalPath, source);
        var handles = new ConcurrentBag<SafeFileHandle>();
        ICloudMount mount = new CloudMountFactory(NullLogger<CloudMount>.Instance).Open(
            new CloudMountOptions
            {
                Container = new ZipContainer(_ =>
                {
                    FileStream stream = File.OpenRead(source);
                    handles.Add(stream.SafeFileHandle);
                    return stream;
                }),
                Root = root,
            }
        );
        bool registered = false;
        try
        {
            await mount.MountAsync(testCancellationToken);
            registered = true;
            await mount.HydrateAsync("docs/hello.txt", testCancellationToken);

            await mount.DisposeAsync();

            Assert.True(handles.Count >= 2, "Expected indexing and hydration to open the archive.");
            Assert.All(handles, handle => Assert.True(handle.IsClosed));
            using FileStream exclusive = File.Open(
                source,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.None
            );
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
                File.Delete(source);
            }
        }
    }

    [Fact]
    public async Task DisposedAndUnregisteredMount_DoesNotPreventRootDeletion()
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
            Assert.Equal(MountState.Mounted, mount.State);
            Assert.True(mount.Contains("docs/hello.txt"));
            Assert.Equal(
                "Hello from a checked-in ZIP fixture!\n",
                await File.ReadAllTextAsync(mount.Resolve("docs/hello.txt"), testCancellationToken)
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
            string localDirectory = Path.Combine(mount.Root, "local");
            Directory.CreateDirectory(localDirectory);
            string localFile = Path.Combine(localDirectory, "note.txt");
            await File.WriteAllTextAsync(localFile, "local only", testCancellationToken);
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
            await resumed.MountAsync(testCancellationToken);
            Assert.Equal(
                "local only",
                await File.ReadAllTextAsync(localFile, testCancellationToken)
            );
            Assert.Equal(
                "Hello from a checked-in ZIP fixture!\n",
                await File.ReadAllTextAsync(
                    resumed.Resolve("docs/hello.txt"),
                    testCancellationToken
                )
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
