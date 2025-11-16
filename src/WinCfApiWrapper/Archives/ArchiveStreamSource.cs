namespace WinCfApiWrapper.Archives;

internal sealed class ArchiveStreamSource(Func<CancellationToken, Stream> openArchive)
{
    public Stream OpenRead(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Stream stream =
            openArchive(cancellationToken)
            ?? throw new InvalidDataException("Archive factory returned no stream.");
        try
        {
            if (!stream.CanRead || !stream.CanSeek)
            {
                throw new InvalidDataException(
                    "Archive factory must return a readable, seekable stream."
                );
            }

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
