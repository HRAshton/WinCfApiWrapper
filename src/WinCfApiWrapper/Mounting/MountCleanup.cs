namespace WinCfApiWrapper.Mounting;

internal static class MountCleanup
{
    // Written only when cleaning up or rolling back, never on the mount/fetch path.
    // This is evidence of a prior cleanup attempt, not an authentication token:
    // the application must exclusively own the containing parent directory.
    private const string PendingCleanupMarkerSuffix = ".wincfapiwrapper-pending-cleanup";
    private const string PendingCleanupContents = "WinCfApiWrapper cleanup v1";

    internal static void Delete(
        string root,
        IMountOperations operations,
        bool registrationFailed = false,
        bool createdByThisAttempt = false
    )
    {
        // Normalize before deriving the parent or sibling recovery marker. A
        // trailing separator would otherwise place the marker inside the root.
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if (!MountPath.IsMount(root))
            throw new IOException($"Not a WinCfApiWrapper folder: {root}");

        FileAttributes rootAttributes;
        try
        {
            rootAttributes = File.GetAttributes(root);
        }
        catch (FileNotFoundException)
        {
            RemoveCompletedCleanupMarker(root);
            return;
        }
        catch (DirectoryNotFoundException)
        {
            RemoveCompletedCleanupMarker(root);
            return;
        }

        if ((rootAttributes & FileAttributes.Directory) == 0)
            throw new IOException($"Mount path is a file, not an owned directory: {root}");
        if ((rootAttributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException($"Mount path is a reparse point: {root}");

        string? parent = Path.GetDirectoryName(Path.GetFullPath(root));
        if (parent is null || (File.GetAttributes(parent) & FileAttributes.ReparsePoint) != 0)
            throw new IOException($"Mount parent is a reparse point or missing: {parent}");

        // Reject nested directory junctions/symlinks before any native unregister
        // or recursive deletion. CFAPI reparse-point *files* are permitted.
        ValidateDirectoryTree(root);

        bool registered = operations.IsRegistered(root);
        if (registered)
        {
            if (registrationFailed)
            {
                throw new IOException(
                    $"Registration appeared after this mount's registration failed at {root}; folder retained."
                );
            }

            if (!operations.IsOwnedSyncRoot(root))
                throw new IOException($"Sync root belongs to another provider: {root}");
        }
        else if (!createdByThisAttempt && !HasPendingCleanupMarker(root))
        {
            // A fingerprinted name is not proof this folder belongs to us.
            throw new IOException($"Unregistered mount has no cleanup ownership evidence: {root}");
        }

        // Recheck after the native ownership query, before writing even a
        // temporary marker; a foreign junction must not receive that write.
        FileAttributes beforeMarker = File.GetAttributes(root);
        if (
            (beforeMarker & (FileAttributes.Directory | FileAttributes.ReparsePoint))
                != FileAttributes.Directory
        )
            throw new IOException($"Mount path changed during ownership validation: {root}");

        // Leave evidence when native unregister succeeds but filesystem deletion
        // fails. Only cleanup pays this I/O cost, never normal mounting.
        WritePendingCleanupMarker(root);
        if (registered)
            operations.Unregister(root);

        // Recheck the root just before recursion to fail closed on ordinary
        // replacements. This is not an atomic defense against hostile path swaps.
        FileAttributes currentAttributes = File.GetAttributes(root);
        if (
            (currentAttributes & (FileAttributes.Directory | FileAttributes.ReparsePoint))
                != FileAttributes.Directory
        )
            throw new IOException($"Mount path changed during cleanup: {root}");

        Directory.Delete(root, recursive: true);
        RemoveCompletedCleanupMarker(root);
    }

    private static void ValidateDirectoryTree(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count != 0)
        {
            string parent = pending.Pop();
            foreach (string child in Directory.EnumerateDirectories(parent))
            {
                FileAttributes attributes = File.GetAttributes(child);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException($"Mount contains a directory reparse point: {child}");
                pending.Push(child);
            }
        }
    }

    // Sidecar lies *outside* the directory being recursively deleted:
    // a failed Directory.Delete may already have removed some child files.
    private static string PendingMarkerPath(string root) =>
        root + PendingCleanupMarkerSuffix;

    private static bool HasPendingCleanupMarker(string root)
    {
        string file = PendingMarkerPath(root);
        try
        {
            FileAttributes attributes = File.GetAttributes(file);
            return (attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == 0
                && File.ReadAllText(file) == PendingCleanupContents;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
    }

    private static void WritePendingCleanupMarker(string root)
    {
        string file = PendingMarkerPath(root);
        try
        {
            FileAttributes attributes = File.GetAttributes(file);
            if (
                (attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0
                || File.ReadAllText(file) != PendingCleanupContents
            )
                throw new IOException($"Unrecognized cleanup marker file: {file}");

            return; // An earlier cleanup attempt already supplied valid evidence.
        }
        catch (FileNotFoundException)
        {
            // Create a new sidecar in the private mount parent, never in the root.
        }

        File.WriteAllText(file, PendingCleanupContents);
    }

    private static void RemoveCompletedCleanupMarker(string root)
    {
        // Do not delete unrelated contents even if they happen to use our suffix.
        if (HasPendingCleanupMarker(root))
            File.Delete(PendingMarkerPath(root));
    }
}
