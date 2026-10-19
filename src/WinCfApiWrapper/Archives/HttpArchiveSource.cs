using System.Net;
using System.Net.Http.Headers;

namespace WinCfApiWrapper.Archives;

// A container owns one remote snapshot. Streams share its validator and length,
// so file opens cannot silently adopt a replacement archive or repeat the probe.
internal sealed class HttpArchiveSource(HttpClient client, Uri uri)
{
    private object? _initializationLock;
    private Snapshot? _snapshot;

    public Stream OpenRead(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        Snapshot snapshot =
            Volatile.Read(ref _snapshot)
            ?? LazyInitializer.EnsureInitialized(
                ref _snapshot,
                ref _initializationLock,
                () => Initialize(token)
            );
        return new HttpRangeStream(this, snapshot, token);
    }

    public async Task<HttpResponseMessage> SendAsync(
        long start,
        long end,
        Snapshot? expected,
        CancellationToken token
    )
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri)
        {
            Version = client.DefaultRequestVersion,
            VersionPolicy = client.DefaultVersionPolicy,
        };
        request.Headers.Range = new RangeHeaderValue(start, end);
        request.Headers.AcceptEncoding.ParseAdd("identity");
        if (expected?.ETag is { } etag)
            request.Headers.IfRange = new RangeConditionHeaderValue(etag);
        else if (expected?.Modified is { } modified)
            request.Headers.IfRange = new RangeConditionHeaderValue(modified);
        HttpResponseMessage response = await client
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token)
            .ConfigureAwait(false);
        try
        {
            Validate(response, start, end, expected);
            return response;
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    private static void Validate(
        HttpResponseMessage response,
        long start,
        long end,
        Snapshot? expected
    )
    {
        ContentRangeHeaderValue? range = response.Content.Headers.ContentRange;
        if (
            response.StatusCode != HttpStatusCode.PartialContent
            || range?.Unit != "bytes"
            || range.From != start
            || range.To != end
            || range.Length is null
            || range.Length <= end
            || (expected is not null && range.Length != expected.Length)
            || response.Content.Headers.ContentEncoding.Any(encoding => encoding != "identity")
            || (expected?.ETag is { } etag && !etag.Equals(response.Headers.ETag))
            || (
                expected is { ETag: null, Modified: { } modified }
                && response.Content.Headers.LastModified != modified
            )
        )
        {
            throw new IOException(
                "Server returned an invalid range or the remote archive changed."
            );
        }
    }

    private Snapshot Initialize(CancellationToken token)
    {
        // Custom async handlers may capture a UI context or scheduler. Only move
        // synchronous callers off it; normal fetch workers need no extra task.
        if (
            SynchronizationContext.Current is null
            && TaskScheduler.Current == TaskScheduler.Default
        )
            return ProbeAsync(token).GetAwaiter().GetResult();
        return Task.Run(() => ProbeAsync(token), token).GetAwaiter().GetResult();
    }

    private async Task<Snapshot> ProbeAsync(CancellationToken token)
    {
        using HttpResponseMessage response = await SendAsync(0, 0, null, token)
            .ConfigureAwait(false);
        EntityTagHeaderValue? etag = response.Headers.ETag is { IsWeak: false } strong
            ? strong
            : null;
        DateTimeOffset? modified = response.Content.Headers.LastModified;
        if (etag is null && modified is null)
        {
            throw new IOException(
                "Remote archives require a strong ETag or Last-Modified validator."
            );
        }

        return new Snapshot(response.Content.Headers.ContentRange!.Length!.Value, etag, modified);
    }

    public sealed record Snapshot(
        long Length,
        EntityTagHeaderValue? ETag,
        DateTimeOffset? Modified
    );
}
