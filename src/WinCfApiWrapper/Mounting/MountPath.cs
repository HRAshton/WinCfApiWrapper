namespace WinCfApiWrapper.Mounting;

internal static class MountPath
{
    private const string Marker = ".rzm-";

    internal static string For(string basePath, string fingerprint)
    {
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(basePath))
            + Marker
            + fingerprint[..8].ToLowerInvariant();
    }

    internal static bool IsMount(string path)
    {
        string name = Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
        int marker = name.LastIndexOf(Marker, StringComparison.OrdinalIgnoreCase);

        return marker > 0
            && name.Length - marker - Marker.Length == 8
            && name.AsSpan(marker + Marker.Length).IndexOfAnyExcept("0123456789ABCDEFabcdef") < 0;
    }
}
