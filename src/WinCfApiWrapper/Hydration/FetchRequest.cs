using Windows.Win32.Storage.CloudFilters;

namespace WinCfApiWrapper.Hydration;

internal sealed class FetchRequest : IDisposable
{
    private readonly CancellationTokenSource _cancellation;
    private readonly List<TransferRange> _pending = [];
    private bool _disposed;
    private bool _finished;
    private bool _nativeCanceled;

    public FetchRequest(
        CF_CALLBACK_INFO info,
        long requiredFileOffset,
        long requiredLength,
        string relativePath
    )
    {
        Info = info;
        RequiredFileOffset = requiredFileOffset;
        RequiredLength = requiredLength;
        RelativePath = relativePath;
        LogicalLength = info.FileSize;
        Key = new FetchKey(info.ConnectionKey, info.TransferKey, info.RequestKey);
        if (info.FileSize > 0)
        {
            _pending.Add(
                TransferRange.ForRequest(requiredFileOffset, requiredLength, info.FileSize)
            );
        }

        _cancellation = new CancellationTokenSource();
    }

    public FetchKey Key { get; }

    public CancellationToken Token => _cancellation.Token;

    public object Gate { get; } = new();

    public bool IsCanceled => _cancellation.IsCancellationRequested;

    public bool NativeCanceled
    {
        get
        {
            lock (Gate)
                return _nativeCanceled;
        }
    }

    public CF_CALLBACK_INFO Info { get; }

    public long RequiredFileOffset { get; }

    public long RequiredLength { get; }

    public string RelativePath { get; }

    public long LogicalLength { get; set; }

    public long CompletedThrough { get; set; } = -1;

    public bool NativeCompletionFailed { get; set; }

    public void Dispose()
    {
        lock (Gate)
        {
            if (!_disposed)
            {
                _disposed = true;
                _cancellation.Dispose();
            }
        }
    }

    public bool IsPending(long offset, long length)
    {
        lock (Gate)
        {
            return !_finished
                && _pending.Any(range =>
                    offset >= range.Offset && length <= range.Offset + range.Length - offset
                );
        }
    }

    public TransferRange[] Pending()
    {
        lock (Gate)
            return _pending.ToArray();
    }

    public void Completed(long offset, long length)
    {
        lock (Gate)
        {
            Remove(offset, length);
            if (_pending.Count == 0)
                _finished = true;
        }
    }

    public void Cancel(long offset, long length)
    {
        if (offset < 0 || length <= 0 || offset > long.MaxValue - length)
            throw new InvalidDataException("Invalid CANCEL_FETCH_DATA range.");

        lock (Gate)
        {
            if (_finished || _disposed)
                return;

            // A transfer is atomic at 4 KiB (or the final EOF tail). Retain
            // partly canceled blocks because uncanceled bytes still need them.
            long start = offset / NativeTransfer.Alignment * NativeTransfer.Alignment;
            if (offset % NativeTransfer.Alignment != 0)
            {
                start =
                    start > long.MaxValue - NativeTransfer.Alignment
                        ? long.MaxValue
                        : start + NativeTransfer.Alignment;
            }

            long end = Math.Min(LogicalLength, offset + length);
            if (end != LogicalLength)
                end = end / NativeTransfer.Alignment * NativeTransfer.Alignment;

            if (start < end)
                Remove(start, end - start);

            if (_pending.Count == 0)
            {
                _finished = true;
                _nativeCanceled = true;
                _cancellation.Cancel();
            }
        }
    }

    public void StopTransport()
    {
        lock (Gate)
        {
            if (!_disposed)
                _cancellation.Cancel();
        }
    }

    private void Remove(long offset, long length)
    {
        long end = offset + length;
        for (int i = _pending.Count - 1; i >= 0; i--)
        {
            TransferRange range = _pending[i];
            long rangeEnd = range.Offset + range.Length;
            if (end <= range.Offset || offset >= rangeEnd)
                continue;

            _pending.RemoveAt(i);
            if (offset > range.Offset)
            {
                _pending.Insert(i, new TransferRange(range.Offset, offset - range.Offset));
                i++;
            }

            if (end < rangeEnd)
                _pending.Insert(i, new TransferRange(end, rangeEnd - end));
        }
    }
}
