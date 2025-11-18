using Microsoft.Extensions.Logging;
using WinCfApiWrapper.Public;

namespace WinCfApiWrapper.Mounting;

internal sealed class SyncRootManager : ISyncRootManager
{
    private readonly ILogger<SyncRootManager> _logger;
    private readonly IMountOperations _operations;

    public SyncRootManager(ILogger<SyncRootManager> logger)
        : this(logger, new NativeMountOperations()) { }

    internal SyncRootManager(ILogger<SyncRootManager> logger, IMountOperations operations)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _operations = operations ?? throw new ArgumentNullException(nameof(operations));
    }

    public int CleanupMounts(string parentFolder)
    {
        RequireWindows();
        ArgumentException.ThrowIfNullOrWhiteSpace(parentFolder);
        string parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parentFolder));
        if (!Directory.Exists(parent))
            throw new DirectoryNotFoundException($"Mount parent is unavailable: {parent}");

        if (string.Equals(parent, Path.GetPathRoot(parent), StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "Mount parent must be a dedicated folder, not a volume root.",
                nameof(parentFolder)
            );
        }

        int cleaned = 0;
        var errors = new List<Exception>();
        foreach (string root in Directory.EnumerateDirectories(parent))
        {
            if (!MountPath.IsMount(root))
                continue;

            try
            {
                if (!Directory.Exists(parent))
                {
                    throw new DirectoryNotFoundException(
                        $"Mount parent became unavailable: {parent}"
                    );
                }

                MountCleanup.Delete(root, _operations);
                cleaned++;
                _logger.LogInformation("Removed abandoned sync root {Root}", root);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Could not remove abandoned sync root {Root}", root);
                errors.Add(new IOException($"Could not clean mount: {root}", ex));
            }
        }

        if (errors.Count != 0)
        {
            throw new AggregateException(
                $"Mount cleanup removed {cleaned} folders but had errors.",
                errors
            );
        }

        return cleaned;
    }

    public void Unregister(string root)
    {
        RequireWindows();
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        string fullPath = Path.GetFullPath(root);
        MountCleanup.Delete(fullPath, _operations);
        _logger.LogInformation("Unregistered sync root {Root}", fullPath);
    }

    private static void RequireWindows()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 16299))
        {
            throw new PlatformNotSupportedException(
                "Cloud Files API requires Windows 10 version 1709 or later."
            );
        }
    }
}
