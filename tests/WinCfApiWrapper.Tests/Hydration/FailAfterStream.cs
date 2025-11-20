namespace WinCfApiWrapper.Tests.Hydration;

internal sealed class FailAfterStream : Stream
{
    private readonly MemoryStream _inner;
    private readonly long _limit;

    public FailAfterStream(byte[] bytes, long limit)
    {
        _inner = new MemoryStream(bytes);
        _limit = limit;
    }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => _inner.Length;

    public override long Position
    {
        get => _inner.Position;
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (_inner.Position >= _limit)
            throw new IOException("Injected entry read failure.");

        return _inner.Read(buffer, offset, (int)Math.Min(count, _limit - _inner.Position));
    }

    public override ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default
    )
    {
        if (_inner.Position >= _limit)
            throw new IOException("Injected entry read failure.");

        return _inner.ReadAsync(
            buffer[..(int)Math.Min(buffer.Length, _limit - _inner.Position)],
            cancellationToken
        );
    }

    public override void Flush()
    {
        throw new NotSupportedException();
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        throw new NotSupportedException();
    }

    public override void SetLength(long value)
    {
        throw new NotSupportedException();
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        throw new NotSupportedException();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _inner.Dispose();

        base.Dispose(disposing);
    }
}
