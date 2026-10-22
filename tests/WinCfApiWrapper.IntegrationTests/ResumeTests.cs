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
    public async Task CollidingLegacyAutomaticZipPrefixes_NeverReuseHydratedFiles()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        string baseRoot = FixtureArchive.Root();
        string archiveA = baseRoot + "-collision-a.zip";
        string archiveB = baseRoot + "-collision-b.zip";
        string legacyRoot = baseRoot + ".rzm-01beb68d";
        string? firstRoot = null;
        string? secondRoot = null;
        var manager = new SyncRootManager(NullLogger<SyncRootManager>.Instance);

        static void WriteArchive(string path, ulong value)
        {
            using ZipArchive archive = ZipFile.Open(path, ZipArchiveMode.Create);
            using Stream entry = archive
                .CreateEntry("file.txt", CompressionLevel.NoCompression)
                .Open();
            entry.Write(BitConverter.GetBytes(value));
        }

        Directory.CreateDirectory(Path.GetDirectoryName(baseRoot)!);
        WriteArchive(archiveA, 37806);
        WriteArchive(archiveB, 58888);
        try
        {
            Directory.CreateDirectory(legacyRoot);
            File.WriteAllText(Path.Combine(legacyRoot, "keep.txt"), "legacy");

            await using (var first = new CloudMount(new CloudMountOptions
            {
                Root = baseRoot,
                Container = new ZipContainer(_ => File.OpenRead(archiveA)),
            }))
            {
                await first.MountAsync(token);
                firstRoot = first.Root;
                Assert.Equal(
                    BitConverter.GetBytes((ulong)37806),
                    await File.ReadAllBytesAsync(first.Resolve("file.txt"), token)
                );
            }

            await using (var second = new CloudMount(new CloudMountOptions
            {
                Root = baseRoot,
                Container = new ZipContainer(_ => File.OpenRead(archiveB)),
            }))
            {
                await second.MountAsync(token);
                secondRoot = second.Root;
                Assert.NotEqual(firstRoot, secondRoot);
                Assert.StartsWith(baseRoot + ".rzm-v2-", secondRoot);
                Assert.Equal(
                    BitConverter.GetBytes((ulong)58888),
                    await File.ReadAllBytesAsync(second.Resolve("file.txt"), token)
                );
            }

            // Archive A remains hydrated in its own directory. The legacy
            // 32-bit path is neither resumed nor automatically deleted.
            Assert.Equal(
                BitConverter.GetBytes((ulong)37806),
                await File.ReadAllBytesAsync(Path.Combine(firstRoot!, "file.txt"), token)
            );
            Assert.Equal("legacy", File.ReadAllText(Path.Combine(legacyRoot, "keep.txt")));
        }
        finally
        {
            if (firstRoot is not null && Directory.Exists(firstRoot))
                manager.Unregister(firstRoot);
            if (secondRoot is not null && Directory.Exists(secondRoot))
                manager.Unregister(secondRoot);
            if (Directory.Exists(legacyRoot))
                Directory.Delete(legacyRoot, true);
            File.Delete(archiveA);
            File.Delete(archiveB);
        }
    }

    [Fact]
    public async Task ChangedArchive_CreatesSeparateMount()
    {
        CancellationToken testCancellationToken = TestContext.Current.CancellationToken;

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
                await first.MountAsync(testCancellationToken);
                registered = true;
                await first.HydrateAsync("docs/hello.txt", testCancellationToken);
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
                await changed.MountAsync(testCancellationToken);
                otherRoot = changed.Root;
                Assert.NotEqual(FixtureArchive.MountedRoot(root), otherRoot);
                Assert.True(changed.Contains("different.txt"));
            }

            Assert.True(Directory.Exists(FixtureArchive.MountedRoot(root)));
            string hydratedContent = await File.ReadAllTextAsync(
                Path.Combine(FixtureArchive.MountedRoot(root), "docs", "hello.txt"),
                testCancellationToken
            );
            Assert.Equal("Hello from a checked-in ZIP fixture!\n", hydratedContent);

            await using var valid = new CloudMount(
                new CloudMountOptions
                {
                    Container = new ZipContainer(_ => File.OpenRead(FixtureArchive.LocalPath)),
                    Root = root,
                }
            );
            await valid.MountAsync(testCancellationToken);
            Assert.Equal(
                "Nested content\n",
                await File.ReadAllTextAsync(
                    Path.Combine(valid.Root, "docs", "nested", "info.txt"),
                    testCancellationToken
                )
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
        CancellationToken testCancellationToken = TestContext.Current.CancellationToken;

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
                await first.MountAsync(testCancellationToken);
                registered = true;
                await first.HydrateAsync("docs/hello.txt", testCancellationToken);
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
                await second.MountAsync(testCancellationToken);
                Assert.Equal(
                    "Hello from a checked-in ZIP fixture!\n",
                    await File.ReadAllTextAsync(
                        second.Resolve("docs/hello.txt"),
                        testCancellationToken
                    )
                );
                Assert.Equal(
                    "Nested content\n",
                    await File.ReadAllTextAsync(
                        second.Resolve("docs/nested/info.txt"),
                        testCancellationToken
                    )
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
