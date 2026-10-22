using System.Text;

namespace WinCfApiWrapper.Containers;

internal static class ContainerPath
{
    internal const int MaxIdentityBytes = 4096;

    private static readonly char[] InvalidCharacters = ['<', '>', ':', '"', '|', '?', '*', '\0'];

    private static readonly string[] ReservedNames =
    [
        "CON",
        "PRN",
        "AUX",
        "NUL",
        "CONIN$",
        "CONOUT$",
    ];

    public static string Normalize(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        path = path.Replace('/', '\\');
        if (path.StartsWith('\\') || path.Contains(':'))
            throw new InvalidDataException($"Unsafe absolute path: {path}");

        string[] segments = path.Split('\\');

        // Only a final trailing separator marks an explicit directory.
        if (segments.Length > 0 && segments[^1].Length == 0)
            segments = segments[..^1];

        bool isUnsafe = segments.Any(segment =>
            segment.Length == 0
            || segment is "." or ".."
            || segment.EndsWith('.')
            || segment.EndsWith(' ')
            || segment.Any(ch => ch < 32 || InvalidCharacters.Contains(ch))
            || IsReservedDeviceName(segment)
        );
        if (isUnsafe)
            throw new InvalidDataException($"Unsafe container path: {path}");

        return string.Join('\\', segments);
    }

    // Validate before expanding parents. File identities must fit the CFAPI
    // 4-KiB UTF-8 budget; directory paths must also be addressable by descendants.
    internal static void ValidateIdentityLength(string normalizedPath)
    {
        if (Encoding.UTF8.GetByteCount(normalizedPath) > MaxIdentityBytes)
        {
            throw new InvalidDataException(
                $"File identity exceeds {MaxIdentityBytes} bytes: {normalizedPath}"
            );
        }
    }

    // Every ancestor of an existing directory is already in the set, so stop
    // as soon as a shared parent is found instead of rebuilding its prefixes.
    internal static void AddParentDirectories(
        string normalizedPath,
        bool isDirectory,
        HashSet<string> directories
    )
    {
        int length = isDirectory
            ? normalizedPath.Length
            : normalizedPath.LastIndexOf('\\');
        while (length > 0)
        {
            string parent = normalizedPath[..length];
            if (!directories.Add(parent))
                break;

            length = normalizedPath.LastIndexOf('\\', length - 1);
        }
    }

    private static bool IsReservedDeviceName(string segment)
    {
        string stem = segment.Split('.')[0];
        if (ReservedNames.Contains(stem, StringComparer.OrdinalIgnoreCase))
            return true;

        return stem.Length == 4
            && (
                stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase)
                || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)
            )
            && "123456789¹²³".Contains(stem[3]);
    }
}
