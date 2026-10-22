using System.Buffers.Binary;

namespace WinCfApiWrapper.Archives;

// Retain each central record once. A one-entry virtual directory lets ZipArchive own
// ZIP decoding (including ZIP64) without downloading or parsing every other entry.
internal sealed class ZipDirectory
{
    private readonly Stream _source;
    private long _position;

    public ZipDirectory(Stream source)
    {
        _source = source;
        byte[] tail = new byte[(int)Math.Min(source.Length, 65557)];
        source.Position = source.Length - tail.Length;
        source.ReadExactly(tail);
        int end = tail.Length - 22;
        while (end >= 0)
        {
            // Match ZipArchive's backward signature search, including trailing bytes after the comment.
            if (BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(end)) == 0x06054b50)
                break;
            end--;
        }

        if (end < 0)
            throw new InvalidDataException("ZIP end record is missing.");
        _position = BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(end + 16));
        if (_position == uint.MaxValue)
        {
            long locator = source.Length - tail.Length + end - 20;
            source.Position = locator;
            byte[] record = new byte[20];
            source.ReadExactly(record);
            if (BinaryPrimitives.ReadUInt32LittleEndian(record) != 0x07064b50)
                throw new InvalidDataException("ZIP64 locator is missing.");
            source.Position = checked(
                (long)BinaryPrimitives.ReadUInt64LittleEndian(record.AsSpan(8))
            );
            record = new byte[56];
            source.ReadExactly(record);
            if (BinaryPrimitives.ReadUInt32LittleEndian(record) != 0x06064b50)
                throw new InvalidDataException("ZIP64 end record is missing.");
            _position = checked((long)BinaryPrimitives.ReadUInt64LittleEndian(record.AsSpan(48)));
        }
    }

    public (long Offset, byte[] Record) ReadEntry(long? remainingMetadataBytes = null)
    {
        _source.Position = _position;
        byte[] header = new byte[46];
        _source.ReadExactly(header);
        if (BinaryPrimitives.ReadUInt32LittleEndian(header) != 0x02014b50)
            throw new InvalidDataException("ZIP central record is missing.");
        int size =
            46
            + BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(28))
            + BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(30))
            + BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(32));
        if (remainingMetadataBytes is { } remaining && size > remaining)
        {
            throw new InvalidDataException(
                "ZIP central-directory metadata exceeds the configured byte limit."
            );
        }

        byte[] record = new byte[size];
        header.CopyTo(record, 0);
        _source.ReadExactly(record.AsSpan(46));
        long offset = _position;
        _position = checked(_position + size);
        return (offset, record);
    }

    public static Stream OpenEntry(Stream source, ZipEntryInfo entry, bool validate)
    {
        try
        {
            byte[] record = entry.DirectoryRecord!;
            if (source.Length != entry.ArchiveLength)
                throw new IOException($"Archive length changed: {entry.FullName}");
            if (validate)
            {
                source.Position = entry.DirectoryOffset;
                byte[] actual = new byte[record.Length];
                source.ReadExactly(actual);

                // DOS timestamps can vary when a factory regenerates identical ZIP contents.
                // Content identity still includes names, sizes, CRC, flags, and offsets.
                if (
                    !actual.AsSpan(0, 12).SequenceEqual(record.AsSpan(0, 12))
                    || !actual.AsSpan(16).SequenceEqual(record.AsSpan(16))
                )
                    throw new IOException($"Archive entry changed: {entry.FullName}");
            }

            using var tail = new MemoryStream();
            using var writer = new BinaryWriter(tail, System.Text.Encoding.UTF8, true);

            // .NET may scan a whole page backwards for the end record. Keep that
            // scan in memory rather than fetching unrelated trailing file contents.
            const int padding = 4096;
            writer.Write(new byte[padding]);
            writer.Write(record);
            writer.Write(0x06064b50u);
            writer.Write(44L);
            writer.Write((ushort)45);
            writer.Write((ushort)45);
            writer.Write(0u);
            writer.Write(0u);
            writer.Write(1L);
            writer.Write(1L);
            writer.Write((long)record.Length);
            writer.Write(checked(entry.ArchiveLength + padding));
            writer.Write(0x07064b50u);
            writer.Write(0u);
            writer.Write(checked(entry.ArchiveLength + padding + record.Length));
            writer.Write(1u);
            writer.Write(0x06054b50u);
            writer.Write((ushort)0);
            writer.Write((ushort)0);
            writer.Write(ushort.MaxValue);
            writer.Write(ushort.MaxValue);
            writer.Write(uint.MaxValue);
            writer.Write(uint.MaxValue);
            writer.Write((ushort)0);
            return new DirectoryStream(source, tail.ToArray());
        }
        catch
        {
            source.Dispose();
            throw;
        }
    }

    public static void CompleteDirectoryRead(Stream stream)
    {
        if (stream is DirectoryStream directory)
            directory.CompleteDirectoryRead();
    }

    private sealed class DirectoryStream(Stream source, byte[] tail) : Stream
    {
        private bool _directoryReadComplete;
        private long _position;

        public override bool CanRead => true;

        public override bool CanSeek => true;

        public override bool CanWrite => false;

        public override long Length =>
            _directoryReadComplete ? source.Length : checked(source.Length + tail.Length);

        public override long Position
        {
            get => _position;
            set =>
                _position =
                    value >= 0 ? value : throw new ArgumentOutOfRangeException(nameof(value));
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            Read(buffer.AsSpan(offset, count));

        public void CompleteDirectoryRead()
        {
            _directoryReadComplete = true;
        }

        public override int Read(Span<byte> buffer)
        {
            if (_position >= Length)
                return 0;
            int count;
            if (_position < source.Length)
            {
                source.Position = _position;
                count = source.Read(
                    buffer[..(int)Math.Min(buffer.Length, source.Length - _position)]
                );
            }
            else
            {
                count = (int)Math.Min(buffer.Length, Length - _position);
                tail.AsSpan((int)(_position - source.Length), count).CopyTo(buffer);
            }

            _position += count;
            return count;
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_position >= source.Length)
                return Read(buffer.Span);
            source.Position = _position;
            int count = await source
                .ReadAsync(
                    buffer[..(int)Math.Min(buffer.Length, source.Length - _position)],
                    cancellationToken
                )
                .ConfigureAwait(false);
            _position += count;
            return count;
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

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                source.Dispose();
            base.Dispose(disposing);
        }
    }
}
