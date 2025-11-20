namespace WinCfApiWrapper.Hydration;

internal readonly record struct TransferRange(long Offset, long Length)
{
    public static TransferRange ForRequest(long offset, long length, long fileLength)
    {
        bool requestIsInvalid =
            fileLength <= 0
            || offset < 0
            || length <= 0
            || offset >= fileLength
            || length > fileLength - offset;
        if (requestIsInvalid)
            throw new InvalidDataException("FETCH_DATA range is outside the logical file.");

        long start = offset / NativeTransfer.Alignment * NativeTransfer.Alignment;
        long requestedEnd = offset + length; // subtraction above excludes overflow
        long padding =
            (NativeTransfer.Alignment - (requestedEnd % NativeTransfer.Alignment))
            % NativeTransfer.Alignment;
        long end = padding >= fileLength - requestedEnd ? fileLength : requestedEnd + padding;

        return new TransferRange(start, end - start);
    }

    public static void ValidateExact(long offset, long length, long fileLength)
    {
        bool rangeIsInvalid =
            fileLength <= 0
            || offset < 0
            || length <= 0
            || offset % NativeTransfer.Alignment != 0
            || offset >= fileLength
            || length > fileLength - offset
            || (length % NativeTransfer.Alignment != 0 && offset + length != fileLength);
        if (rangeIsInvalid)
        {
            throw new InvalidDataException(
                "CFAPI transfer range is not aligned or exceeds logical EOF."
            );
        }
    }
}
