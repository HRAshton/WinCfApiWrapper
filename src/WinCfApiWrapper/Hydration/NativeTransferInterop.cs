using System.Runtime.InteropServices;
using WinCfApiWrapper.Interop;
using Windows.Win32.Foundation;
using Windows.Win32.Storage.CloudFilters;

namespace WinCfApiWrapper.Hydration;

internal static class NativeTransferInterop
{
    internal delegate void ExecuteOperation(
        in CF_OPERATION_INFO info,
        ref CF_OPERATION_PARAMETERS parameters
    );

    internal static void Execute(CF_CALLBACK_INFO info, TransferCall transfer) =>
        Execute(info, transfer, CloudApi.Execute);

    internal static unsafe void Execute(
        CF_CALLBACK_INFO info,
        TransferCall transfer,
        ExecuteOperation execute
    )
    {
        var operation = new CF_OPERATION_INFO
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

        execute(in operation, ref parameters);
    }
}
