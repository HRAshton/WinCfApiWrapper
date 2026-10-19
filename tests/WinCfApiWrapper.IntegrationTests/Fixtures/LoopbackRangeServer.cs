using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;

namespace WinCfApiWrapper.IntegrationTests.Fixtures;

// Serves one byte-range request per TCP connection. The transport test can hold
// responses until two connections are active to make serialization observable.
internal sealed class LoopbackRangeServer : IAsyncDisposable
{
    private readonly Task _accepting;
    private readonly byte[] _archive;
    private readonly ConcurrentBag<Task> _connections = [];
    private readonly ConcurrentQueue<Exception> _errors = new();
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly ConcurrentDictionary<int, byte> _observedPorts = new();

    private readonly TaskCompletionSource _overlap = new(
        TaskCreationOptions.RunContinuationsAsynchronously
    );

    private readonly CancellationTokenSource _shutdown = new();
    private int _active;
    private int _maxConcurrent;
    private volatile bool _observe;
    private volatile bool _waitForOverlap;

    public LoopbackRangeServer(byte[] archive)
    {
        _archive = archive;
        _listener.Start();
        int port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        Url = new Uri($"http://127.0.0.1:{port}/archive.zip");
        _accepting = AcceptAsync();
    }

    public Uri Url { get; }

    public int MaxConcurrent => Volatile.Read(ref _maxConcurrent);

    public int DistinctConnections => _observedPorts.Count;

    public IReadOnlyCollection<Exception> Errors => _errors.ToArray();

    public async ValueTask DisposeAsync()
    {
        await _shutdown.CancelAsync();
        _listener.Stop();
        await _accepting;
        await Task.WhenAll(_connections);
        _shutdown.Dispose();
    }

    public void ObserveRequests(bool waitForOverlap = false)
    {
        _waitForOverlap = waitForOverlap;
        _observe = true;
    }

    private async Task AcceptAsync()
    {
        try
        {
            while (!_shutdown.IsCancellationRequested)
            {
                TcpClient client = await _listener.AcceptTcpClientAsync(_shutdown.Token);
                _connections.Add(ServeAsync(client));
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
            // Ignore cancellation during shutdown
        }
        catch (SocketException) when (_shutdown.IsCancellationRequested)
        {
            // Ignore cancellation during shutdown
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using (client)
        {
            bool observed = false;
            try
            {
                await using NetworkStream network = client.GetStream();
                using var reader = new StreamReader(network, Encoding.ASCII, leaveOpen: true);
                string? requestLine = await reader.ReadLineAsync(_shutdown.Token);
                bool isExpectedArchiveRequest =
                    requestLine is not null
                    && requestLine.StartsWith(
                        "GET /archive.zip HTTP/1.1",
                        StringComparison.Ordinal
                    );
                if (!isExpectedArchiveRequest)
                    throw new InvalidDataException("Unexpected HTTP request.");

                string? line;
                RangeItemHeaderValue? range = null;
                while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync(_shutdown.Token)))
                {
                    if (line.StartsWith("Range: ", StringComparison.OrdinalIgnoreCase))
                        range = RangeHeaderValue.Parse(line[7..]).Ranges.Single();
                }

                if (range?.From is null)
                    throw new InvalidDataException("Archive request did not use a byte range.");
                long start = range.From.Value;
                long end = Math.Min(range.To ?? (_archive.Length - 1), _archive.Length - 1);
                int size = checked((int)(end - start + 1));

                observed = _observe;
                if (observed)
                {
                    _observedPorts.TryAdd(((IPEndPoint)client.Client.RemoteEndPoint!).Port, 0);
                    int active = Interlocked.Increment(ref _active);
                    int seen;
                    while (
                        (seen = Volatile.Read(ref _maxConcurrent)) < active
                        && Interlocked.CompareExchange(ref _maxConcurrent, active, seen) != seen
                    )
                    {
                        // Loop until we successfully update the max concurrent count
                    }

                    if (active >= 2)
                        _overlap.TrySetResult();

                    if (_waitForOverlap)
                        await _overlap.Task.WaitAsync(TimeSpan.FromSeconds(10), _shutdown.Token);
                }

                byte[] headers = Encoding.ASCII.GetBytes(
                    $"HTTP/1.1 206 Partial Content\r\nContent-Range: bytes {start}-{end}/{_archive.Length}\r\n"
                        + $"Content-Length: {size}\r\nETag: \"fixture\"\r\nConnection: close\r\n\r\n"
                );
                await network.WriteAsync(headers, _shutdown.Token);
                await network.WriteAsync(_archive.AsMemory((int)start, size), _shutdown.Token);
            }
            catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
            {
                // Ignore cancellation during shutdown
            }
            catch (Exception ex)
            {
                _errors.Enqueue(ex);
            }
            finally
            {
                if (observed)
                    Interlocked.Decrement(ref _active);
            }
        }
    }
}
