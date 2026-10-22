using System.Buffers;

namespace WinCfApiWrapper.Archives;

// Each cursor streams bounded ranges from its container's pinned HTTP snapshot.
internal sealed class HttpRangeStream(
    HttpArchiveSource source,
    HttpArchiveSource.Snapshot snapshot,
    CancellationToken token
) : Stream
{
    private long _position;
    private bool _disposed;

    public override bool CanRead => !_disposed;
    public override bool CanSeek => !_disposed;
    public override bool CanWrite => false;

    public override long Length
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return snapshot.Length;
        }
    }

    public override long Position
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _position;
        }

        set
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _position = value >= 0 ? value : throw new ArgumentOutOfRangeException(nameof(value));
        }
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        ReadSynchronously(buffer.AsMemory(offset, count));

    public override int Read(Span<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        token.ThrowIfCancellationRequested();
        int size = (int)Math.Min(
            Math.Min(buffer.Length, 64 * 1024),
            Math.Max(0, Length - Position)
        );
        if (size == 0)
            return 0;
        byte[] rented = ArrayPool<byte>.Shared.Rent(size);
        try
        {
            int read = ReadSynchronously(rented.AsMemory(0, size));
            rented.AsSpan(0, read).CopyTo(buffer);
            return read;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default
    )
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var linked =
            token.CanBeCanceled && cancellationToken.CanBeCanceled && token != cancellationToken
                ? CancellationTokenSource.CreateLinkedTokenSource(token, cancellationToken)
                : null;
        CancellationToken readToken =
            linked?.Token ?? (cancellationToken.CanBeCanceled ? cancellationToken : token);
        readToken.ThrowIfCancellationRequested();
        int size = (int)Math.Min(
            Math.Min(buffer.Length, 64 * 1024),
            Math.Max(0, Length - Position)
        );
        if (size == 0)
            return 0;
        using CancellationTokenSource? deadline = source.CreateDeadline(readToken);
        CancellationToken effectiveToken = deadline?.Token ?? readToken;
        try
        {
            using HttpResponseMessage response = await source
                .SendAsync(Position, Position + size - 1, snapshot, effectiveToken)
                .ConfigureAwait(false);
            await using Stream body = await response
                .Content.ReadAsStreamAsync(effectiveToken)
                .ConfigureAwait(false);
            await body.ReadExactlyAsync(buffer[..size], effectiveToken).ConfigureAwait(false);
            Position += size;
            return size;
        }
        catch (OperationCanceledException ex) when (
            deadline is not null &&
            deadline.IsCancellationRequested &&
            !readToken.IsCancellationRequested
        )
        {
            throw new TimeoutException("HTTP archive range read exceeded its deadline.", ex);
        }
    }

    public override Task<int> ReadAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken
    ) => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override long Seek(long offset, SeekOrigin origin)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => checked(Position + offset),
            SeekOrigin.End => checked(Length + offset),
            _ => throw new ArgumentOutOfRangeException(nameof(origin)),
        };
        return Position;
    }

    public override void Flush() => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        _disposed = true;
        base.Dispose(disposing);
    }

    private int ReadSynchronously(Memory<byte> buffer)
    {
        if (
            SynchronizationContext.Current is null
            && TaskScheduler.Current == TaskScheduler.Default
        )
            return ReadAsync(buffer).AsTask().GetAwaiter().GetResult();
        return Task.Run(() => ReadAsync(buffer).AsTask(), token).GetAwaiter().GetResult();
    }
}
