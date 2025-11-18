using Microsoft.Extensions.Logging.Abstractions;
using WinCfApiWrapper.Mounting;
using Xunit;

namespace WinCfApiWrapper.Tests.Mounting;

public sealed class SyncRootManagerTests
{
    [Fact]
    public void CleanupMounts_IgnoresUnmarkedChildren()
    {
        string parent = Path.Combine(
            Path.GetTempPath(),
            "WinCfApiWrapper.Unit",
            Guid.NewGuid().ToString("N")
        );
        Directory.CreateDirectory(parent);
        try
        {
            string unrelated = Path.Combine(parent, "unrelated");
            Directory.CreateDirectory(unrelated);
            Assert.Equal(
                0,
                new SyncRootManager(NullLogger<SyncRootManager>.Instance).CleanupMounts(parent)
            );
            Assert.True(Directory.Exists(unrelated));
        }
        finally
        {
            Directory.Delete(parent, true);
        }
    }

    [Fact]
    public void CleanupMounts_RequiresAccessibleParent()
    {
        string parent = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Assert.Throws<DirectoryNotFoundException>(() =>
            new SyncRootManager(NullLogger<SyncRootManager>.Instance).CleanupMounts(parent)
        );
    }

    [Fact]
    public void CleanupMounts_RemovesOwnedFoldersAndPreservesUnrelatedChildren()
    {
        string parent = Path.Combine(
            Path.GetTempPath(),
            "WinCfApiWrapper.Unit",
            Guid.NewGuid().ToString("N")
        );
        Directory.CreateDirectory(parent);
        try
        {
            string owned = MountPath.For(Path.Combine(parent, "sample"), new string('A', 64));
            string unrelated = Path.Combine(parent, "other");
            Directory.CreateDirectory(owned);
            Directory.CreateDirectory(unrelated);
            var fake = new FakeMountOperations(registered: true);
            var manager = new SyncRootManager(NullLogger<SyncRootManager>.Instance, fake);

            Assert.Equal(1, manager.CleanupMounts(parent));
            Assert.False(Directory.Exists(owned));
            Assert.True(Directory.Exists(unrelated));
            Assert.Contains("unregister", fake.Calls);
        }
        finally
        {
            Directory.Delete(parent, true);
        }
    }

    [Fact]
    public void CleanupMounts_OneFailure_ContinuesAndReportsRetainedFolder()
    {
        string parent = Path.Combine(
            Path.GetTempPath(),
            "WinCfApiWrapper.Unit",
            Guid.NewGuid().ToString("N")
        );
        Directory.CreateDirectory(parent);
        try
        {
            string first = MountPath.For(Path.Combine(parent, "first"), new string('A', 64));
            string second = MountPath.For(Path.Combine(parent, "second"), new string('B', 64));
            Directory.CreateDirectory(first);
            Directory.CreateDirectory(second);
            int unregisterCalls = 0;
            var fake = new FakeMountOperations(registered: true)
            {
                Fault = name =>
                {
                    if (name == "unregister" && ++unregisterCalls == 1)
                        throw new IOException("injected failure");
                },
            };
            var manager = new SyncRootManager(NullLogger<SyncRootManager>.Instance, fake);

            AggregateException error = Assert.Throws<AggregateException>(() =>
                manager.CleanupMounts(parent)
            );
            Assert.Single(error.InnerExceptions);
            Assert.True(Directory.Exists(first));
            Assert.False(Directory.Exists(second));
        }
        finally
        {
            Directory.Delete(parent, true);
        }
    }

    [Fact]
    public void Unregister_RejectsUnownedFolderWithoutDeletingIt()
    {
        string parent = Path.Combine(
            Path.GetTempPath(),
            "WinCfApiWrapper.Unit",
            Guid.NewGuid().ToString("N")
        );
        Directory.CreateDirectory(parent);
        try
        {
            var fake = new FakeMountOperations();
            var manager = new SyncRootManager(NullLogger<SyncRootManager>.Instance, fake);
            Assert.Throws<IOException>(() => manager.Unregister(parent));
            Assert.True(Directory.Exists(parent));
            Assert.Empty(fake.Calls);
        }
        finally
        {
            Directory.Delete(parent, true);
        }
    }

    [Fact]
    public void Unregister_OwnedFolder_RemovesRegistrationAndDirectory()
    {
        string parent = Path.Combine(
            Path.GetTempPath(),
            "WinCfApiWrapper.Unit",
            Guid.NewGuid().ToString("N")
        );
        string root = MountPath.For(Path.Combine(parent, "sample"), new string('A', 64));
        Directory.CreateDirectory(root);
        try
        {
            var fake = new FakeMountOperations(registered: true);
            var manager = new SyncRootManager(NullLogger<SyncRootManager>.Instance, fake);

            manager.Unregister(root);

            Assert.False(Directory.Exists(root));
            Assert.False(fake.Registered);
        }
        finally
        {
            Directory.Delete(parent, true);
        }
    }

    [Fact]
    public void CleanupMounts_RejectsVolumeRoot()
    {
        var manager = new SyncRootManager(
            NullLogger<SyncRootManager>.Instance,
            new FakeMountOperations()
        );
        string root = Path.GetPathRoot(Path.GetTempPath())!;

        Assert.Throws<ArgumentException>(() => manager.CleanupMounts(root));
    }
}
