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
        string fingerprint = new('A', 64);
        string root = MountPath.For(basePath, fingerprint);

        Assert.Equal(basePath + ".rzm-" + fingerprint, root);
        Assert.True(MountPath.IsMount(root));
        Assert.False(MountPath.IsMount(basePath));
        Assert.False(MountPath.IsMount(basePath + ".rzm-" + new string('A', 63)));
        Assert.False(MountPath.IsMount(basePath + ".rzm-" + new string('G', 64)));
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
