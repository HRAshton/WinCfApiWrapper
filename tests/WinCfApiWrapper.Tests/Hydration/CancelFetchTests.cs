using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using WinCfApiWrapper.Containers;
using WinCfApiWrapper.Hydration;
using WinCfApiWrapper.Public;
using Windows.Win32.Storage.CloudFilters;
using Xunit;

namespace WinCfApiWrapper.Tests.Hydration;

public sealed class CancelFetchTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "WinCfApiWrapper.Cancel",
        Guid.NewGuid().ToString("N")
    );

    private readonly string _source;

    public CancelFetchTests()
    {
        Directory.CreateDirectory(_directory);
        _source = Path.Combine(_directory, "source.zip");
        using ZipArchive archive = ZipFile.Open(_source, ZipArchiveMode.Create);
        using Stream output = archive
            .CreateEntry("file.txt", CompressionLevel.NoCompression)
            .Open();
        output.Write(new byte[8192]);
    }

    public void Dispose()
    {
        Directory.Delete(_directory, true);
    }

    [Fact]
    public void CancelFetch_NativeInteropLayout_MatchesExpectedOffsetsAndSizes()
    {
        int cancelOffset =
            Marshal
                .OffsetOf<CF_CALLBACK_PARAMETERS>(nameof(CF_CALLBACK_PARAMETERS.Anonymous))
                .ToInt32()
            + Marshal
                .OffsetOf<CF_CALLBACK_PARAMETERS._Anonymous_e__Union._Cancel_e__Struct>(
                    nameof(CF_CALLBACK_PARAMETERS._Anonymous_e__Union._Cancel_e__Struct.Anonymous)
                )
                .ToInt32();
        Assert.Equal(16, cancelOffset);
        Assert.Equal(
            0,
            Marshal
                .OffsetOf<CF_CALLBACK_PARAMETERS._Anonymous_e__Union._Cancel_e__Struct._Anonymous_e__Union._FetchData_e__Struct>(
                    "FileOffset"
                )
                .ToInt32()
        );
        Assert.Equal(
            8,
            Marshal
                .OffsetOf<CF_CALLBACK_PARAMETERS._Anonymous_e__Union._Cancel_e__Struct._Anonymous_e__Union._FetchData_e__Struct>(
                    "Length"
                )
                .ToInt32()
        );
    }

    [Fact]
    public void CancelFetch_PartialRange_PreservesNeededRanges()
    {
        FetchRequest request = Request(1);
        request.Cancel(0, 4096);
        TransferRange remaining = Assert.Single(request.Pending());
        Assert.Equal(4096, remaining.Offset);
        Assert.Equal(4096, remaining.Length);
        Assert.False(request.IsCanceled);
        Assert.False(request.IsPending(0, 4096));
        Assert.True(request.IsPending(4096, 4096));
    }

    [Fact]
    public void CancelFetch_UnalignedRange_RetainsPartialBlocksAndCompletesAlignedBytes()
    {
        using FetchRequest request = Request(1);
        request.Cancel(1, 4095);
        var transfers = new List<TransferCall>();
        var transfer = new NativeTransfer((_, parameters) => transfers.Add(parameters));
        Assert.Equal(new TransferRange(0, 8192), Assert.Single(request.Pending()));
        Assert.True(transfer.Complete(request, new byte[8192], 8192, 0));
        Assert.Equal(0, Assert.Single(transfers).Offset);
        Assert.False(transfer.Fail(request));
    }

    [Fact]
    public void CancelFetch_MiddleBlock_SplitsPendingTransferAroundCanceledRange()
    {
        using var request = new FetchRequest(
            new CF_CALLBACK_INFO { FileSize = 12288 },
            0,
            12288,
            "file.txt"
        );

        request.Cancel(4096, 4096);

        Assert.Equal(
            [new TransferRange(0, 4096), new TransferRange(8192, 4096)],
            request.Pending()
        );
    }

    [Theory]
    [InlineData(-1, 4096)]
    [InlineData(0, 0)]
    [InlineData(long.MaxValue, 1)]
    public void CancelFetch_InvalidRange_LeavesRequestPending(long offset, long length)
    {
        using FetchRequest request = Request(1);

        Assert.Throws<InvalidDataException>(() => request.Cancel(offset, length));
        Assert.Equal(new TransferRange(0, 8192), Assert.Single(request.Pending()));
    }

    [Fact]
    public void CancelFetch_AfterComplete_IsIdempotent()
    {
        FetchRequest request = Request(1);
        var transfer = new NativeTransfer((_, _) => { });
        Assert.True(transfer.Complete(request, new byte[8192], 8192, 0));
        request.Cancel(0, 8192);
        Assert.False(transfer.Fail(request));
    }

    [Fact]
    public async Task CancelFetch_RacingWithComplete_AtMostOneTerminalTransition()
    {
        FetchRequest request = Request(1);
        int calls = 0;
        var transfer = new NativeTransfer((_, _) => Interlocked.Increment(ref calls));
        await Task.WhenAll(
            Task.Run(() => request.Cancel(0, 8192)),
            Task.Run(() => transfer.Complete(request, new byte[8192], 8192, 0))
        );
        Assert.InRange(calls, 0, 1);
        Assert.False(transfer.Fail(request));
    }

    [Fact]
    public async Task CancelFetch_QueuedRequest_SkipsTransportAndReleasesState()
    {
        (ContainerCatalog catalog, IContentContainer container) = TestZipContainer.Create(_source);
        var stream = new GatedReadStream(new byte[8192]);
        int opened = 0;
        var transferred = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var coordinator = new FetchCoordinator(
            catalog,
            container,
            1,
            2,
            (_, _) => { },
            new NativeTransfer(
                (_, parameters) =>
                {
                    if (parameters.CompletionStatus == 0)
                        transferred.TrySetResult();
                }
            ),
            _ =>
            {
                opened++;

                return stream;
            }
        );
        await using (coordinator)
        {
            Invoke(coordinator, 1, false);
            await stream.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Invoke(coordinator, 2, false);
            Invoke(coordinator, 2, true);
            stream.Released.TrySetResult();
            await transferred.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }

        Assert.Equal(1, opened);
        Assert.Equal(0, coordinator.PendingRequestCount);
        Assert.Equal(0, coordinator.Statistics.FailedFetches);
    }

    [Fact]
    public async Task CancelFetch_RunningRequest_PropagatesCancellationToRead()
    {
        (ContainerCatalog catalog, IContentContainer container) = TestZipContainer.Create(_source);
        var stream = new GatedReadStream(new byte[8192]);
        var coordinator = new FetchCoordinator(
            catalog,
            container,
            1,
            2,
            (_, _) => { },
            new NativeTransfer((_, _) => { }),
            _ => stream
        );
        try
        {
            Invoke(coordinator, 1, false);
            await stream.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Invoke(coordinator, 1, true);
        }
        finally
        {
            await coordinator.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        }

        Assert.True(stream.Canceled);
        Assert.Equal(0, coordinator.Statistics.FailedFetches);
    }

    [Fact]
    public async Task CancelFetch_ActivePartialRange_TransfersRemainingBytes()
    {
        (ContainerCatalog catalog, IContentContainer container) = TestZipContainer.Create(_source);
        var stream = new GatedReadStream(new byte[8192]);
        var transferred = new TaskCompletionSource<TransferCall>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var coordinator = new FetchCoordinator(
            catalog,
            container,
            1,
            2,
            (_, _) => { },
            new NativeTransfer((_, parameters) => transferred.TrySetResult(parameters)),
            _ => stream
        );
        await using (coordinator)
        {
            Invoke(coordinator, 1, false);
            await stream.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            InvokeCancel(coordinator, 1, 0, 4096);
            stream.Released.TrySetResult();
            TransferCall result = await transferred.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(4096, result.Offset);
            Assert.Equal(4096, result.Length);
            Assert.Equal(0, result.CompletionStatus);
        }

        Assert.Equal(0, coordinator.Statistics.FailedFetches);
    }

    [Fact]
    public async Task CancelFetch_DifferentRequestSamePath_DoesNotCancelOtherRequest()
    {
        (ContainerCatalog catalog, IContentContainer container) = TestZipContainer.Create(_source);
        int successes = 0;
        var transferred = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var coordinator = new FetchCoordinator(
            catalog,
            container,
            1,
            2,
            (_, _) => { },
            new NativeTransfer(
                (_, parameters) =>
                {
                    if (parameters.CompletionStatus == 0)
                    {
                        Interlocked.Increment(ref successes);
                        transferred.TrySetResult();
                    }
                }
            ),
            _ => new MemoryStream(new byte[8192])
        );
        await using (coordinator)
        {
            Invoke(coordinator, 1, false);
            Invoke(coordinator, 2, false);
            Invoke(coordinator, 1, true);
            await transferred.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }

        Assert.True(successes >= 1);
    }

    [Fact]
    public async Task ConcurrentHydration_QueueSaturation_RespectsConfiguredLimits()
    {
        (ContainerCatalog catalog, IContentContainer container) = TestZipContainer.Create(_source);
        GatedReadStream[] streams = [new(new byte[8192]), new(new byte[8192])];
        int opened = 0;
        var coordinator = new FetchCoordinator(
            catalog,
            container,
            2,
            4,
            (_, _) => { },
            new NativeTransfer((_, _) => { }),
            _ => streams[Interlocked.Increment(ref opened) - 1]
        );
        try
        {
            Invoke(coordinator, 1, false);
            Invoke(coordinator, 2, false);
            await Task.WhenAll(streams.Select(stream => stream.Started.Task))
                .WaitAsync(TimeSpan.FromSeconds(5));
            for (int key = 3; key <= 64; key++)
                Invoke(coordinator, key, false);

            Assert.Equal(2, coordinator.Statistics.ActiveFetches);
            Assert.Equal(6, coordinator.PendingRequestCount);
            Assert.Equal(58, coordinator.Statistics.FailedFetches);
            for (int key = 3; key <= 6; key++)
                Invoke(coordinator, key, true);

            for (int key = 1; key <= 2; key++)
                Invoke(coordinator, key, true);
        }
        finally
        {
            await coordinator.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        }

        Assert.Equal(2, opened);
        Assert.Equal(0, coordinator.PendingRequestCount);
        Assert.Equal(58, coordinator.Statistics.FailedFetches);
    }

    [Fact]
    public async Task CancelFetch_MalformedParameters_DoesNotThrowThroughCallback()
    {
        (ContainerCatalog catalog, IContentContainer container) = TestZipContainer.Create(_source);
        int errors = 0;
        await using var coordinator = new FetchCoordinator(
            catalog,
            container,
            0,
            1,
            (_, _) => errors++
        );
        coordinator.OnCancelFetch(nint.Zero, nint.Zero);
        Assert.Equal(1, errors);
    }

    [Theory]
    [InlineData(23u, 0, 8192)]
    [InlineData(24u, -1, 8192)]
    [InlineData(24u, 0, 0)]
    [InlineData(24u, long.MaxValue, 1)]
    public async Task CancelFetch_InvalidRangeOrSize_ReportsErrorWithoutUnwinding(
        uint parameterSize,
        long offset,
        long length
    )
    {
        (ContainerCatalog catalog, IContentContainer container) = TestZipContainer.Create(_source);
        int errors = 0;
        await using var coordinator = new FetchCoordinator(
            catalog,
            container,
            0,
            1,
            (_, _) => errors++
        );

        InvokeCancel(coordinator, 1, offset, length, parameterSize);

        Assert.Equal(1, errors);
    }

    private static FetchRequest Request(long key)
    {
        return new FetchRequest(
            new CF_CALLBACK_INFO
            {
                ConnectionKey = (CF_CONNECTION_KEY)1,
                TransferKey = 2,
                RequestKey = key,
                FileSize = 8192,
            },
            0,
            8192,
            "file.txt"
        );
    }

    private static unsafe void Invoke(FetchCoordinator coordinator, long key, bool cancel)
    {
        byte[] identity = Encoding.UTF8.GetBytes("file.txt");
        nint identityPointer = Marshal.AllocHGlobal(identity.Length);
        nint infoPointer = Marshal.AllocHGlobal(Marshal.SizeOf<CF_CALLBACK_INFO>());
        nint parametersPointer = Marshal.AllocHGlobal(Marshal.SizeOf<CF_CALLBACK_PARAMETERS>());
        try
        {
            Marshal.Copy(identity, 0, identityPointer, identity.Length);
            Marshal.StructureToPtr(
                new CF_CALLBACK_INFO
                {
                    StructSize = (uint)Marshal.SizeOf<CF_CALLBACK_INFO>(),
                    ConnectionKey = (CF_CONNECTION_KEY)1,
                    TransferKey = 2,
                    RequestKey = key,
                    FileSize = 8192,
                    FileIdentity = (void*)identityPointer,
                    FileIdentityLength = (uint)identity.Length,
                },
                infoPointer,
                false
            );
            CF_CALLBACK_PARAMETERS parameters = default;
            if (cancel)
            {
                parameters.ParamSize = 24;
                parameters.Cancel.FetchData.FileOffset = 0;
                parameters.Cancel.FetchData.Length = 8192;
                Marshal.StructureToPtr(parameters, parametersPointer, false);
                coordinator.OnCancelFetch(infoPointer, parametersPointer);
            }
            else
            {
                parameters.ParamSize = (uint)(
                    sizeof(uint)
                    + sizeof(CF_CALLBACK_PARAMETERS._Anonymous_e__Union._FetchData_e__Struct)
                );
                parameters.FetchData.RequiredFileOffset = 0;
                parameters.FetchData.RequiredLength = 8192;
                Marshal.StructureToPtr(parameters, parametersPointer, false);
                coordinator.OnFetch(infoPointer, parametersPointer);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(parametersPointer);
            Marshal.FreeHGlobal(infoPointer);
            Marshal.FreeHGlobal(identityPointer);
        }
    }

    private static void InvokeCancel(
        FetchCoordinator coordinator,
        long key,
        long offset,
        long length,
        uint parameterSize = 24
    )
    {
        nint infoPointer = Marshal.AllocHGlobal(Marshal.SizeOf<CF_CALLBACK_INFO>());
        nint parametersPointer = Marshal.AllocHGlobal(Marshal.SizeOf<CF_CALLBACK_PARAMETERS>());
        try
        {
            Marshal.StructureToPtr(
                new CF_CALLBACK_INFO
                {
                    ConnectionKey = (CF_CONNECTION_KEY)1,
                    TransferKey = 2,
                    RequestKey = key,
                },
                infoPointer,
                false
            );
            var parameters = new CF_CALLBACK_PARAMETERS { ParamSize = parameterSize };
            parameters.Cancel.FetchData.FileOffset = offset;
            parameters.Cancel.FetchData.Length = length;
            Marshal.StructureToPtr(parameters, parametersPointer, false);
            coordinator.OnCancelFetch(infoPointer, parametersPointer);
        }
        finally
        {
            Marshal.FreeHGlobal(parametersPointer);
            Marshal.FreeHGlobal(infoPointer);
        }
    }
}
