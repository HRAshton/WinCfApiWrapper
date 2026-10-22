using Microsoft.Extensions.Logging.Abstractions;
using WinCfApiWrapper.Archives;
using WinCfApiWrapper.Mounting;
using WinCfApiWrapper.Public.Containers;

namespace WinCfApiWrapper.IntegrationTests.Fixtures;

internal static class FixtureArchive
{
    public static string LocalPath =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "archive.zip");

    public static string Root()
    {
        return Path.Combine(
            Path.GetTempPath(),
            "WinCfApiWrapper.Tests",
            Guid.NewGuid().ToString("N")
        );
    }

    public static string MountedRoot(string baseRoot)
    {
        var catalog = new ZipCatalog(
            File.OpenRead(LocalPath),
            new ArchiveResourceLimits().MaxEntries
        );

        return MountPath.For(baseRoot, catalog.Fingerprint);
    }

    public static void CleanupRoot(string root)
    {
        string testParent = Path.GetFullPath(
            Path.Combine(Path.GetTempPath(), "WinCfApiWrapper.Tests")
        );
        string name = Path.GetFileName(root);
        string baseName = MountPath.IsMount(root)
            ? name[..name.LastIndexOf(".rzm-", StringComparison.OrdinalIgnoreCase)]
            : name;
        bool rootIsOutsideGeneratedTestRoots =
            !string.Equals(
                Path.GetDirectoryName(Path.GetFullPath(root)),
                testParent,
                StringComparison.OrdinalIgnoreCase
            ) || !Guid.TryParseExact(baseName, "N", out _);
        if (rootIsOutsideGeneratedTestRoots)
        {
            throw new ArgumentException(
                "Refusing to clean a path outside the generated test roots.",
                nameof(root)
            );
        }

        new SyncRootManager(NullLogger<SyncRootManager>.Instance).Unregister(
            MountPath.IsMount(root) ? root : MountedRoot(root)
        );
    }
}
