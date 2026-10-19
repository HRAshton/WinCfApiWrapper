namespace WinCfApiWrapper.Tests.Archives;

internal sealed class ReadTrackingStream(byte[] bytes) : Stream
{
    private readonly MemoryStream _inner = new(bytes, writable: false);

    public override bool CanRead => _inner.CanRead;

    public override bool CanSeek => _inner.CanSeek;

    public override bool CanWrite => false;

    public override long Length => _inner.Length;

    public override long Position
    {
        get => _inner.Position;
        set => _inner.Position = value;
    }

    public long BytesRead { get; private set; }

    // Count only reads that return data; empty, EOF, failed, and canceled reads do not fetch bytes.
    public int ReadCalls { get; private set; }

    public void ResetCounts()
    {
        BytesRead = 0;
        ReadCalls = 0;
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        Record(_inner.Read(buffer, offset, count));

    public override int Read(Span<byte> buffer) => Record(_inner.Read(buffer));

    public override int ReadByte()
    {
        int value = _inner.ReadByte();
        if (value >= 0)
            Record(1);
        return value;
    }

    public override async Task<int> ReadAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken
    )
    {
        return Record(
            await _inner.ReadAsync(buffer, offset, count, cancellationToken).ConfigureAwait(false)
        );
    }

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default
    )
    {
        return Record(await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false));
    }

    public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);

    public override void Flush() => _inner.Flush();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _inner.Dispose();
        base.Dispose(disposing);
    }

    private int Record(int count)
    {
        if (count > 0)
        {
            BytesRead += count;
            ReadCalls++;
        }

        return count;
    }
}
