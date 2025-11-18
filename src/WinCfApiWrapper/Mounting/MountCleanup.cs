namespace WinCfApiWrapper.Mounting;

internal static class MountCleanup
{
    internal static void Delete(
        string root,
        IMountOperations operations,
        bool registrationFailed = false
    )
    {
        if (!MountPath.IsMount(root))
            throw new IOException($"Not a WinCfApiWrapper folder: {root}");

        if (File.Exists(root))
            throw new IOException($"Mount path is a file, not an owned directory: {root}");

        if (Directory.Exists(root))
        {
            bool registered = operations.IsRegistered(root);
            switch (registered)
            {
                case false when (File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0:
                    throw new IOException($"Unregistered mount path is a reparse point: {root}");
                case true:
                {
                    if (registrationFailed)
                    {
                        throw new IOException(
                            $"Registration appeared after this mount's registration failed at {root}; folder retained."
                        );
                    }

                    operations.Unregister(root);
                    break;
                }
            }

            // The entire directory belongs to this mount. A failed deletion can be retried
            // using its fingerprinted name.
            Directory.Delete(root, true);
        }
    }
}
