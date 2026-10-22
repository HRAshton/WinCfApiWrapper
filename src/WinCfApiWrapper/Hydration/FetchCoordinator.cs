using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Channels;
using WinCfApiWrapper.Containers;
using WinCfApiWrapper.Public;
using Windows.Win32.Storage.CloudFilters;

namespace WinCfApiWrapper.Hydration;

// Bounded native callback queue. Worker tasks own their streams and outlive every queued request.
internal sealed class FetchCoordinator : IAsyncDisposable
{
    private const int MaxIdentityBytes = 4096;

    // Only the required fields are read. The FETCH_DATA union has gained
    // optional trailing fields across Windows versions; older ParamSize
    // values remain usable if the required range fields are present.
    private static readonly uint MinimumFetchDataSize = (uint)(
        sizeof(uint)
        + Marshal.OffsetOf<CF_CALLBACK_PARAMETERS._Anonymous_e__Union._FetchData_e__Struct>(
            nameof(CF_CALLBACK_PARAMETERS._Anonymous_e__Union._FetchData_e__Struct.RequiredLength)
        ).ToInt32()
        + sizeof(long)
    );
    private readonly ContainerCatalog _catalog;
    private readonly Channel<FetchRequest> _channel;
    private readonly IContentContainer _container;
    private readonly Action<string?, Exception> _onError;
    private readonly Func<ContainerEntry, Stream>? _openEntry;
    private readonly Action<OperationProgress>? _progress;

    // CFAPI can reuse a key while an earlier callback is still completing.
    // Track each callback independently; cancellation applies to every matching key.
    private readonly ConcurrentDictionary<FetchRequest, byte> _requests = new();
    private readonly NativeTransfer _transfer;
    private readonly Task[] _workers;
    private int _active;
    private long _attempts;
    private long _bytes;
    private long _failures;
    private int _stopping;

    public FetchCoordinator(
        ContainerCatalog catalog,
        IContentContainer container,
        int workers,
        int queueCapacity,
        Action<string?, Exception> onError,
        NativeTransfer? transfer = null,
        Func<ContainerEntry, Stream>? openEntry = null,
        Action<OperationProgress>? progress = null
    )
    {
        _catalog = catalog;
        _container = container;
        _onError = onError;
        _transfer = transfer ?? new NativeTransfer();
        _openEntry = openEntry;
        _progress = progress;
        _channel = Channel.CreateBounded<FetchRequest>(
            new BoundedChannelOptions(queueCapacity)
            {
                SingleReader = false,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait,
            }
        );
        _workers = Enumerable.Range(0, workers).Select(_ => Task.Run(WorkAsync)).ToArray();
    }

    public MountStatistics Statistics =>
        new(
            Interlocked.Read(ref _attempts),
            Interlocked.Read(ref _failures),
            Interlocked.Read(ref _bytes),
            Volatile.Read(ref _active)
        );

