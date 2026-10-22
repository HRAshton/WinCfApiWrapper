using FsCheck;
using FsCheck.Xunit;
using WinCfApiWrapper.Containers;
using WinCfApiWrapper.Hydration;
using Xunit;

namespace WinCfApiWrapper.Tests.Fuzzing;

public sealed class InputBoundaryProperties
{
    [Property(MaxTest = 1000)]
    public void AcceptedPaths_StayInsideRootAndNormalizeIdempotently(NonNull<string> input)
    {
        string normalized;
        try
        {
            normalized = ContainerPath.Normalize(input.Get);
        }
        catch (InvalidDataException)
        {
            return;
        }

        Assert.Equal(normalized, ContainerPath.Normalize(normalized));
        Assert.Equal(normalized, ContainerPath.Normalize(normalized.Replace('\\', '/')));
        string root = Path.Combine(Path.GetTempPath(), "WinCfApiWrapper.FuzzRoot");
        string resolved = Path.GetFullPath(Path.Combine(root, normalized));
        bool contained;
        if (normalized.Length == 0)
        {
            contained = string.Equals(root, resolved, StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            contained = resolved.StartsWith(
                root + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase
            );
        }

        Assert.True(contained, $"Accepted path escaped its root: {input.Get}");
    }

    [Property(MaxTest = 1000)]
    public void TraversalPaths_AreRejected(NonNull<string> suffix)
    {
        Assert.Throws<InvalidDataException>(() => ContainerPath.Normalize("../" + suffix.Get));
        Assert.Throws<InvalidDataException>(() =>
            ContainerPath.Normalize("folder\\..\\" + suffix.Get)
        );
    }

    [Property(MaxTest = 1000)]
    public void ValidRequests_ExpandToAlignedRangeWithinFile(
        long fileSeed,
        long offsetSeed,
        long lengthSeed
    )
    {
        long fileLength = (long)((ulong)fileSeed % long.MaxValue) + 1;
        long offset = (long)((ulong)offsetSeed % (ulong)fileLength);
        long length = (long)((ulong)lengthSeed % (ulong)(fileLength - offset)) + 1;

        TransferRange range = TransferRange.ForRequest(offset, length, fileLength);

        TransferRange.ValidateExact(range.Offset, range.Length, fileLength);
        Assert.InRange(range.Offset, 0, offset);
        Assert.InRange(range.Length, 1, fileLength - range.Offset);
        Assert.True(range.Offset + range.Length >= offset + length);
        Assert.Equal(range, TransferRange.ForRequest(range.Offset, range.Length, fileLength));
    }

    [Property(MaxTest = 1000)]
    public void InvalidRequests_AreRejected(long offset, long length, long fileLength)
    {
        // Use decimal arithmetic to check validity without overflowing.
        bool valid =
            fileLength > 0 && offset >= 0 && length > 0 && (decimal)offset + length <= fileLength;
        if (!valid)
        {
            Assert.Throws<InvalidDataException>(() =>
                TransferRange.ForRequest(offset, length, fileLength)
            );
            Assert.Throws<InvalidDataException>(() =>
                TransferRange.ValidateExact(offset, length, fileLength)
            );
        }
    }
}
