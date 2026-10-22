using WinCfApiWrapper.Mounting;
using Xunit;

namespace WinCfApiWrapper.Tests.Mounting;

public sealed class MountPathTests
{
    [Fact]
    public void FingerprintSuffixIdentifiesOnlyOwnedFolderNames()
    {
        string parent = Path.Combine(Path.GetTempPath(), "WinCfApiWrapper.Unit");
        string basePath = Path.Combine(parent, "sample");
        string fingerprint = "ABCDEF01" + new string('B', 56);
        string root = MountPath.For(basePath, fingerprint);
        string trusted = MountPath.For(basePath, "ABCDEF01");

        Assert.Equal(basePath + ".rzm-v2-abcdef01" + new string('b', 24), root);
        Assert.Equal(basePath + ".rzm-abcdef01", trusted);
        Assert.True(MountPath.IsMount(root));
        Assert.True(MountPath.IsMount(trusted));
        Assert.False(MountPath.IsMount(basePath));
        Assert.False(MountPath.IsMount(basePath + ".rzm-" + new string('G', 8)));
        Assert.False(MountPath.IsMount(basePath + ".rzm-" + new string('A', 7)));
        Assert.False(MountPath.IsMount(basePath + ".rzm-" + new string('A', 9)));
        Assert.False(MountPath.IsMount(basePath + ".rzm-v2-" + new string('a', 31)));
        Assert.False(MountPath.IsMount(basePath + ".rzm-v2-" + new string('a', 33)));
        Assert.False(MountPath.IsMount(basePath + ".rzm-v2-" + new string('z', 32)));
        Assert.Throws<ArgumentException>(() => MountPath.For(basePath, "invalid"));
    }

    [Fact]
    public void LegacyAutomaticFolder_StillRecognizedForExplicitCleanup()
    {
        string parent = Path.Combine(
            Path.GetTempPath(), "WinCfApiWrapper.Unit", Guid.NewGuid().ToString("N")
        );
        Directory.CreateDirectory(parent);
        try
        {
            string oldRoot = Path.Combine(parent, "sample.rzm-01beb68d");
            Directory.CreateDirectory(oldRoot);
            File.WriteAllText(Path.Combine(oldRoot, "keep.txt"), "old");
            Assert.True(MountPath.IsMount(oldRoot));
            MountCleanup.Delete(oldRoot, new FakeMountOperations(registered: true));
            Assert.False(Directory.Exists(oldRoot));
        }
        finally
        {
            Directory.Delete(parent, true);
        }
    }

    [Fact]
    public void KnownZipPayloadsWithSameLegacyPrefix_GetDifferentV2Mounts()
    {
        static string Fingerprint(ulong value)
        {
            using var memory = new MemoryStream();
            using (var archive = new System.IO.Compression.ZipArchive(
                memory,
                System.IO.Compression.ZipArchiveMode.Create,
                leaveOpen: true
            ))
            {
                using Stream entry = archive
                    .CreateEntry("file.txt", System.IO.Compression.CompressionLevel.NoCompression)
                    .Open();
                entry.Write(BitConverter.GetBytes(value));
            }

            memory.Position = 0;
            return new WinCfApiWrapper.Archives.ZipCatalog(memory, maxEntries: 1).Fingerprint;
        }

        string a = Fingerprint(37806);
        string b = Fingerprint(58888);
        Assert.Equal("01BEB68D", a[..8]);
        Assert.Equal(a[..8], b[..8]);
        Assert.NotEqual(a, b);
        string basePath = Path.Combine(Path.GetTempPath(), "WinCfApiWrapper.Unit", "collision");
        string rootA = MountPath.For(basePath, a);
        string rootB = MountPath.For(basePath, b);
        Assert.NotEqual(rootA, rootB);
        Assert.True(MountPath.IsMount(rootA));
        Assert.True(MountPath.IsMount(rootB));
    }

