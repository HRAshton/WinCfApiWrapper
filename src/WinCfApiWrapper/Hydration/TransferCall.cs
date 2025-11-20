namespace WinCfApiWrapper.Hydration;

// Transfer fields kept independent of native layout so hydration can be tested without CFAPI.
internal readonly record struct TransferCall(
    nint Buffer,
    long Offset,
    long Length,
    int CompletionStatus
);
