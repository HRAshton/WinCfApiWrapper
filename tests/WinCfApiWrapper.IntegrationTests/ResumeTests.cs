using System.IO.Compression;
using Microsoft.Extensions.Logging.Abstractions;
using WinCfApiWrapper.IntegrationTests.Fixtures;
using WinCfApiWrapper.Mounting;
using WinCfApiWrapper.Public;
using WinCfApiWrapper.Public.Containers;
using Xunit;

namespace WinCfApiWrapper.IntegrationTests;

[Collection("Windows sync roots")]
[Trait("Category", "Offline")]
public sealed class ResumeTests
{
    [Fact]
    public async Task ChangedArchive_CreatesSeparateMount()
    {
        string root = FixtureArchive.Root();
        string otherSource = Path.Combine(
            Path.GetTempPath(),
            "WinCfApiWrapper.Tests",
            Guid.NewGuid().ToString("N") + ".zip"
        );
        Directory.CreateDirectory(Path.GetDirectoryName(otherSource)!);
        File.Copy(FixtureArchive.LocalPath, otherSource);
        using (ZipArchive archive = ZipFile.Open(otherSource, ZipArchiveMode.Update))
            archive.CreateEntry("different.txt");

        bool registered = false;
        string? otherRoot = null;
        try
        {
            await using (
                var first = new CloudMount(
                    new CloudMountOptions
                    {
                        Container = new ZipContainer(_ => File.OpenRead(FixtureArchive.LocalPath)),
                        Root = root,
                    }
                )
            )
            {
                await first.MountAsync();
                registered = true;
                await first.HydrateAsync("docs/hello.txt");
            }

            await using (
                var changed = new CloudMount(
                    new CloudMountOptions
                    {
                        Container = new ZipContainer(_ => File.OpenRead(otherSource)),
                        Root = root,
                    }
                )
            )
            {
                await changed.MountAsync();
                otherRoot = changed.Root;
                Assert.NotEqual(FixtureArchive.MountedRoot(root), otherRoot);
                Assert.True(changed.Contains("different.txt"));
            }

            Assert.True(Directory.Exists(FixtureArchive.MountedRoot(root)));
            string hydratedContent = await File.ReadAllTextAsync(
                Path.Combine(FixtureArchive.MountedRoot(root), "docs", "hello.txt")
            );
            Assert.Equal("Hello from a checked-in ZIP fixture!\n", hydratedContent);

            await using var valid = new CloudMount(
                new CloudMountOptions
                {
                    Container = new ZipContainer(_ => File.OpenRead(FixtureArchive.LocalPath)),
                    Root = root,
                }
            );
            await valid.MountAsync();
            Assert.Equal(
                "Nested content\n",
                await File.ReadAllTextAsync(Path.Combine(valid.Root, "docs", "nested", "info.txt"))
            );
        }
        finally
        {
            try
            {
                if (registered)
                    FixtureArchive.CleanupRoot(root);

                if (otherRoot is not null && Directory.Exists(otherRoot))
                    new SyncRootManager(NullLogger<SyncRootManager>.Instance).Unregister(otherRoot);
            }
            finally
            {
                File.Delete(otherSource);
            }
        }
    }

    [Fact]
    public async Task Resume_IdenticalArchiveAtAnotherPath_RetainsFileContent()
    {
        string root = FixtureArchive.Root();
        string copy = root + ".zip";
        Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
        File.Copy(FixtureArchive.LocalPath, copy);
        var factory = new CloudMountFactory(NullLogger<CloudMount>.Instance);
        bool registered = false;
        try
        {
            ICloudMount first = factory.Open(
                new CloudMountOptions
                {
                    Container = new ZipContainer(_ => File.OpenRead(copy)),
                    Root = root,
                }
            );
            try
            {
                await first.MountAsync();
                registered = true;
                await first.HydrateAsync("docs/hello.txt");
            }
            finally
            {
                await first.DisposeAsync();
            }

            ICloudMount second = factory.Open(
                new CloudMountOptions
                {
                    Container = new ZipContainer(_ => File.OpenRead(FixtureArchive.LocalPath)),
                    Root = root,
                }
            );
            try
            {
                await second.MountAsync();
                Assert.Equal(
                    "Hello from a checked-in ZIP fixture!\n",
                    await File.ReadAllTextAsync(second.Resolve("docs/hello.txt"))
                );
                Assert.Equal(
                    "Nested content\n",
                    await File.ReadAllTextAsync(second.Resolve("docs/nested/info.txt"))
                );
            }
            finally
            {
                await second.DisposeAsync();
            }
        }
        finally
        {
            if (registered)
                FixtureArchive.CleanupRoot(root);

            File.Delete(copy);
        }
    }
}
