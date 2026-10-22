using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using WinCfApiWrapper.Public;
using Windows.Win32;
using Windows.Win32.Storage.CloudFilters;
using Windows.Win32.Storage.FileSystem;
using static WinCfApiWrapper.Interop.CloudApi;

namespace WinCfApiWrapper.Mounting;

internal sealed class NativeMountOperations : IMountOperations
{
    // Stable CFAPI identity used when this provider registers a sync root.
    private static readonly Guid ProviderId = new("82135F09-322B-4B32-941F-20AE1B9C2A47");

    public unsafe void Register(string root)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 16299))
        {
            throw new PlatformNotSupportedException(
                "Cloud Files API requires Windows 10 version 1709 or later."
            );
        }

        const string providerName = "WinCfApiWrapper";
        const string providerVersion = "1.0";
        fixed (
            char* name = providerName,
                version = providerVersion
        )
        {
            var registration = new CF_SYNC_REGISTRATION
            {
                StructSize = (uint)sizeof(CF_SYNC_REGISTRATION),
                ProviderName = name,
                ProviderVersion = version,
                ProviderId = ProviderId,
            };
            var policies = new CF_SYNC_POLICIES
            {
                StructSize = (uint)sizeof(CF_SYNC_POLICIES),
                Hydration = new CF_HYDRATION_POLICY
                {
                    Primary = CF_HYDRATION_POLICY_PRIMARY.CF_HYDRATION_POLICY_FULL,
                },
                Population = new CF_POPULATION_POLICY
                {
                    Primary = CF_POPULATION_POLICY_PRIMARY.CF_POPULATION_POLICY_ALWAYS_FULL,
                },
                HardLink = CF_HARDLINK_POLICY.CF_HARDLINK_POLICY_ALLOWED,
            };

            // No UPDATE flag: an existing registration is never claimed by this attempt.
            Check(
                PInvoke.CfRegisterSyncRoot(
                    root,
                    in registration,
                    in policies,
                    CF_REGISTER_FLAGS.CF_REGISTER_FLAG_NONE
                ),
                "Register sync root"
            );
        }
    }

    public void Unregister(string root)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 16299))
        {
            throw new PlatformNotSupportedException(
                "Cloud Files API requires Windows 10 version 1709 or later."
            );
        }

        Check(PInvoke.CfUnregisterSyncRoot(root), "Unregister sync root");
    }

    public long Connect(string root, CF_CALLBACK_REGISTRATION[] callbacks)
    {
        return ConnectSyncRoot(root, callbacks);
    }

    public void Disconnect(long key)
    {
        DisconnectSyncRoot(key);
    }

    public unsafe void CreatePlaceholder(string parent, string fileName, ContainerEntry entry)
    {
        byte[] identity = Encoding.UTF8.GetBytes(entry.RelativePath);
        long time = DateTime.UtcNow.ToFileTimeUtc();
        fixed (byte* identityPointer = identity)
        {
            fixed (char* namePointer = fileName)
            {
                CF_PLACEHOLDER_CREATE_INFO[] items =
                [
                    new()
                    {
                        RelativeFileName = namePointer,
                        FileIdentity = identityPointer,
                        FileIdentityLength = (uint)identity.Length,
                        FsMetadata = new CF_FS_METADATA
                        {
                            BasicInfo = new FILE_BASIC_INFO
                            {
                                CreationTime = time,
                                LastWriteTime = time,
                                LastAccessTime = time,
                                ChangeTime = time,
                                FileAttributes = (uint)FileAttributes.Normal,
                            },
                            FileSize = entry.Length,
                        },
                    },
                ];
                int hr = CreatePlaceholders(parent, items, out uint processed);
                if (hr < 0 || processed != 1 || items[0].Result.Failed)
                {
                    throw new COMException(
                        $"Cannot create placeholder: {entry.RelativePath}",
                        hr < 0 ? hr : items[0].Result
                    );
                }
            }
        }
    }

    public void ValidatePlaceholder(string path, ContainerEntry entry)
    {
        PlaceholderValidator.Validate(path, entry);
    }

    public bool IsRegistered(string root)
    {
        return IsSyncRootRegistered(root);
    }

    // CF_SYNC_ROOT_INFO_PROVIDER is fixed-size: status + 256 UTF-16 name
    // characters + 256 UTF-16 version characters. Never used by fetch/mount.
    [DllImport("CldApi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int CfGetSyncRootInfoByPath(
        string filePath,
        int infoClass,
        nint infoBuffer,
        uint infoBufferLength,
        nint returnedLength
    );

    public unsafe bool IsOwnedSyncRoot(string root)
    {
        const int nameChars = 256;
        const int infoBytes = sizeof(uint) + nameChars * sizeof(char) * 2;
        Span<byte> bytes = stackalloc byte[infoBytes];
        fixed (byte* buffer = bytes)
        {
            int result = CfGetSyncRootInfoByPath(
                root,
                2, // CF_SYNC_ROOT_INFO_PROVIDER
                (nint)buffer,
                infoBytes,
                nint.Zero
            );
            if (result < 0)
                throw new IOException(
                    $"Cannot validate sync root provider: {root}",
                    new COMException("CfGetSyncRootInfoByPath failed.", result)
                );

            ReadOnlySpan<char> name = new((char*)(buffer + sizeof(uint)), nameChars);
            int terminator = name.IndexOf('\0');
            return terminator >= 0
                && name[..terminator].SequenceEqual("WinCfApiWrapper");
        }
    }

    public async Task HydrateAsync(SafeFileHandle handle, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(handle, FileAccess.Read, 64 * 1024, true);
        byte[] buffer = new byte[64 * 1024];
        while (await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false) != 0)
            cancellationToken.ThrowIfCancellationRequested();
    }
}
