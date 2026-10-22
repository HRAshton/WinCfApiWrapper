namespace WinCfApiWrapper.Mounting;

internal static class MountPath
{
    // Legacy eight-hex trusted IDs retain their original mount names.
    // Old automatic names remain recognizable by explicit cleanup.
    private const string LegacyMarker = ".rzm-";
    private const string AutomaticV2Marker = ".rzm-v2-";
    private const int AutomaticV2HexDigits = 32;

    internal static string For(string basePath, string fingerprint)
    {
        ArgumentNullException.ThrowIfNull(fingerprint);
        bool isHex = fingerprint.AsSpan().IndexOfAnyExcept("0123456789ABCDEFabcdef") < 0;
        string suffix = fingerprint.Length switch
        {
            8 when isHex => LegacyMarker + fingerprint.ToLowerInvariant(),
            64 when isHex =>
                AutomaticV2Marker + fingerprint[..AutomaticV2HexDigits].ToLowerInvariant(),
            _ => throw new ArgumentException(
                "Expected an eight-digit trusted ID or 64-digit automatic SHA-256 fingerprint.",
                nameof(fingerprint)
            ),
        };
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(basePath)) + suffix;
    }

    internal static bool IsMount(string path)
    {
        string name = Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
        return HasHexSuffix(name, LegacyMarker, 8)
            || HasHexSuffix(name, AutomaticV2Marker, AutomaticV2HexDigits);
    }

    private static bool HasHexSuffix(string name, string marker, int hexDigits)
    {
        int position = name.LastIndexOf(marker, StringComparison.OrdinalIgnoreCase);
        return position > 0
            && name.Length - position - marker.Length == hexDigits
            && name.AsSpan(position + marker.Length).IndexOfAnyExcept(
                "0123456789ABCDEFabcdef"
            ) < 0;
    }
}
