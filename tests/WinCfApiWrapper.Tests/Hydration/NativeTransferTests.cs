using System.Runtime.InteropServices;
using WinCfApiWrapper.Hydration;
using Windows.Win32.Storage.CloudFilters;
using Xunit;

namespace WinCfApiWrapper.Tests.Hydration;

public sealed class NativeTransferTests
{
    [Fact]
    public void TransferData_GeneratedLayout_HasCfApiParameterSize()
    {
        int transferOffset = Marshal
            .OffsetOf<CF_OPERATION_PARAMETERS>(nameof(CF_OPERATION_PARAMETERS.Anonymous))
            .ToInt32();
        int transferSize =
            Marshal.SizeOf<CF_OPERATION_PARAMETERS._Anonymous_e__Union._TransferData_e__Struct>();
        Assert.Equal(40, transferOffset + transferSize);
        Assert.Equal(48, Marshal.SizeOf<CF_OPERATION_INFO>());
    }

    [Theory]
    [InlineData(0, 1, 8192, 0, 4096)]
    [InlineData(1, 1, 8192, 0, 4096)]
    [InlineData(4095, 2, 8192, 0, 8192)]
    [InlineData(4096, 1, 8192, 4096, 4096)]
    [InlineData(4097, 1, 8192, 4096, 4096)]
    [InlineData(5000, 10, 10000, 4096, 4096)]
    public void TransferFailure_UnalignedRequiredRange_UsesValidCfApiRange(
        long offset,
        long length,
        long fileLength,
        long expectedOffset,
        long expectedLength
    )
    {
        var calls = new List<TransferCall>();
        var transfer = new NativeTransfer((_, parameters) => calls.Add(parameters));
        FetchRequest request = Request(offset, length, fileLength);

        Assert.True(transfer.Fail(request));
        TransferCall call = Assert.Single(calls);
        Assert.Equal(expectedOffset, call.Offset);
        Assert.Equal(expectedLength, call.Length);
        Assert.Equal(nint.Zero, call.Buffer);
        Assert.Equal(NativeTransfer.StatusCloudFileUnsuccessful, call.CompletionStatus);
    }

    [Theory]
    [InlineData(0, 1, 1, 0, 1)]
    [InlineData(4095, 1, 4096, 0, 4096)]
    [InlineData(4096, 1, 4097, 4096, 1)]
    [InlineData(4096, 5, 4101, 4096, 5)]
    public void TransferFailure_EndsAtEof_AllowsShortFinalLength(
        long offset,
        long length,
        long fileLength,
        long expectedOffset,
        long expectedLength
    )
    {
        TransferCall? observed = null;
        var transfer = new NativeTransfer((_, parameters) => observed = parameters);
        Assert.True(transfer.Fail(Request(offset, length, fileLength)));
        Assert.Equal(expectedOffset, observed!.Value.Offset);
        Assert.Equal(expectedLength, observed.Value.Length);
    }

    [Theory]
    [InlineData(-1, 1, 100)]
    [InlineData(0, 0, 100)]
    [InlineData(100, 1, 100)]
    [InlineData(0, 101, 100)]
    [InlineData(long.MaxValue - 1, 10, long.MaxValue)]
    [InlineData(0, long.MaxValue, 0)]
    public void TransferFailure_InvalidOrOverflowingRange_DoesNotInvokeNative(
        long offset,
        long length,
        long fileLength
    )
    {
        int calls = 0;
        var transfer = new NativeTransfer((_, _) => calls++);
        Assert.Throws<InvalidDataException>(() =>
            transfer.Fail(Request(offset, length, fileLength))
        );
        Assert.Equal(0, calls);
    }

    [Fact]
    public void TransferFailure_AfterSuccessfulChunk_FailsOnlyRemainingRange()
    {
        var calls = new List<TransferCall>();
        var transfer = new NativeTransfer((_, parameters) => calls.Add(parameters));
        FetchRequest request = Request(1, 131071, 131072);
        transfer.Complete(
            request,
            new byte[NativeTransfer.BufferSize],
            NativeTransfer.BufferSize,
            0
        );
        Assert.True(transfer.Fail(request));
        Assert.Equal(2, calls.Count);
        Assert.Equal(0, calls[0].Offset);
        Assert.Equal(65536, calls[0].Length);
        Assert.Equal(65536, calls[1].Offset);
        Assert.Equal(65536, calls[1].Length);
    }

    [Fact]
    public void TransferComplete_BufferShorterThanRange_RejectsBeforeNativeCall()
    {
        int calls = 0;
        var transfer = new NativeTransfer((_, _) => calls++);
        using FetchRequest request = Request(0, 4096, 4096);

        Assert.Throws<InvalidDataException>(() =>
            transfer.Complete(request, new byte[4095], 4096, 0)
        );
        Assert.Equal(0, calls);
    }

    [Fact]
    public void TransferComplete_SuccessfulRangeTransfersPinnedBytesOnce()
    {
        var calls = new List<TransferCall>();
        var transfer = new NativeTransfer(
            (_, parameters) =>
            {
                byte[] transferred = new byte[4];
                Marshal.Copy(parameters.Buffer, transferred, 0, transferred.Length);
                Assert.Equal(new byte[] { 1, 2, 3, 4 }, transferred);
                calls.Add(parameters);
            }
        );
        using FetchRequest request = Request(0, 4, 4);
        byte[] buffer = [1, 2, 3, 4];

        Assert.True(transfer.Complete(request, buffer, buffer.Length, 0));
        Assert.False(transfer.Complete(request, buffer, buffer.Length, 0));

        TransferCall call = Assert.Single(calls);
        Assert.NotEqual(nint.Zero, call.Buffer);
        Assert.Equal(0, call.Offset);
        Assert.Equal(4, call.Length);
        Assert.Equal(0, call.CompletionStatus);
        Assert.Empty(request.Pending());
    }

    [Fact]
    public void TransferComplete_NativeFailureLeavesRangePendingAndMarksCompletionUnknown()
    {
        var transfer = new NativeTransfer((_, _) => throw new IOException("native failure"));
        using FetchRequest request = Request(0, 4, 4);

        Assert.Throws<IOException>(() => transfer.Complete(request, [1, 2, 3, 4], 4, 0));

        Assert.True(request.NativeCompletionFailed);
        Assert.Equal(new TransferRange(0, 4), Assert.Single(request.Pending()));
    }

    [Fact]
    public void TransferFailure_WhenRequestWasCanceledAndHasNoPendingRanges_DoesNotSubmitAgain()
    {
        int calls = 0;
        var transfer = new NativeTransfer((_, _) => calls++);
        using FetchRequest request = Request(0, 4096, 4096);
        request.Cancel(0, 4096);

        Assert.False(transfer.Fail(request));
        Assert.Equal(0, calls);
    }

    [Fact]
    public void TransferComplete_UnalignedRange_RejectsBeforeNativeCall()
    {
        int calls = 0;
        var transfer = new NativeTransfer((_, _) => calls++);
        using FetchRequest request = Request(0, 8192, 8192);

        Assert.Throws<InvalidDataException>(() =>
            transfer.Complete(request, new byte[4096], 4095, 0)
        );
        Assert.Equal(0, calls);
        Assert.Equal(new TransferRange(0, 8192), Assert.Single(request.Pending()));
    }

    private static FetchRequest Request(long offset, long length, long fileLength)
    {
        return new FetchRequest(
            new CF_CALLBACK_INFO { FileSize = fileLength },
            offset,
            length,
            "file.txt"
        );
    }
}
