using Microsoft.Win32.SafeHandles;
using WinCfApiWrapper.Mounting;
using Xunit;

namespace WinCfApiWrapper.Tests.Mounting;

public sealed class NativeMountOperationsTests
{
    [Fact]
    public async Task HydrateAsync_ReadsToEndAndClosesTheFileHandle()
    {
        CancellationToken testCancellationToken = TestContext.Current.CancellationToken;

        string path = Path.GetTempFileName();
        try
        {
            await File.WriteAllBytesAsync(path, new byte[128 * 1024], testCancellationToken);
            SafeFileHandle handle = File.OpenHandle(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                FileOptions.Asynchronous
            );

            await new NativeMountOperations().HydrateAsync(handle, testCancellationToken);

            Assert.True(handle.IsClosed);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task HydrateAsync_CanceledReadStillClosesTheFileHandle()
    {
        string path = Path.GetTempFileName();
        try
        {
            await File.WriteAllBytesAsync(
                path,
                new byte[128 * 1024],
                TestContext.Current.CancellationToken
            );
            SafeFileHandle handle = File.OpenHandle(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                FileOptions.Asynchronous
            );
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                new NativeMountOperations().HydrateAsync(handle, cancellation.Token)
            );

            Assert.True(handle.IsClosed);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
