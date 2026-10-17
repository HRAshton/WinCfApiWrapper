using WinCfApiWrapper.Containers;
using Xunit;

namespace WinCfApiWrapper.Tests.Archives;

public sealed class ContainerStreamSourceTests
{
    [Fact]
    public void OpenReadable_NullFactoryResultIsRejected()
    {
        Assert.Throws<InvalidDataException>(() =>
            ContainerStreamSource.OpenReadable(_ => null!, default, "Compressed")
        );
    }

    [Fact]
    public void OpenReadable_UnreadableStreamIsDisposedAndRejected()
    {
        var stream = new UnreadableStream();

        Assert.Throws<InvalidDataException>(() =>
            ContainerStreamSource.OpenReadable(_ => stream, default, "Compressed")
        );

        Assert.True(stream.WasDisposed);
    }

    [Fact]
    public void OpenReadable_NonSeekableStreamCanBeConsumedFromItsCurrentPosition()
    {
        var stream = new NonSeekableStream([1, 2, 3]);

        using Stream opened = ContainerStreamSource.OpenReadable(
            _ => stream,
            default,
            "Compressed"
        );

        Assert.Equal(1, opened.ReadByte());
    }

    [Fact]
    public void OpenReadable_CancellationDuringOpenDisposesTheReturnedStream()
    {
        using var cancellation = new CancellationTokenSource();
        var stream = new TrackingStream([1, 2, 3]);

        Assert.Throws<OperationCanceledException>(() =>
            ContainerStreamSource.OpenReadable(
                _ =>
                {
                    cancellation.Cancel();
                    return stream;
                },
                cancellation.Token,
                "Compressed"
            )
        );

        Assert.True(stream.WasDisposed);
    }

    private sealed class UnreadableStream : MemoryStream
    {
        public bool WasDisposed { get; private set; }

        public override bool CanRead => false;

        protected override void Dispose(bool disposing)
        {
            WasDisposed |= disposing;
            base.Dispose(disposing);
        }
    }

    private sealed class NonSeekableStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
    }

    private sealed class TrackingStream(byte[] bytes) : MemoryStream(bytes)
    {
        public bool WasDisposed { get; private set; }

        protected override void Dispose(bool disposing)
        {
            WasDisposed |= disposing;
            base.Dispose(disposing);
        }
    }
}
