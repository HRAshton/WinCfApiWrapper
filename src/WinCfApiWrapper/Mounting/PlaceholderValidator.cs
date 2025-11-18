using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using WinCfApiWrapper.Interop;
using WinCfApiWrapper.Public;
using Windows.Win32.Storage.CloudFilters;

namespace WinCfApiWrapper.Mounting;

internal static class PlaceholderValidator
{
    public static void Validate(string filePath, ContainerEntry entry)
    {
        if (!File.Exists(filePath))
            throw new IOException($"Existing placeholder is missing: {entry.RelativePath}");

        long size = new FileInfo(filePath).Length;
        if (size != entry.Length)
        {
            throw new IOException(
                $"Existing placeholder has unexpected size: {entry.RelativePath} ({size} != {entry.Length})."
            );
        }

        byte[] expectedIdentity = Encoding.UTF8.GetBytes(entry.RelativePath);
        int identityLengthOffset = Marshal
            .OffsetOf<CF_PLACEHOLDER_BASIC_INFO>(
                nameof(CF_PLACEHOLDER_BASIC_INFO.FileIdentityLength)
            )
            .ToInt32();
        int identityOffset = Marshal
            .OffsetOf<CF_PLACEHOLDER_BASIC_INFO>(nameof(CF_PLACEHOLDER_BASIC_INFO.FileIdentity))
            .ToInt32();
        byte[] buffer = new byte[CF_PLACEHOLDER_BASIC_INFO.SizeOf(expectedIdentity.Length)];

        // Request only FILE_READ_ATTRIBUTES: resume must not trigger hydration.
        using SafeFileHandle handle = CloudApi.OpenForAttributes(filePath);

        int result = CloudApi.GetPlaceholderInfo(handle, buffer, out uint returnedLength);
        if (result < 0)
        {
            throw new IOException(
                $"Existing file is not a readable CFAPI placeholder: {entry.RelativePath}",
                new COMException("CfGetPlaceholderInfo failed", result)
            );
        }

        if (returnedLength < identityOffset || returnedLength > buffer.Length)
            throw new IOException($"Placeholder metadata is incomplete: {entry.RelativePath}");

        uint actualIdentityLength = BinaryPrimitives.ReadUInt32LittleEndian(
            buffer.AsSpan(identityLengthOffset)
        );
        bool identityLengthIsInvalid =
            actualIdentityLength != expectedIdentity.Length
            || (ulong)identityOffset + actualIdentityLength > returnedLength;
        if (identityLengthIsInvalid)
            throw new IOException($"Placeholder identity length differs: {entry.RelativePath}");

        if (!buffer.AsSpan(identityOffset, expectedIdentity.Length).SequenceEqual(expectedIdentity))
            throw new IOException($"Placeholder identity differs: {entry.RelativePath}");
    }
}
