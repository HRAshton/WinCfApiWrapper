using System.Runtime.InteropServices;
using WinCfApiWrapper.Interop;
using Xunit;

namespace WinCfApiWrapper.Tests.Interop;

public sealed class CloudApiTests
{
    [Fact]
    public void Check_SuccessfulNativeResultContinuesTheOperation()
    {
        CloudApi.Check(0, "test operation");
    }

    [Fact]
    public void Check_FailedNativeResultPreservesTheHResult()
    {
        const int hresult = unchecked((int)0x80070005);

        COMException error = Assert.Throws<COMException>(() =>
            CloudApi.Check(hresult, "test operation")
        );

        Assert.Equal(hresult, error.HResult);
        Assert.Contains("test operation", error.Message);
    }
}
