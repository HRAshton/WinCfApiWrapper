using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using WinCfApiWrapper.Containers;
using WinCfApiWrapper.Hydration;
using WinCfApiWrapper.Public;
using Windows.Win32.Storage.CloudFilters;
using Xunit;

namespace WinCfApiWrapper.Tests.Hydration;

public sealed class FetchCoordinatorTests : IDisposable
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
    [InlineData(4096)]
    [InlineData(2048)]
    [InlineData(4097)]
    [InlineData(-1)]
    public async Task Fetch_UsesReturnedSeekPosition_WithoutTransferringWrongBytes(long seekResult)
    {
        const int start = 4096;
        WriteArchive(8192);
        (ContainerCatalog catalog, IContentContainer container) = TestZipContainer.Create(_source);
        byte[] payload = new byte[8192];
        new Random(37).NextBytes(payload);
        var input = new AdjustedSeekStream(payload, seekResult);
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

        Assert.Equal(1, input.SeekCount);
        Assert.Equal(start, result.Offset);
        if (seekResult < 0 || seekResult > start)
        {
            Assert.Equal(NativeTransfer.StatusCloudFileUnsuccessful, result.CompletionStatus);
            Assert.Null(transferred);
            Assert.IsType<IOException>(Assert.Single(errors));
            Assert.Equal(0, input.BytesRead);
        }
        else
        {
            Assert.Equal(0, result.CompletionStatus);
            Assert.Equal(payload[start..], transferred);
            Assert.Empty(errors);
            Assert.Equal(payload.Length - seekResult, input.BytesRead);
        }
    }

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
        long? requiredLength = null
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
                ParamSize = (uint)(
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

        public override long Seek(long offset, SeekOrigin origin)
        {
            Assert.Equal(4096, offset);
            Assert.Equal(SeekOrigin.Begin, origin);
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