    internal int PendingRequestCount => _requests.Count;

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _stopping, 1) == 0)
        {
            _channel.Writer.TryComplete();
            foreach (FetchRequest request in _requests.Keys)
                request.StopTransport();
        }

        await Task.WhenAll(_workers).ConfigureAwait(false);
    }

    // Invoked from unmanaged callback; no waiting or unbounded Task.Run.
    public unsafe void OnFetch(nint infoPointer, nint parametersPointer)
    {
        try
        {
            if (infoPointer == nint.Zero || parametersPointer == nint.Zero)
                throw new InvalidDataException("FETCH_DATA callback pointers are null.");

            CF_CALLBACK_INFO info = *(CF_CALLBACK_INFO*)infoPointer;
            var parameters = (CF_CALLBACK_PARAMETERS*)parametersPointer;
            Interlocked.Increment(ref _attempts);

            // ParamSize excludes union padding; inspect only the fields it covers.
            if (parameters->ParamSize < MinimumFetchDataSize)
                throw new InvalidDataException("Malformed FETCH_DATA parameters.");

            long requiredOffset = parameters->FetchData.RequiredFileOffset;
            long requiredLength = parameters->FetchData.RequiredLength;
            // A wholly invalid range has no safe native completion. An overlong
            // request beginning inside the file can still fail its valid overlap.
            if (
                info.FileSize <= 0
                || requiredOffset < 0
                || requiredLength <= 0
                || requiredOffset >= info.FileSize
            )
                throw new InvalidDataException("FETCH_DATA range is outside the logical file.");

            if (requiredLength > info.FileSize - requiredOffset)
            {
                long alignedStart =
                    requiredOffset / NativeTransfer.Alignment * NativeTransfer.Alignment;
                // The final range ends exactly at EOF and is legal even when short.
                using var overlapping = new FetchRequest(
                    info,
                    alignedStart,
                    info.FileSize - alignedStart,
                    string.Empty
                );
                Interlocked.Increment(ref _failures);
                TryFail(
                    overlapping,
                    new InvalidDataException("FETCH_DATA required range exceeds logical EOF.")
                );

                return;
            }

            string name;
            try
            {
                name = ReadIdentity(info);
            }
            catch (Exception identityError)
            {
                Interlocked.Increment(ref _failures);

                // FileSize comes from CFAPI and remains available when identity bytes are malformed.
                using var invalid = new FetchRequest(
                    info,
                    requiredOffset,
                    requiredLength,
                    string.Empty
                );
                TryFail(invalid, identityError);

                return;
            }

            var request = new FetchRequest(
                info,
                requiredOffset,
                requiredLength,
                name
            );
            bool added = Volatile.Read(ref _stopping) == 0 && _requests.TryAdd(request, 0);
            switch (added)
            {
                case true when _channel.Writer.TryWrite(request):
                    return;
                case true:
                    _requests.TryRemove(request, out _);
                    break;
            }

            Interlocked.Increment(ref _failures);
            if (_catalog.Entries.TryGetValue(name, out ContainerEntry? entry))
                request.LogicalLength = entry.Length;

            TryFail(request, new IOException("Fetch queue full or mount stopping."));
            request.Dispose();
        }
        catch (Exception ex)
        {
            // An invalid payload has no reliable completion range; log without
            // touching the native transfer key or retrying CfExecute.
            Interlocked.Increment(ref _failures);
            Report(null, ex);
        }
    }

    public unsafe void OnCancelFetch(nint infoPointer, nint parametersPointer)
    {
        try
        {
            if (infoPointer == nint.Zero || parametersPointer == nint.Zero)
                throw new InvalidDataException("CANCEL_FETCH_DATA callback pointers are null.");

            CF_CALLBACK_INFO info = *(CF_CALLBACK_INFO*)infoPointer;
            var parameters = (CF_CALLBACK_PARAMETERS*)parametersPointer;

            // CFAPI reports sizeof(Cancel.FetchData) + two ULONGs, excluding x64 union padding.
            if (parameters->ParamSize < 24)
                throw new InvalidDataException("Malformed CANCEL_FETCH_DATA parameters.");

            long offset = parameters->Cancel.FetchData.FileOffset;
            long length = parameters->Cancel.FetchData.Length;
            if (offset < 0 || length <= 0 || offset > long.MaxValue - length)
                throw new InvalidDataException("Malformed CANCEL_FETCH_DATA parameters.");

            var key = new FetchKey(info.ConnectionKey, info.TransferKey, info.RequestKey);
            foreach (FetchRequest request in _requests.Keys)
            {
                if (request.Key == key)
                    request.Cancel(offset, length);
            }
        }
        catch (Exception ex)
        {
            Report(null, ex);
        }
    }

    private void TryFail(FetchRequest request, Exception originalError)
    {
        try
        {
            _transfer.Fail(request);
        }
        catch (Exception completionError)
        {
            Report(
                request.RelativePath,
                new IOException("FETCH_DATA failure completion failed.", completionError)
            );
        }

        Report(request.RelativePath, originalError);
    }

    private static unsafe string ReadIdentity(CF_CALLBACK_INFO info)
    {
        if (info.FileIdentity == null || info.FileIdentityLength is 0 or > MaxIdentityBytes)
            throw new InvalidDataException("Invalid file identity length.");

        byte[] bytes = new byte[checked((int)info.FileIdentityLength)];
        Marshal.Copy((nint)info.FileIdentity, bytes, 0, bytes.Length);

        return new UTF8Encoding(false, true).GetString(bytes);
    }

    private async Task WorkAsync()
    {
        await foreach (FetchRequest request in _channel.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            Interlocked.Increment(ref _active);
            try
            {
                if (request.NativeCanceled)
                    continue;

                if (!_catalog.Entries.TryGetValue(request.RelativePath, out ContainerEntry? entry))
                {
                    throw new FileNotFoundException(
                        "Container entry missing",
                        request.RelativePath
                    );
                }

                request.LogicalLength = entry.Length;
                await TransferAsync(request, entry).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (request.NativeCanceled)
            {
                // CFAPI canceled every remaining range; no failure completion is legal.
            }
            catch (Exception ex)
            {
                if (!request.NativeCanceled && Volatile.Read(ref _stopping) == 0)
                    Interlocked.Increment(ref _failures);

                // A failed CfExecute may have reached CFAPI; never blindly submit it again.
                if (!request.NativeCompletionFailed)
                {
                    try
                    {
                        _transfer.Fail(request);
                    }
                    catch (Exception completionError)
                    {
                        Report(
                            request.RelativePath,
                            new IOException(
                                "FETCH_DATA failure completion failed.",
                                completionError
                            )
                        );
                    }
                }

                if (!request.NativeCanceled && Volatile.Read(ref _stopping) == 0)
                    Report(request.RelativePath, ex);
            }
            finally
            {
                _requests.TryRemove(request, out _);
                request.Dispose();
                Interlocked.Decrement(ref _active);
            }
        }
    }

    private async Task TransferAsync(FetchRequest request, ContainerEntry entry)
    {
        // ReSharper disable once InlineTemporaryVariable
        NativeTransfer transfer = _transfer;
        (long start, long length) = TransferRange.ForRequest(
            request.RequiredFileOffset,
            request.RequiredLength,
            entry.Length
        );
        long end = start + length;
        await using Stream input = _openEntry is null
            ? await _container.OpenReadAsync(entry, request.Token).ConfigureAwait(false)
            : _openEntry(entry);
        if (!input.CanRead)
            throw new InvalidDataException("Container returned a stream that cannot be read.");
        byte[] buffer = new byte[NativeTransfer.BufferSize];
        long skipped = 0;
        if (input.CanSeek)
        {
            // On .NET 8, a sliced MemoryStream can return a backing-buffer
            // offset from Seek. Position is relative to the stream itself.
            input.Seek(start, SeekOrigin.Begin);
            skipped = input.Position;
            if (skipped < 0 || skipped > start)
                throw new IOException("Container stream sought outside the requested prefix.");
        }

        while (skipped < start)
        {
            int take = (int)Math.Min(buffer.Length, start - skipped);
            int count = await input
                .ReadAsync(buffer.AsMemory(0, take), request.Token)
                .ConfigureAwait(false);
            if (count == 0)
                throw new EndOfStreamException("Container entry ended before its declared length.");

            skipped += count;
        }

        for (long offset = start; offset < end; )
        {
            int size = (int)Math.Min(buffer.Length, end - offset);
            await input
                .ReadExactlyAsync(buffer.AsMemory(0, size), request.Token)
                .ConfigureAwait(false);
            request.Token.ThrowIfCancellationRequested();
            lock (request.Gate)
            {
                foreach (TransferRange pending in request.Pending())
                {
                    long partStart = Math.Max(offset, pending.Offset);
                    long partEnd = Math.Min(offset + size, pending.Offset + pending.Length);
                    if (partStart >= partEnd)
                        continue;

                    int partSize = checked((int)(partEnd - partStart));
                    byte[] partBuffer =
                        partStart == offset && partSize == size
                            ? buffer
                            : buffer.AsSpan(checked((int)(partStart - offset)), partSize).ToArray();
                    if (transfer.Complete(request, partBuffer, partSize, partStart))
                        Interlocked.Add(ref _bytes, partSize);
                }
            }

            offset += size;
            _progress?.Invoke(
                new OperationProgress("Fetch", offset - start, length, entry.RelativePath)
            );
        }
    }

    private void Report(string? relativePath, Exception exception)
    {
        try
        {
            _onError(relativePath, exception);
        }
        catch
        {
            /* User handlers cannot unwind native callback or worker. */
        }
    }
}
