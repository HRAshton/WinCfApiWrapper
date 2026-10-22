using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using WinCfApiWrapper.Containers;
using WinCfApiWrapper.Hydration;
using WinCfApiWrapper.Public;
using Windows.Win32.Storage.CloudFilters;
using Xunit;

namespace WinCfApiWrapper.Tests.Hydration;

public sealed partial class FetchCoordinatorTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "WinCfApiWrapper.Fetch",
        Guid.NewGuid().ToString("N")
    );

    private readonly string _source;

    public FetchCoordinatorTests()
    {
        Directory.CreateDirectory(_directory);
        _source = Path.Combine(_directory, "source.zip");
        WriteArchive(8);
    }

    public void Dispose()
    {
        Directory.Delete(_directory, true);
    }

    [Fact]
    public async Task Fetch_LargeFile_TransfersAndReportsEveryChunk()
    {
        int length = (NativeTransfer.BufferSize * 3) + 17;
        WriteArchive(length);
        (ContainerCatalog catalog, IContentContainer container) = TestZipContainer.Create(_source);
        var calls = new List<TransferCall>();
        var progress = new List<OperationProgress>();
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var coordinator = new FetchCoordinator(
            catalog,
            container,
            1,
            1,
            (_, _) => { },
            new NativeTransfer((_, call) => calls.Add(call)),
            progress: report =>
            {
                progress.Add(report);
                if (report.Completed == length)
                    finished.TrySetResult();
            }
        );
        await using (coordinator)
        {
            Invoke(coordinator, Encoding.UTF8.GetBytes("file.txt"), length);
            await finished.Task.WaitAsync(
                TimeSpan.FromSeconds(5),
                TestContext.Current.CancellationToken
            );
        }

        Assert.Equal(4, calls.Count);
        Assert.All(calls, call => Assert.InRange(call.Length, 1, NativeTransfer.BufferSize));
        Assert.Equal(length, calls.Sum(call => call.Length));
        Assert.Equal(4, progress.Count);
        Assert.Equal(length, progress[^1].Completed);
        Assert.Equal(0, coordinator.Statistics.FailedFetches);
    }

    [Fact]
    public async Task OnFetch_RepeatedKeyDuringCompletion_TransfersBothRanges()
    {
        WriteArchive(8192);
        (ContainerCatalog catalog, IContentContainer container) = TestZipContainer.Create(_source);
        var calls = new List<TransferCall>();
        var errors = new List<Exception>();
        var completed = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        FetchCoordinator? coordinator = null;
        var transfer = new NativeTransfer(
            (_, call) =>
            {
                calls.Add(call);
                if (calls.Count == 1)
                {
                    // Windows may request more data before the first CfExecute returns.
                    Invoke(coordinator!, Encoding.UTF8.GetBytes("file.txt"), 8192, 4096, 4096);
                }

                if (calls.Count == 2)
                    completed.TrySetResult();
            }
        );
        coordinator = new FetchCoordinator(
            catalog,
            container,
            1,
            2,
            (_, error) => errors.Add(error),
            transfer
        );
        await using (coordinator)
        {
            Invoke(coordinator, Encoding.UTF8.GetBytes("file.txt"), 8192, 0, 4096);
            await completed.Task.WaitAsync(
                TimeSpan.FromSeconds(5),
                TestContext.Current.CancellationToken
            );
        }

        Assert.Empty(errors);
        Assert.Equal(2, calls.Count);
        Assert.All(calls, call => Assert.Equal(0, call.CompletionStatus));
        Assert.Equal(new long[] { 0, 4096 }, calls.Select(call => call.Offset));
        Assert.All(calls, call => Assert.Equal(4096, call.Length));
        Assert.Equal(0, coordinator.Statistics.FailedFetches);
        Assert.Equal(0, coordinator.PendingRequestCount);
    }

    [Fact]
    public async Task OnFetch_QueueFull_CompletesFailureOnce()
    {
        (ContainerCatalog catalog, IContentContainer container) = TestZipContainer.Create(_source);
        var calls = new List<TransferCall>();
        var transfer = new NativeTransfer((_, parameters) => calls.Add(parameters));
        var errors = new List<Exception>();
        await using var coordinator = new FetchCoordinator(
            catalog,
            container,
            0,
            1,
            (_, error) => errors.Add(error),
            transfer
        );
        Invoke(coordinator, Encoding.UTF8.GetBytes("file.txt"), 8);
        Invoke(coordinator, Encoding.UTF8.GetBytes("file.txt"), 8);

        TransferCall call = Assert.Single(calls);
        Assert.Equal(0, call.Offset);
        Assert.Equal(8, call.Length);
        Assert.Equal(NativeTransfer.StatusCloudFileUnsuccessful, call.CompletionStatus);
        Assert.Equal(2, coordinator.Statistics.FetchAttempts);
        Assert.Equal(1, coordinator.Statistics.FailedFetches);
        Assert.Single(errors);
    }

    [Fact]
    public async Task OnFetch_NativeCompletionThrows_DoesNotRetryTerminalCompletion()
    {
        (ContainerCatalog catalog, IContentContainer container) = TestZipContainer.Create(_source);
        int calls = 0;
        var transfer = new NativeTransfer(
            (_, _) =>
            {
                calls++;
                throw new IOException("injected CfExecute failure");
            }
        );
        var errors = new List<Exception>();
        await using var coordinator = new FetchCoordinator(
            catalog,
            container,
            0,
            1,
            (_, error) => errors.Add(error),
            transfer
        );
        Invoke(coordinator, Encoding.UTF8.GetBytes("file.txt"), 8);
        Invoke(coordinator, Encoding.UTF8.GetBytes("file.txt"), 8);

        Assert.Equal(1, calls);
        Assert.Contains(errors, error => error.ToString().Contains("injected CfExecute failure"));
        Assert.Contains(errors, error => error.Message.Contains("queue full"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OnFetch_InvalidIdentity_DoesNotUnwindNativeBoundary(bool missingIdentity)
    {
        (ContainerCatalog catalog, IContentContainer container) = TestZipContainer.Create(_source);
        var calls = new List<TransferCall>();
        var transfer = new NativeTransfer((_, parameters) => calls.Add(parameters));
        int errors = 0;
        await using var coordinator = new FetchCoordinator(
            catalog,
            container,
            0,
            1,
            (_, _) =>
            {
                errors++;
                throw new IOException("handler failure");
            },
            transfer
        );

        Invoke(coordinator, missingIdentity ? null : [0xFF], 8);
        Assert.Single(calls);
        Assert.Equal(8, calls[0].Length); // CFAPI FileSize is the fallback without a valid identity.
        Assert.Equal(1, errors);
        Assert.Equal(1, coordinator.Statistics.FailedFetches);
    }

    [Fact]
    public async Task OnFetch_OversizedIdentity_RejectsAtSharedCfapiByteLimit()
    {
        (ContainerCatalog catalog, IContentContainer container) = TestZipContainer.Create(_source);
        var calls = new List<TransferCall>();
        var errors = new List<Exception>();
        await using var coordinator = new FetchCoordinator(
            catalog,
            container,
            0,
            1,
            (_, error) => errors.Add(error),
            new NativeTransfer((_, call) => calls.Add(call))
        );

        Invoke(coordinator, new byte[ContainerPath.MaxIdentityBytes + 1], 8);

        TransferCall failure = Assert.Single(calls);
        Assert.Equal(NativeTransfer.StatusCloudFileUnsuccessful, failure.CompletionStatus);
        Assert.Equal(0, failure.Offset);
        Assert.Equal(8, failure.Length);
        Assert.Equal(nint.Zero, failure.Buffer);
        Assert.IsType<InvalidDataException>(Assert.Single(errors));
        Assert.Equal(1, coordinator.Statistics.FetchAttempts);
        Assert.Equal(1, coordinator.Statistics.FailedFetches);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(4u)]
    public async Task OnFetch_ShortParameterPayload_IsReportedWithoutNativeCompletion(
        uint parameterSize
    )
    {
        (ContainerCatalog catalog, IContentContainer container) = TestZipContainer.Create(_source);
        var calls = new List<TransferCall>();
        var errors = new List<Exception>();
        await using var coordinator = new FetchCoordinator(
            catalog,
            container,
            0,
            1,
            (_, error) => errors.Add(error),
            new NativeTransfer((_, call) => calls.Add(call))
        );

        Invoke(
            coordinator,
            Encoding.UTF8.GetBytes("file.txt"),
            8,
            parameterSize: parameterSize
        );

        Assert.Empty(calls);
        Assert.IsType<InvalidDataException>(Assert.Single(errors));
        Assert.Equal(1, coordinator.Statistics.FetchAttempts);
        Assert.Equal(1, coordinator.Statistics.FailedFetches);
    }

    [Theory]
    [InlineData(28u)] // Required range only (older/compact layout).
    [InlineData(44u)] // Required and optional ranges.
    [InlineData(60u)] // Extended payload including dehydration metadata.
    public async Task OnFetch_CompatiblePayloadSizes_AcceptRequiredFields(uint paramSize)
    {
        (ContainerCatalog catalog, IContentContainer container) = TestZipContainer.Create(_source);
        var calls = new List<TransferCall>();
        var errors = new List<Exception>();
        await using var coordinator = new FetchCoordinator(
            catalog,
            container,
            0,
            1,
            (_, error) => errors.Add(error),
            new NativeTransfer((_, call) => calls.Add(call))
        );

        Invoke(coordinator, [0xFF], 8, parameterSize: paramSize);

        Assert.Equal(NativeTransfer.StatusCloudFileUnsuccessful, Assert.Single(calls).CompletionStatus);
        Assert.IsType<DecoderFallbackException>(Assert.Single(errors));
        Assert.Equal(1, coordinator.Statistics.FailedFetches);
    }

    [Fact]
    public async Task OnFetch_ParameterSizeOneByteShort_IsRejected()
    {
        (ContainerCatalog catalog, IContentContainer container) = TestZipContainer.Create(_source);
        uint minimumSize = (uint)(
            sizeof(uint)
            + Marshal.OffsetOf<CF_CALLBACK_PARAMETERS._Anonymous_e__Union._FetchData_e__Struct>(
                nameof(CF_CALLBACK_PARAMETERS._Anonymous_e__Union._FetchData_e__Struct.RequiredLength)
            ).ToInt32()
            + sizeof(long)
        );
        int calls = 0;
        var errors = new List<Exception>();
        await using var coordinator = new FetchCoordinator(
            catalog,
            container,
            0,
            1,
            (_, error) => errors.Add(error),
            new NativeTransfer((_, _) => calls++)
        );

        Invoke(
            coordinator,
            Encoding.UTF8.GetBytes("file.txt"),
            8,
            parameterSize: minimumSize - 1
        );

        Assert.Equal(0, calls);
        Assert.IsType<InvalidDataException>(Assert.Single(errors));
        Assert.Equal(1, coordinator.Statistics.FailedFetches);
    }

    [Fact]
    public async Task OnFetch_MinimumRequiredFields_AreSufficientToFailBadIdentity()
    {
        (ContainerCatalog catalog, IContentContainer container) = TestZipContainer.Create(_source);
        uint minimumSize = (uint)(
            sizeof(uint)
            + Marshal.OffsetOf<CF_CALLBACK_PARAMETERS._Anonymous_e__Union._FetchData_e__Struct>(
                nameof(CF_CALLBACK_PARAMETERS._Anonymous_e__Union._FetchData_e__Struct.RequiredLength)
            ).ToInt32()
            + sizeof(long)
        );
        var calls = new List<TransferCall>();
        var errors = new List<Exception>();
        await using var coordinator = new FetchCoordinator(
            catalog,
            container,
            0,
            1,
            (_, error) => errors.Add(error),
            new NativeTransfer((_, call) => calls.Add(call))
        );

        Invoke(coordinator, [0xFF], 8, parameterSize: minimumSize);

        TransferCall failure = Assert.Single(calls);
        Assert.Equal(NativeTransfer.StatusCloudFileUnsuccessful, failure.CompletionStatus);
        Assert.Equal(0, failure.Offset);
        Assert.Equal(8, failure.Length);
        Assert.IsType<DecoderFallbackException>(Assert.Single(errors));
        Assert.Equal(1, coordinator.Statistics.FailedFetches);
    }

    [Theory]
    [InlineData(-1, 1, 8)]
    [InlineData(0, 0, 8)]
    [InlineData(8, 1, 8)]
    [InlineData(0, 1, 0)]
    [InlineData(-1, 10, 8)]
    public async Task OnFetch_InvalidRequiredRange_NeverCallsNative(
        long offset,
        long length,
        long fileLength
    )
    {
        (ContainerCatalog catalog, IContentContainer container) = TestZipContainer.Create(_source);
        var calls = new List<TransferCall>();
        var errors = new List<Exception>();
        await using var coordinator = new FetchCoordinator(
            catalog,
            container,
            0,
            1,
            (_, error) => errors.Add(error),
            new NativeTransfer((_, call) => calls.Add(call))
        );

        Invoke(coordinator, Encoding.UTF8.GetBytes("file.txt"), fileLength, offset, length);

        Assert.Empty(calls);
        Assert.IsType<InvalidDataException>(Assert.Single(errors));
        Assert.Equal(1, coordinator.Statistics.FetchAttempts);
        Assert.Equal(1, coordinator.Statistics.FailedFetches);
    }

    [Theory]
    [InlineData(0, 9, 8, 0, 8)]
    [InlineData(4097, 1000, 5000, 4096, 904)]
    [InlineData(long.MaxValue - 1, 10, long.MaxValue, long.MaxValue - 4095, 4095)]
    public async Task OnFetch_OverlongRange_FailsValidAlignedOverlapOnce(
        long offset,
        long length,
        long fileLength,
        long expectedOffset,
        long expectedLength
    )
    {
        (ContainerCatalog catalog, IContentContainer container) = TestZipContainer.Create(_source);
        var calls = new List<TransferCall>();
        var errors = new List<Exception>();
        await using var coordinator = new FetchCoordinator(
            catalog,
            container,
            0,
            1,
            (_, error) => errors.Add(error),
            new NativeTransfer((_, call) => calls.Add(call))
        );

        Invoke(coordinator, Encoding.UTF8.GetBytes("file.txt"), fileLength, offset, length);

        TransferCall failure = Assert.Single(calls);
        Assert.Equal(expectedOffset, failure.Offset);
        Assert.Equal(expectedLength, failure.Length);
        Assert.Equal(nint.Zero, failure.Buffer);
        Assert.Equal(NativeTransfer.StatusCloudFileUnsuccessful, failure.CompletionStatus);
        Assert.IsType<InvalidDataException>(Assert.Single(errors));
        Assert.Equal(1, coordinator.Statistics.FetchAttempts);
        Assert.Equal(1, coordinator.Statistics.FailedFetches);
    }

    [Fact]
    public async Task OnFetch_OverlongRange_NativeFailureIsNotRetried()
    {
        (ContainerCatalog catalog, IContentContainer container) = TestZipContainer.Create(_source);
        int nativeCalls = 0;
        var errors = new List<Exception>();
        await using var coordinator = new FetchCoordinator(
            catalog,
            container,
            0,
            1,
            (_, error) => errors.Add(error),
            new NativeTransfer(
                (_, _) =>
                {
                    nativeCalls++;
                    throw new IOException("injected native failure");
                }
            )
        );

        Invoke(coordinator, Encoding.UTF8.GetBytes("file.txt"), 8, 0, 9);

        Assert.Equal(1, nativeCalls);
        Assert.Equal(2, errors.Count);
        Assert.Contains(errors, error => error.Message.Contains("failure completion failed"));
        Assert.Contains(errors, error => error.Message.Contains("exceeds logical EOF"));
        Assert.Equal(1, coordinator.Statistics.FailedFetches);
    }

    [Fact]
    public void OnFetch_RequiredFieldsAbi_ExcludesCallbackUnionPadding()
    {
        Assert.Equal(
            8,
            Marshal.OffsetOf<CF_CALLBACK_PARAMETERS>(nameof(CF_CALLBACK_PARAMETERS.Anonymous))
                .ToInt32()
        );
        Assert.Equal(
            8,
            Marshal.OffsetOf<CF_CALLBACK_PARAMETERS._Anonymous_e__Union._FetchData_e__Struct>(
                nameof(CF_CALLBACK_PARAMETERS._Anonymous_e__Union._FetchData_e__Struct.RequiredFileOffset)
            ).ToInt32()
        );
        Assert.Equal(
            16,
            Marshal.OffsetOf<CF_CALLBACK_PARAMETERS._Anonymous_e__Union._FetchData_e__Struct>(
                nameof(CF_CALLBACK_PARAMETERS._Anonymous_e__Union._FetchData_e__Struct.RequiredLength)
            ).ToInt32()
        );

        // Documented ParamSize is four bytes plus the populated union arm,
        // not the eight-byte-aligned native offset of the union field.
        Assert.Equal(28u, (uint)(
            sizeof(uint)
            + Marshal.OffsetOf<CF_CALLBACK_PARAMETERS._Anonymous_e__Union._FetchData_e__Struct>(
                nameof(CF_CALLBACK_PARAMETERS._Anonymous_e__Union._FetchData_e__Struct.RequiredLength)
            ).ToInt32()
            + sizeof(long)
        ));
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(4u)]
    [InlineData(27u)]
    public async Task OnFetch_ProtectedPageRejectsPhysicallyShortParameterBuffer(uint paramSize)
    {
        (ContainerCatalog catalog, IContentContainer container) = TestZipContainer.Create(_source);
        int nativeCalls = 0;
        var errors = new List<Exception>();
        await using var coordinator = new FetchCoordinator(
            catalog,
            container,
            0,
            1,
            (_, error) => errors.Add(error),
            new NativeTransfer((_, _) => nativeCalls++)
        );

        int pageSize = Environment.SystemPageSize;
        nint pages = VirtualAlloc(nint.Zero, (nuint)(2 * pageSize), 0x3000, 0x04);
        Assert.NotEqual(nint.Zero, pages);
        try
        {
            // Only the four-byte ParamSize header is readable. Any access to
            // FetchData will cross into a PAGE_NOACCESS page and fault.
            Assert.True(VirtualProtect(pages + pageSize, (nuint)pageSize, 0x01, out _));
            nint header = pages + pageSize - sizeof(uint);
            Marshal.WriteInt32(header, checked((int)paramSize));
            nint info = Marshal.AllocHGlobal(Marshal.SizeOf<CF_CALLBACK_INFO>());
            try
            {
                Marshal.StructureToPtr(new CF_CALLBACK_INFO { FileSize = 8 }, info, false);
                coordinator.OnFetch(info, header);
            }
            finally
            {
                Marshal.FreeHGlobal(info);
            }
        }
        finally
        {
            Assert.True(VirtualFree(pages, 0, 0x8000));
        }

        Assert.Equal(0, nativeCalls);
        Assert.IsType<InvalidDataException>(Assert.Single(errors));
        Assert.Equal(1, coordinator.Statistics.FailedFetches);
    }

    [Fact]
    public async Task OnFetch_InvalidIdentityAndRange_NeverCallsNative()
    {
        (ContainerCatalog catalog, IContentContainer container) = TestZipContainer.Create(_source);
        int calls = 0;
        var errors = new List<Exception>();
        await using var coordinator = new FetchCoordinator(
            catalog,
            container,
            0,
            1,
            (_, error) => errors.Add(error),
            new NativeTransfer((_, _) => calls++)
        );

        Invoke(coordinator, [0xFF], 8, 8, 1);

        Assert.Equal(0, calls);
        Assert.IsType<InvalidDataException>(Assert.Single(errors));
        Assert.Equal(1, coordinator.Statistics.FailedFetches);
    }

    [Fact]
    public async Task OnFetch_InvalidIdentity_FailedNativeCompletionIsNotRetried()
    {
        (ContainerCatalog catalog, IContentContainer container) = TestZipContainer.Create(_source);
        int calls = 0;
        var errors = new List<Exception>();
        await using var coordinator = new FetchCoordinator(
            catalog,
            container,
            0,
            1,
            (_, error) => errors.Add(error),
            new NativeTransfer(
                (_, _) =>
                {
                    calls++;
                    throw new IOException("CfExecute failed");
                }
            )
        );

        Invoke(coordinator, [0xFF], 8);

        Assert.Equal(1, calls);
        Assert.Equal(2, errors.Count);
        Assert.Contains(errors, error => error.Message.Contains("failure completion failed"));
        Assert.Contains(errors, error => error is DecoderFallbackException);
        Assert.Equal(1, coordinator.Statistics.FailedFetches);
    }

    [Fact]
    public async Task OnFetch_NullPointers_AreReportedWithoutNativeCompletion()
    {
        (ContainerCatalog catalog, IContentContainer container) = TestZipContainer.Create(_source);
        int calls = 0;
        var errors = new List<Exception>();
        await using var coordinator = new FetchCoordinator(
            catalog,
            container,
            0,
            1,
            (_, error) => errors.Add(error),
            new NativeTransfer((_, _) => calls++)
        );

        coordinator.OnFetch(nint.Zero, nint.Zero);

        Assert.Equal(0, calls);
        Assert.IsType<InvalidDataException>(Assert.Single(errors));
        Assert.Equal(1, coordinator.Statistics.FetchAttempts);
        Assert.Equal(1, coordinator.Statistics.FailedFetches);
    }

    [Fact]
    public async Task OnFetch_Shutdown_CompletesFailureOnce()
    {
        (ContainerCatalog catalog, IContentContainer container) = TestZipContainer.Create(_source);
        int calls = 0;
        var transfer = new NativeTransfer((_, _) => calls++);
        var coordinator = new FetchCoordinator(catalog, container, 0, 1, (_, _) => { }, transfer);
        await coordinator.DisposeAsync();
        Invoke(coordinator, Encoding.UTF8.GetBytes("file.txt"), 8);
        Assert.Equal(1, calls);
        Assert.Equal(1, coordinator.Statistics.FailedFetches);
    }

    [Fact]
    public async Task Fetch_WorkerFailsAfterPartialTransfer_DoesNotDuplicateTerminalCompletion()
    {
        WriteArchive(131072);
        (ContainerCatalog catalog, IContentContainer container) = TestZipContainer.Create(_source);
        var calls = new List<TransferCall>();
        var failed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var transfer = new NativeTransfer(
            (_, parameters) =>
            {
                lock (calls)
                    calls.Add(parameters);

                if (parameters.CompletionStatus != 0)
                    failed.TrySetResult();
            }
        );
        var coordinator = new FetchCoordinator(
            catalog,
            container,
            1,
            1,
            (_, _) => { },
            transfer,
            _ => new FailAfterStream(new byte[131072], NativeTransfer.BufferSize)
        );

        await using (coordinator)
        {
            Invoke(coordinator, Encoding.UTF8.GetBytes("file.txt"), 131072);
            await failed.Task.WaitAsync(
                TimeSpan.FromSeconds(5),
                TestContext.Current.CancellationToken
            );
        }

        Assert.Equal(2, calls.Count);
        Assert.Equal(0, calls[0].CompletionStatus);
        Assert.Equal(0, calls[0].Offset);
        Assert.Equal(65536, calls[0].Length);
        Assert.Equal(NativeTransfer.StatusCloudFileUnsuccessful, calls[1].CompletionStatus);
        Assert.Equal(65536, calls[1].Offset);
        Assert.Equal(65536, calls[1].Length);
    }

    [Fact]
    public async Task Fetch_MissingArchiveMember_FailsRequestAndReportsPath()
    {
        (ContainerCatalog catalog, IContentContainer container) = TestZipContainer.Create(_source);
        var failed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reported = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var errors = new List<(string? Path, Exception Error)>();
        await using var coordinator = new FetchCoordinator(
            catalog,
            container,
            1,
            1,
            (path, error) =>
            {
                errors.Add((path, error));
                reported.TrySetResult();
            },
            new NativeTransfer(
                (_, call) =>
                {
                    if (call.CompletionStatus != 0)
                        failed.TrySetResult();
                }
            )
        );

        Invoke(coordinator, Encoding.UTF8.GetBytes("missing.txt"), 8);
        await Task.WhenAll(failed.Task, reported.Task)
            .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Contains(
            errors,
            item => item is { Path: "missing.txt", Error: FileNotFoundException }
        );
        Assert.Equal(1, coordinator.Statistics.FailedFetches);
    }

    [Fact]
    public async Task Fetch_FailureCompletionThrows_ReportsBothFailures()
    {
        (ContainerCatalog catalog, IContentContainer container) = TestZipContainer.Create(_source);
        var reported = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var errors = new List<Exception>();
        await using var coordinator = new FetchCoordinator(
            catalog,
            container,
            1,
            1,
            (_, error) =>
            {
                errors.Add(error);
                if (errors.Count == 2)
                    reported.TrySetResult();
            },
            new NativeTransfer((_, _) => throw new IOException("completion failed")),
            _ => throw new IOException("source failed")
        );

        Invoke(coordinator, Encoding.UTF8.GetBytes("file.txt"), 8);
        await reported.Task.WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken
        );

        Assert.Contains(errors, error => error.Message.Contains("failure completion failed"));
        Assert.Contains(errors, error => error.Message.Contains("source failed"));
    }

    [Fact]
    public async Task Fetch_TruncatedEntryDuringSkip_FailsRemainingRange()
    {
        WriteArchive(8192);
        (ContainerCatalog catalog, IContentContainer container) = TestZipContainer.Create(_source);
        var failed = new TaskCompletionSource<TransferCall>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        await using var coordinator = new FetchCoordinator(
            catalog,
            container,
            1,
            1,
            (_, _) => { },
            new NativeTransfer(
                (_, call) =>
                {
                    if (call.CompletionStatus != 0)
                        failed.TrySetResult(call);
                }
            ),
            _ => new MemoryStream(new byte[32])
        );

        Invoke(coordinator, Encoding.UTF8.GetBytes("file.txt"), 8192, 4096, 4096);
        TransferCall failure = await failed.Task.WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken
        );

        Assert.Equal(4096, failure.Offset);
        Assert.Equal(4096, failure.Length);
    }

    [Theory]
    [InlineData(4096, false)]
    [InlineData(2048, false)]
    [InlineData(4097, false)]
    [InlineData(-1, false)]
    [InlineData(4096, true)]
    public async Task Fetch_UsesActualSeekPosition_WithoutTransferringWrongBytes(
        long seekResult,
        bool sliced
    )
    {
        const int start = 4096;
        WriteArchive(8192);
        (ContainerCatalog catalog, IContentContainer container) = TestZipContainer.Create(_source);
        byte[] payload = new byte[8192];
        new Random(37).NextBytes(payload);
        const int sliceOrigin = 37;
        byte[] backing = new byte[payload.Length + sliceOrigin];
        payload.CopyTo(backing, sliceOrigin);
        Stream input = sliced
            ? new MemoryStream(backing, sliceOrigin, payload.Length)
            : new AdjustedSeekStream(payload, seekResult);
        var completed = new TaskCompletionSource<TransferCall>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        byte[]? transferred = null;
        var errors = new List<Exception>();
        var coordinator = new FetchCoordinator(
            catalog,
            container,
            1,
            1,
            (_, error) => errors.Add(error),
            new NativeTransfer(
                (_, call) =>
                {
                    if (call.CompletionStatus == 0)
                    {
                        transferred = new byte[checked((int)call.Length)];
                        Marshal.Copy(call.Buffer, transferred, 0, transferred.Length);
                    }

                    completed.TrySetResult(call);
                }
            ),
            _ => input
        );
        TransferCall result;
        await using (coordinator)
        {
            Invoke(coordinator, Encoding.UTF8.GetBytes("file.txt"), payload.Length, start, start);
            result = await completed.Task.WaitAsync(
                TimeSpan.FromSeconds(5),
                TestContext.Current.CancellationToken
            );
        }

        if (input is AdjustedSeekStream adjusted)
            Assert.Equal(1, adjusted.SeekCount);
        Assert.Equal(start, result.Offset);
        if (seekResult < 0 || seekResult > start)
        {
            Assert.Equal(NativeTransfer.StatusCloudFileUnsuccessful, result.CompletionStatus);
            Assert.Null(transferred);
            Assert.IsType<IOException>(Assert.Single(errors));
            Assert.Equal(0, Assert.IsType<AdjustedSeekStream>(input).BytesRead);
        }
        else
        {
            Assert.Equal(0, result.CompletionStatus);
            Assert.Equal(payload[start..], transferred);
            Assert.Empty(errors);
            if (input is AdjustedSeekStream counted)
                Assert.Equal(payload.Length - seekResult, counted.BytesRead);
        }
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint VirtualAlloc(
        nint address,
        nuint size,
        uint allocationType,
        uint protect
    );

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool VirtualProtect(
        nint address,
        nuint size,
        uint newProtect,
        out uint oldProtect
    );

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool VirtualFree(nint address, nuint size, uint freeType);

    private void WriteArchive(int length)
    {
        if (File.Exists(_source))
            File.Delete(_source);

        using ZipArchive archive = ZipFile.Open(_source, ZipArchiveMode.Create);
        using Stream output = archive
            .CreateEntry("file.txt", CompressionLevel.NoCompression)
            .Open();
        output.Write(new byte[length]);
    }

    private static unsafe void Invoke(
        FetchCoordinator coordinator,
        byte[]? identity,
        long fileLength,
        long requiredOffset = 0,
        long? requiredLength = null,
        uint? parameterSize = null
    )
    {
        nint identityPointer = identity is null ? nint.Zero : Marshal.AllocHGlobal(identity.Length);
        nint infoPointer = Marshal.AllocHGlobal(Marshal.SizeOf<CF_CALLBACK_INFO>());
        nint parametersPointer = Marshal.AllocHGlobal(Marshal.SizeOf<CF_CALLBACK_PARAMETERS>());
        try
        {
            if (identity is not null)
                Marshal.Copy(identity, 0, identityPointer, identity.Length);

            var info = new CF_CALLBACK_INFO
            {
                StructSize = (uint)Marshal.SizeOf<CF_CALLBACK_INFO>(),
                FileIdentity = (void*)identityPointer,
                FileIdentityLength = (uint)(identity?.Length ?? 0),
                FileSize = fileLength,
                ConnectionKey = (CF_CONNECTION_KEY)1,
                TransferKey = 2,
                RequestKey = 3,
            };
            var parameters = new CF_CALLBACK_PARAMETERS
            {
                ParamSize = parameterSize ?? (uint)(
                    sizeof(uint)
                    + sizeof(CF_CALLBACK_PARAMETERS._Anonymous_e__Union._FetchData_e__Struct)
                ),
            };
            parameters.FetchData.RequiredFileOffset = requiredOffset;
            parameters.FetchData.RequiredLength = requiredLength ?? fileLength;
            Marshal.StructureToPtr(info, infoPointer, false);
            Marshal.StructureToPtr(parameters, parametersPointer, false);
            coordinator.OnFetch(infoPointer, parametersPointer);
        }
        finally
        {
            Marshal.FreeHGlobal(parametersPointer);
            Marshal.FreeHGlobal(infoPointer);
            if (identityPointer != nint.Zero)
                Marshal.FreeHGlobal(identityPointer);
        }
    }

    private sealed class AdjustedSeekStream(byte[] data, long seekResult) : MemoryStream(data)
    {
        public int SeekCount { get; private set; }
        public long BytesRead { get; private set; }

        public override long Position
        {
            get => seekResult < 0 ? seekResult : base.Position;
            set => base.Position = value;
        }

        public override long Seek(long offset, SeekOrigin loc)
        {
            Assert.Equal(4096, offset);
            Assert.Equal(SeekOrigin.Begin, loc);
            SeekCount++;
            Position = Math.Max(0, seekResult);
            return seekResult;
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default
        )
        {
            int count = await base.ReadAsync(buffer, cancellationToken);
            BytesRead += count;
            return count;
        }
    }
}
