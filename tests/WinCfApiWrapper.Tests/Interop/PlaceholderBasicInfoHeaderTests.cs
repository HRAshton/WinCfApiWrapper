using System.Runtime.InteropServices;
using Windows.Win32.Storage.CloudFilters;
using Xunit;

namespace WinCfApiWrapper.Tests.Interop;

public sealed class PlaceholderBasicInfoHeaderTests
{
    [Fact]
    public void Header_HasNativeCfapiOffsets()
    {
        Assert.Equal(
            24,
            Marshal
                .OffsetOf<CF_PLACEHOLDER_BASIC_INFO>(
                    nameof(CF_PLACEHOLDER_BASIC_INFO.FileIdentityLength)
                )
                .ToInt32()
        );
        Assert.Equal(
            28,
            Marshal
                .OffsetOf<CF_PLACEHOLDER_BASIC_INFO>(nameof(CF_PLACEHOLDER_BASIC_INFO.FileIdentity))
                .ToInt32()
        );
        Assert.Equal(32, Marshal.SizeOf<CF_PLACEHOLDER_BASIC_INFO>());
    }
}
