using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;
using Windows.Win32;
using Windows.Win32.Storage.CloudFilters;

namespace WinCfApiWrapper.Interop;

internal static partial class CloudApi
{
    private const string MinimumVersionMessage =
        "Cloud Files API requires Windows 10 version 1709 or later.";

    private const uint FileReadAttributes = 0x00000080;
    private const uint ShareReadWriteDelete = 0x00000007;
    private const uint OpenExisting = 3;

    [SupportedOSPlatformGuard("windows10.0.16299")]
    private static bool IsSupported => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 16299);

    internal static SafeFileHandle OpenForAttributes(string path)
    {
        SafeFileHandle handle = CreateFileForAttributes(
            path,
            FileReadAttributes,
            ShareReadWriteDelete,
            nint.Zero,
            OpenExisting,
            0,
            nint.Zero
        );
        if (!handle.IsInvalid)
            return handle;

        int error = Marshal.GetLastPInvokeError();
        handle.Dispose();
        throw new Win32Exception(error, $"Cannot open placeholder metadata: {path}");
    }

    internal static unsafe bool IsSyncRootRegistered(string root)
    {
        if (!IsSupported)
            throw new PlatformNotSupportedException(MinimumVersionMessage);

        long basicInfo;
        int hr = PInvoke.CfGetSyncRootInfoByPath(
            root,
            CF_SYNC_ROOT_INFO_CLASS.CF_SYNC_ROOT_INFO_BASIC,
            &basicInfo,
            sizeof(long),
            out _
        );
        switch (hr)
        {
            case >= 0:

                return true;
            case unchecked((int)0x80070178) or unchecked((int)0x80070186):

                return false;
            default:
                Check(hr, "Inspect sync root registration");

                return false;
        }
    }

    internal static unsafe long ConnectSyncRoot(string root, CF_CALLBACK_REGISTRATION[] callbacks)
    {
        if (!IsSupported)
            throw new PlatformNotSupportedException(MinimumVersionMessage);

        Check(
            PInvoke.CfConnectSyncRoot(
                root,
                callbacks,
                null,
                CF_CONNECT_FLAGS.CF_CONNECT_FLAG_NONE,
                out CF_CONNECTION_KEY key
            ),
            "Connect sync root"
        );

        return key;
    }

    internal static void DisconnectSyncRoot(long key)
    {
        if (!IsSupported)
            throw new PlatformNotSupportedException(MinimumVersionMessage);

        Check(PInvoke.CfDisconnectSyncRoot((CF_CONNECTION_KEY)key), "Disconnect sync root");
    }

    internal static int CreatePlaceholders(
        string parent,
        CF_PLACEHOLDER_CREATE_INFO[] items,
        out uint processed
    )
    {
        if (!IsSupported)
            throw new PlatformNotSupportedException(MinimumVersionMessage);

        return PInvoke.CfCreatePlaceholders(
            parent,
            items,
            CF_CREATE_FLAGS.CF_CREATE_FLAG_STOP_ON_ERROR,
            out processed
        );
    }

    internal static int GetPlaceholderInfo(
        SafeFileHandle handle,
        Span<byte> buffer,
        out uint returnedLength
    )
    {
        if (!IsSupported)
            throw new PlatformNotSupportedException(MinimumVersionMessage);

        return PInvoke.CfGetPlaceholderInfo(
            handle,
            CF_PLACEHOLDER_INFO_CLASS.CF_PLACEHOLDER_INFO_BASIC,
            buffer,
            out returnedLength
        );
    }

    internal static void Execute(in CF_OPERATION_INFO info, ref CF_OPERATION_PARAMETERS parameters)
    {
        if (!IsSupported)
            throw new PlatformNotSupportedException(MinimumVersionMessage);

        Check(PInvoke.CfExecute(in info, ref parameters), "Transfer data");
    }

    internal static void Check(int hr, string operation)
    {
        if (hr < 0)
            throw new COMException($"{operation} failed (HRESULT 0x{hr:X8}).", hr);
    }

    [SupportedOSPlatformGuard("windows10.0.16299")]
    [LibraryImport(
        "kernel32.dll",
        EntryPoint = "CreateFileW",
        StringMarshalling = StringMarshalling.Utf16,
        SetLastError = true
    )]
    private static partial SafeFileHandle CreateFileForAttributes(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile
    );
}