    [Fact]
    public void Cleanup_AlreadyRemovedRoot_AlsoRemovesValidSiblingMarker()
    {
        string parent = Path.Combine(
            Path.GetTempPath(),
            "WinCfApiWrapper.Unit",
            Guid.NewGuid().ToString("N")
        );
        Directory.CreateDirectory(parent);
        try
        {
            string root = Path.Combine(parent, "removed.rzm-1234abcd");
            string marker = root + ".wincfapiwrapper-pending-cleanup";
            File.WriteAllText(marker, "WinCfApiWrapper cleanup v1");

            MountCleanup.Delete(root, new FakeMountOperations());
            Assert.False(File.Exists(marker));
            Assert.False(Directory.Exists(root));
        }
        finally
        {
            Directory.Delete(parent, true);
        }
    }

    [Fact]
    public void Cleanup_MissingParent_RemainsIdempotent()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "WinCfApiWrapper.Unit",
            Guid.NewGuid().ToString("N"),
            "missing.rzm-v2-" + new string('a', 32)
        );
        MountCleanup.Delete(root, new FakeMountOperations());
        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public void Cleanup_RejectsParentJunctionBeforeUnregistration()
    {
        string parent = Path.Combine(
            Path.GetTempPath(),
            "WinCfApiWrapper.Unit",
            Guid.NewGuid().ToString("N")
        );
        string target = Path.Combine(parent, "actual-parent");
        string redirectedParent = Path.Combine(parent, "redirected-parent");
        string root = Path.Combine(redirectedParent, "mount.rzm-1234abcd");
        Directory.CreateDirectory(target);
        Directory.CreateDirectory(Path.Combine(target, "mount.rzm-1234abcd"));
        File.WriteAllText(Path.Combine(target, "mount.rzm-1234abcd", "keep.txt"), "keep");
        try
        {
            CreateJunction(redirectedParent, target);
            var fake = new FakeMountOperations(registered: true);

            Assert.Throws<IOException>(() => MountCleanup.Delete(root, fake));
            Assert.DoesNotContain("unregister", fake.Calls);
            Assert.Equal("keep", File.ReadAllText(Path.Combine(root, "keep.txt")));
        }
        finally
        {
            if (Directory.Exists(redirectedParent))
                Directory.Delete(redirectedParent);
            Directory.Delete(parent, true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Cleanup_RejectsUnrecognizedSiblingMarkerBeforeUnregistration(bool directoryMarker)
    {
        string parent = Path.Combine(
            Path.GetTempPath(),
            "WinCfApiWrapper.Unit",
            Guid.NewGuid().ToString("N")
        );
        string root = Path.Combine(parent, "mount.rzm-1234abcd");
        string marker = root + ".wincfapiwrapper-pending-cleanup";
        Directory.CreateDirectory(root);
        if (directoryMarker)
            Directory.CreateDirectory(marker);
        else
            File.WriteAllText(marker, "some other owner");
        try
        {
            var fake = new FakeMountOperations(registered: true);

            IOException error = Assert.Throws<IOException>(() => MountCleanup.Delete(root, fake));
            Assert.Contains("Unrecognized cleanup marker", error.Message);
            Assert.True(fake.Registered);
            Assert.DoesNotContain("unregister", fake.Calls);
            Assert.True(Directory.Exists(root));
        }
        finally
        {
            Directory.Delete(parent, true);
        }
    }

    [Fact]
    public void Cleanup_RejectsUnregisteredRootWithDirectoryMarker()
    {
        string parent = Path.Combine(
            Path.GetTempPath(),
            "WinCfApiWrapper.Unit",
            Guid.NewGuid().ToString("N")
        );
        string root = Path.Combine(parent, "mount.rzm-1234abcd");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(root + ".wincfapiwrapper-pending-cleanup");
        try
        {
            var fake = new FakeMountOperations();
            Assert.Throws<IOException>(() => MountCleanup.Delete(root, fake));
            Assert.DoesNotContain("unregister", fake.Calls);
            Assert.True(Directory.Exists(root));
        }
        finally
        {
            Directory.Delete(parent, true);
        }
    }

    [Fact]
    public void Cleanup_RejectsRootSwappedToJunctionDuringOwnershipCheck()
    {
        string parent = Path.Combine(
            Path.GetTempPath(),
            "WinCfApiWrapper.Unit",
            Guid.NewGuid().ToString("N")
        );
        string root = Path.Combine(parent, "mount.rzm-1234abcd");
        string moved = Path.Combine(parent, "original");
        string target = Path.Combine(parent, "outside");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "keep.txt"), "untouched");
        try
        {
            var fake = new FakeMountOperations(
                fault: step =>
                {
                    if (step == "check-owner")
                    {
                        Directory.Move(root, moved);
                        CreateJunction(root, target);
                    }
                },
                registered: true
            );

            IOException error = Assert.Throws<IOException>(() => MountCleanup.Delete(root, fake));
            Assert.Contains("changed during ownership validation", error.Message);
            Assert.DoesNotContain("unregister", fake.Calls);
            Assert.True(Directory.Exists(moved));
            Assert.Equal("untouched", File.ReadAllText(Path.Combine(target, "keep.txt")));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root);
            Directory.Delete(parent, true);
        }
    }

    [Fact]
    public void Cleanup_RefusesUnregisteredForeignFolderWithMatchingSuffix()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "WinCfApiWrapper.Unit",
            Guid.NewGuid().ToString("N"),
            "foreign.rzm-1234abcd"
        );
        Directory.CreateDirectory(root);
        try
        {
            string importantFile = Path.Combine(root, "keep.txt");
            File.WriteAllText(importantFile, "foreign data");
            var fake = new FakeMountOperations();

            IOException error = Assert.Throws<IOException>(() => MountCleanup.Delete(root, fake));
            Assert.Contains("ownership evidence", error.Message);
            Assert.Equal("foreign data", File.ReadAllText(importantFile));
            Assert.DoesNotContain("unregister", fake.Calls);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(root)!, true);
        }
    }

    [Fact]
    public void Cleanup_RejectsRegistrationBelongingToAnotherProvider()
    {
        string parent = Path.Combine(
            Path.GetTempPath(),
            "WinCfApiWrapper.Unit",
            Guid.NewGuid().ToString("N")
        );
        string root = Path.Combine(parent, "foreign.rzm-1234abcd");
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "keep.txt"), "foreign");
            var fake = new FakeMountOperations(registered: true)
            {
                OwnsRegistration = false,
            };

            Assert.Throws<IOException>(() => MountCleanup.Delete(root, fake));
            Assert.True(fake.Registered);
            Assert.DoesNotContain("unregister", fake.Calls);
            Assert.True(File.Exists(Path.Combine(root, "keep.txt")));
        }
        finally
        {
            Directory.Delete(parent, true);
        }
    }

    [Fact]
    public void Cleanup_RejectsNestedDirectoryJunctionWithoutTouchingTarget()
    {
        string parent = Path.Combine(
            Path.GetTempPath(),
            "WinCfApiWrapper.Unit",
            Guid.NewGuid().ToString("N")
        );
        string root = Path.Combine(parent, "mount.rzm-1234abcd");
        string outside = Path.Combine(parent, "outside");
        string junction = Path.Combine(root, "link");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "keep.txt"), "outside");
        try
        {
            CreateJunction(junction, outside);
            var fake = new FakeMountOperations(registered: true);

            IOException error = Assert.Throws<IOException>(() => MountCleanup.Delete(root, fake));
            Assert.Contains("reparse point", error.Message);
            Assert.Equal("outside", File.ReadAllText(Path.Combine(outside, "keep.txt")));
            Assert.True(fake.Registered);
            Assert.DoesNotContain("unregister", fake.Calls);
        }
        finally
        {
            if (Directory.Exists(junction))
                Directory.Delete(junction);
            Directory.Delete(parent, true);
        }
    }

    [Fact]
    public void Cleanup_RejectsRootJunctionWithoutTouchingTarget()
    {
        string parent = Path.Combine(
            Path.GetTempPath(),
            "WinCfApiWrapper.Unit",
            Guid.NewGuid().ToString("N")
        );
        string target = Path.Combine(parent, "target");
        string junction = Path.Combine(parent, "fake.rzm-1234abcd");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "keep.txt"), "outside");
        try
        {
            CreateJunction(junction, target);
            var fake = new FakeMountOperations(registered: true);

            Assert.Throws<IOException>(() => MountCleanup.Delete(junction, fake));
            Assert.True(File.Exists(Path.Combine(target, "keep.txt")));
            Assert.DoesNotContain("unregister", fake.Calls);
        }
        finally
        {
            if (Directory.Exists(junction))
                Directory.Delete(junction);
            Directory.Delete(parent, true);
        }
    }

    [Fact]
    public void Cleanup_PathSwappedToJunctionDuringUnregister_FailsBeforeDeletion()
    {
        string parent = Path.Combine(
            Path.GetTempPath(),
            "WinCfApiWrapper.Unit",
            Guid.NewGuid().ToString("N")
        );
        string root = Path.Combine(parent, "mount.rzm-1234abcd");
        string displaced = Path.Combine(parent, "original");
        string outside = Path.Combine(parent, "outside");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "keep.txt"), "outside");
        try
        {
            var fake = new FakeMountOperations(
                fault: step =>
                {
                    if (step == "unregister")
                    {
                        Directory.Move(root, displaced);
                        CreateJunction(root, outside);
                    }
                },
                registered: true
            );
            Assert.Throws<IOException>(() => MountCleanup.Delete(root, fake));

            Assert.Equal("outside", File.ReadAllText(Path.Combine(outside, "keep.txt")));
            Assert.True(Directory.Exists(displaced));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root);
            Directory.Delete(parent, true);
        }
    }

    private static void CreateJunction(string link, string target)
    {
        var start = new System.Diagnostics.ProcessStartInfo("cmd.exe")
        {
            Arguments = $"/d /c mklink /J \"{link}\" \"{target}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using System.Diagnostics.Process process = System.Diagnostics.Process.Start(start)!;
        try
        {
            Assert.True(process.WaitForExit(5000), "Creating directory junction timed out.");
            Assert.Equal(0, process.ExitCode);
        }
        finally
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
    }

    [Fact]
    public void CleanupRejectsUnmarkedFolder()
    {
        string parent = Path.Combine(
            Path.GetTempPath(),
            "WinCfApiWrapper.Unit",
            Guid.NewGuid().ToString("N")
        );
        Directory.CreateDirectory(parent);
        try
        {
            string root = Path.Combine(parent, "sample");
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, "keep.txt"), "keep");

            Assert.Throws<IOException>(() => MountCleanup.Delete(root, new FakeMountOperations()));
            Assert.True(File.Exists(Path.Combine(root, "keep.txt")));
        }
        finally
        {
            Directory.Delete(parent, true);
        }
    }

    [Fact]
    public void CleanupRefusesRegistrationAfterFailedRegistration()
    {
        string parent = Path.Combine(
            Path.GetTempPath(),
            "WinCfApiWrapper.Unit",
            Guid.NewGuid().ToString("N")
        );
        Directory.CreateDirectory(parent);
        try
        {
            string root = MountPath.For(Path.Combine(parent, "sample"), new string('A', 64));
            Directory.CreateDirectory(root);
            var operations = new FakeMountOperations(registered: true);

            Assert.Throws<IOException>(() => MountCleanup.Delete(root, operations, true));
            Assert.True(operations.Registered);
            Assert.True(Directory.Exists(root));
        }
        finally
        {
            Directory.Delete(parent, true);
        }
    }

    [Fact]
    public void Cleanup_RefusesOwnedNameWhenItIsAFile()
    {
        string parent = Path.Combine(
            Path.GetTempPath(),
            "WinCfApiWrapper.Unit",
            Guid.NewGuid().ToString("N")
        );
        Directory.CreateDirectory(parent);
        try
        {
            string root = MountPath.For(Path.Combine(parent, "sample"), new string('A', 64));
            File.WriteAllText(root, "keep");

            Assert.Throws<IOException>(() => MountCleanup.Delete(root, new FakeMountOperations()));
            Assert.Equal("keep", File.ReadAllText(root));
        }
        finally
        {
            Directory.Delete(parent, true);
        }
    }
}
