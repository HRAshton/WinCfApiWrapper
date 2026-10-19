using System.Buffers;
using System.Net;
using System.Net.Http.Headers;

namespace WinCfApiWrapper.Archives;

// Requests only the bytes consumed by ZipArchive. Never accepts a server fallback
// to a full response, and pins subsequent requests to the first response's version.
internal sealed class HttpRangeStream : Stream
{
    private readonly HttpClient _client;
    private readonly Uri _uri;
    private readonly CancellationToken _token;
    private readonly EntityTagHeaderValue? _etag;
    private readonly DateTimeOffset? _modified;
    private readonly long _length;
    private long _position;
    private bool _disposed;

    public HttpRangeStream(HttpClient client, Uri uri, CancellationToken token)
    {
        _client = client;
        _uri = uri;
        _token = token;
        using HttpResponseMessage response = Send(0, 0, token);
        _length = response.Content.Headers.ContentRange?.Length
            ?? throw new IOException("The server did not supply the archive length.");
        _etag = response.Headers.ETag is { IsWeak: false } etag ? etag : null;
        _modified = response.Content.Headers.LastModified;
        if (_etag is null && _modified is null)
            throw new IOException("Remote archives require a strong ETag or Last-Modified validator.");
    }

    public override bool CanRead => !_disposed;
    public override bool CanSeek => !_disposed;
    public override bool CanWrite => false;
    public override long Length => _length;

    public override long Position
    {
        get => _position;
        set => _position = value >= 0 ? value : throw new ArgumentOutOfRangeException(nameof(value));
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _token.ThrowIfCancellationRequested();
        int size = (int)Math.Min(Math.Min(buffer.Length, 64 * 1024), Math.Max(0, Length - Position));
        if (size == 0)
            return 0;
        using HttpResponseMessage response = Send(Position, Position + size - 1, _token);
        using Stream body = response.Content.ReadAsStream(_token);
        byte[] rented = ArrayPool<byte>.Shared.Rent(size);
        try
        {
            body.ReadExactlyAsync(rented.AsMemory(0, size), _token).AsTask().GetAwaiter().GetResult();
            rented.AsSpan(0, size).CopyTo(buffer);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }

        Position += size;
        return size;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_token, cancellationToken);
        linked.Token.ThrowIfCancellationRequested();
        int size = (int)Math.Min(Math.Min(buffer.Length, 64 * 1024), Math.Max(0, Length - Position));
        if (size == 0)
            return 0;
        using var request = CreateRequest(Position, Position + size - 1);
        using HttpResponseMessage response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token).ConfigureAwait(false);
        Validate(response, Position, Position + size - 1);
        await using Stream body = await response.Content.ReadAsStreamAsync(linked.Token).ConfigureAwait(false);
        await body.ReadExactlyAsync(buffer[..size], linked.Token).ConfigureAwait(false);
        Position += size;
        return size;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
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

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        _disposed = true;
        base.Dispose(disposing);
    }

    private HttpRequestMessage CreateRequest(long start, long end)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, _uri);
        request.Headers.Range = new RangeHeaderValue(start, end);
        request.Headers.AcceptEncoding.ParseAdd("identity");
        if (_etag is not null)
            request.Headers.IfRange = new RangeConditionHeaderValue(_etag);
        else if (_modified is { } modified)
            request.Headers.IfRange = new RangeConditionHeaderValue(modified);
        return request;
    }

    private HttpResponseMessage Send(long start, long end, CancellationToken token)
    {
        using var request = CreateRequest(start, end);
        HttpResponseMessage response = _client.Send(request, HttpCompletionOption.ResponseHeadersRead, token);
        try
        {
            Validate(response, start, end);
            return response;
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    private void Validate(HttpResponseMessage response, long start, long end)
    {
        ContentRangeHeaderValue? range = response.Content.Headers.ContentRange;
        if (response.StatusCode != HttpStatusCode.PartialContent
            || range?.Unit != "bytes" || range.From != start || range.To != end
            || range.Length is null || range.Length <= end
            || (_length != 0 && range.Length != _length)
            || response.Content.Headers.ContentEncoding.Any(encoding => encoding != "identity")
            || (_etag is not null && !_etag.Equals(response.Headers.ETag))
            || (_etag is null && _modified is not null && response.Content.Headers.LastModified != _modified))
            throw new IOException("Server returned an invalid range or the remote archive changed.");
    }
}
