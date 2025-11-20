using System.Runtime.InteropServices;
using WinCfApiWrapper.Interop;
using Windows.Win32.Foundation;
using Windows.Win32.Storage.CloudFilters;

namespace WinCfApiWrapper.Hydration;

internal sealed class NativeTransfer(Action<CF_CALLBACK_INFO, TransferCall>? execute = null)
{
    internal const int Alignment = 4096;

    internal const int BufferSize = 64 * 1024;

    // ntstatus.h: STATUS_CLOUD_FILE_UNSUCCESSFUL (the old CF00 value means corrupt sync-root metadata).
    internal const int StatusCloudFileUnsuccessful = unchecked((int)0xC000CF12);
    private readonly Action<CF_CALLBACK_INFO, TransferCall> _execute = execute ?? ExecuteNative;

    public bool Complete(FetchRequest request, byte[] buffer, int size, long offset)
    {
        if (size > buffer.Length)
            throw new InvalidDataException("Transfer buffer is shorter than the completed range.");

        TransferRange.ValidateExact(offset, size, request.LogicalLength);
        lock (request.Gate)
        {
            if (!request.IsPending(offset, size))
                return false;

            Execute(request, buffer, offset, size, 0);
            request.CompletedThrough = offset + size;
            request.Completed(offset, size);

            return true;
        }
    }

    public bool Fail(FetchRequest request)
    {
        lock (request.Gate)
        {
            if (!request.IsCanceled)
            {
                TransferRange.ForRequest(
                    request.RequiredFileOffset,
                    request.RequiredLength,
                    request.LogicalLength
                );
            }

            bool submitted = false;
            foreach (TransferRange range in request.Pending())
            {
                TransferRange.ValidateExact(range.Offset, range.Length, request.LogicalLength);
                Execute(request, null, range.Offset, range.Length, StatusCloudFileUnsuccessful);
                request.Completed(range.Offset, range.Length);
                submitted = true;
            }

            return submitted;
        }
    }

    private void Execute(FetchRequest request, byte[]? buffer, long offset, long length, int status)
    {
        GCHandle pin = default;
        try
        {
            if (buffer is not null)
                pin = GCHandle.Alloc(buffer, GCHandleType.Pinned);

            try
            {
                _execute(
                    request.Info,
                    new TransferCall(
                        pin.IsAllocated ? pin.AddrOfPinnedObject() : nint.Zero,
                        offset,
                        length,
                        status
                    )
                );
            }
            catch
            {
                request.NativeCompletionFailed = true;
                throw;
            }
        }
        finally
        {
            if (pin.IsAllocated)
                pin.Free();
        }
    }

    private static unsafe void ExecuteNative(CF_CALLBACK_INFO info, TransferCall transfer)
    {
        var op = new CF_OPERATION_INFO
        {
            StructSize = (uint)sizeof(CF_OPERATION_INFO),
            Type = CF_OPERATION_TYPE.CF_OPERATION_TYPE_TRANSFER_DATA,
            ConnectionKey = info.ConnectionKey,
            TransferKey = info.TransferKey,
            RequestKey = info.RequestKey,
        };
        var parameters = new CF_OPERATION_PARAMETERS
        {
            ParamSize = (uint)(
                Marshal
                    .OffsetOf<CF_OPERATION_PARAMETERS>(nameof(CF_OPERATION_PARAMETERS.Anonymous))
                    .ToInt32()
                + sizeof(CF_OPERATION_PARAMETERS._Anonymous_e__Union._TransferData_e__Struct)
            ),
        };

        parameters.TransferData.CompletionStatus = new NTSTATUS(transfer.CompletionStatus);
        parameters.TransferData.Buffer = (void*)transfer.Buffer;
        parameters.TransferData.Offset = transfer.Offset;
        parameters.TransferData.Length = transfer.Length;

        CloudApi.Execute(in op, ref parameters);
    }
}
