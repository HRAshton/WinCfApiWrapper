using Xunit;

namespace WinCfApiWrapper.Tests.Archives;

public sealed class ReadTrackingStreamTests
{
    [Theory]
    [InlineData("Array", 2)]
    [InlineData("Span", 2)]
    [InlineData("ReadByte", 1)]
    [InlineData("ArrayAsync", 2)]
    [InlineData("MemoryAsync", 2)]
    public async Task ReadApis_CountDataOnce_AndIgnoreEmptyReadsAndEof(
        string api,
        int expectedBytes
    )
    {
        byte[] payload = [11, 22, 33, 44];
        using var stream = new ReadTrackingStream(payload);
        byte[] buffer = new byte[2];
        CancellationToken token = TestContext.Current.CancellationToken;

        Assert.Equal(expectedBytes, await ReadUsingAsync(stream, api, buffer, token));
        Assert.Equal(payload[..expectedBytes], buffer[..expectedBytes]);
        Assert.Equal(expectedBytes, stream.BytesRead);
        Assert.Equal(1, stream.ReadCalls);

        if (api != "ReadByte")
            Assert.Equal(0, await ReadUsingAsync(stream, api, [], token));
        Assert.Equal(expectedBytes, stream.Position);
        Assert.Equal(expectedBytes, stream.BytesRead);
        Assert.Equal(1, stream.ReadCalls);

        stream.Position = stream.Length;
        Assert.Equal(0, await ReadUsingAsync(stream, api, buffer, token));
        Assert.Equal(expectedBytes, stream.BytesRead);
        Assert.Equal(1, stream.ReadCalls);
        stream.ResetCounts();
        Assert.Equal(0, stream.BytesRead);
        Assert.Equal(0, stream.ReadCalls);
        Assert.Equal(payload.Length, stream.Position);

        Assert.Equal(0, stream.Seek(0, SeekOrigin.Begin));
        Assert.Equal(expectedBytes, await ReadUsingAsync(stream, api, buffer, token));
        Assert.Equal(expectedBytes, stream.BytesRead);
        Assert.Equal(1, stream.ReadCalls);
    }

    [Theory]
    [InlineData("ArrayAsync")]
    [InlineData("MemoryAsync")]
    public async Task CanceledReads_DoNotConsumeOrCountBytes(string api)
    {
        using var stream = new ReadTrackingStream([11, 22, 33]);
        Assert.Equal(11, stream.ReadByte());
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await ReadUsingAsync(stream, api, new byte[2], canceled.Token)
        );

        Assert.Equal(1, stream.Position);
        Assert.Equal(1, stream.BytesRead);
        Assert.Equal(1, stream.ReadCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadExactlyAndCopyTo_CountUnderlyingReadsOnce(bool asynchronous)
    {
        byte[] payload = [11, 22, 33, 44];
        using var stream = new ReadTrackingStream(payload);
        byte[] buffer = new byte[payload.Length];
        CancellationToken token = TestContext.Current.CancellationToken;

        if (asynchronous)
            await stream.ReadExactlyAsync(buffer.AsMemory(), token);
        else
            stream.ReadExactly(buffer.AsSpan());

        Assert.Equal(payload, buffer);
        Assert.Equal(payload.Length, stream.BytesRead);
        Assert.Equal(1, stream.ReadCalls);
        stream.Position = 0;
        stream.ResetCounts();
        using var output = new MemoryStream();
        if (asynchronous)
            await stream.CopyToAsync(output, token);
        else
            stream.CopyTo(output);

        Assert.Equal(payload, output.ToArray());
        Assert.Equal(payload.Length, stream.BytesRead);
        Assert.Equal(1, stream.ReadCalls);
    }

    [Fact]
    public void InvalidReads_DoNotChangeCounters_AndDisposalClosesTheWrapper()
    {
        using var stream = new ReadTrackingStream([11, 22]);
        Assert.True(stream.CanRead);
        Assert.True(stream.CanSeek);
        Assert.False(stream.CanWrite);
        Assert.ThrowsAny<ArgumentException>(() => stream.Read(new byte[1], 0, 2));
        Assert.Equal(0, stream.BytesRead);
        Assert.Equal(0, stream.ReadCalls);
        Assert.Equal(0, stream.Position);
        stream.Flush();
        Assert.Throws<NotSupportedException>(() => stream.Write([1], 0, 1));
        Assert.Throws<NotSupportedException>(() => stream.SetLength(0));

        stream.Dispose();

        Assert.False(stream.CanRead);
        Assert.False(stream.CanSeek);
        Assert.Throws<ObjectDisposedException>(() => stream.ReadByte());
        Assert.Equal(0, stream.BytesRead);
        Assert.Equal(0, stream.ReadCalls);
    }

    private static ValueTask<int> ReadUsingAsync(
        ReadTrackingStream stream,
        string api,
        byte[] buffer,
        CancellationToken token
    )
    {
        switch (api)
        {
            case "Array":
                return ValueTask.FromResult(stream.Read(buffer, 0, buffer.Length));
            case "Span":
                return ValueTask.FromResult(stream.Read(buffer.AsSpan()));
            case "ReadByte":
                int value = stream.ReadByte();
                if (value < 0)
                    return ValueTask.FromResult(0);
                buffer[0] = (byte)value;
                return ValueTask.FromResult(1);
            case "ArrayAsync":
                return new ValueTask<int>(stream.ReadAsync(buffer, 0, buffer.Length, token));
            case "MemoryAsync":
                return stream.ReadAsync(buffer.AsMemory(), token);
            default:
                throw new ArgumentOutOfRangeException(nameof(api));
        }
    }
}
