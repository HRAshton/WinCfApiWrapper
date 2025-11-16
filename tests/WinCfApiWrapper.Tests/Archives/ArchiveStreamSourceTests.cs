using WinCfApiWrapper.Archives;
using Xunit;

namespace WinCfApiWrapper.Tests.Archives;

public sealed class ArchiveStreamSourceTests
{
    [Fact]
    public void OpenRead_RequestsIndependentStreamForEachCall()
    {
        int calls = 0;
        var source = new ArchiveStreamSource(_ =>
        {
            calls++;

            return new MemoryStream([1, 2, 3]);
        });

        using Stream first = source.OpenRead();
        using Stream second = source.OpenRead();

        Assert.NotSame(first, second);
        Assert.Equal(2, calls);
    }

    [Fact]
    public void OpenRead_ResetsReturnedStreamToArchiveStart()
    {
        var stream = new MemoryStream([1, 2, 3]);
        stream.Position = 2;
        var source = new ArchiveStreamSource(_ => stream);

        using Stream opened = source.OpenRead();

        Assert.Equal(0, opened.Position);
        Assert.Equal(1, opened.ReadByte());
    }

    [Fact]
    public void OpenRead_NonSeekableStream_DisposesAndRejectsIt()
    {
        var stream = new NonSeekableStream();
        var source = new ArchiveStreamSource(_ => stream);

        Assert.Throws<InvalidDataException>(() => source.OpenRead());
        Assert.False(stream.CanRead);
    }

    [Fact]
    public void OpenRead_CanceledBeforeFactory_DoesNotOpenStream()
    {
        int calls = 0;
        var source = new ArchiveStreamSource(_ =>
        {
            calls++;

            return new MemoryStream();
        });
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() => source.OpenRead(cancellation.Token));
        Assert.Equal(0, calls);
    }

    [Fact]
    public void OpenRead_FactoryCancelsDuringOpen_DisposesReturnedStream()
    {
        using var cancellation = new CancellationTokenSource();
        var stream = new MemoryStream([1]);
        var source = new ArchiveStreamSource(_ =>
        {
            // The factory is invoked synchronously inside OpenRead, before this source is disposed.
            // ReSharper disable once AccessToDisposedClosure
            cancellation.Cancel();

            return stream;
        });

        Assert.Throws<OperationCanceledException>(() => source.OpenRead(cancellation.Token));
        Assert.False(stream.CanRead);
    }

    [Fact]
    public void OpenRead_FactoryReturnsNull_RejectsIt()
    {
        var source = new ArchiveStreamSource(_ => null!);

        Assert.Throws<InvalidDataException>(() => source.OpenRead());
    }

    private sealed class NonSeekableStream : MemoryStream
    {
        public override bool CanSeek => false;
    }
}
