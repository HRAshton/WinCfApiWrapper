namespace WinCfApiWrapper.Containers;

internal static class ContainerStreamSource
{
    public static Stream OpenReadable(
        Func<CancellationToken, Stream> openStream,
        CancellationToken cancellationToken,
        string format
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        Stream stream =
            openStream(cancellationToken)
            ?? throw new InvalidDataException($"{format} factory returned no stream.");
        try
        {
            if (!stream.CanRead)
                throw new InvalidDataException($"{format} stream must be readable.");
            if (stream.CanSeek)
                stream.Position = 0;
            cancellationToken.ThrowIfCancellationRequested();
            return stream;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }
}
