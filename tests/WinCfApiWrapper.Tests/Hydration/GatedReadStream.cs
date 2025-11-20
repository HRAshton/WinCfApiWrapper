namespace WinCfApiWrapper.Tests.Hydration;

internal sealed class GatedReadStream(byte[] data) : MemoryStream(data)
{
    public TaskCompletionSource Started { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource Released { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public bool Canceled { get; private set; }

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default
    )
    {
        Started.TrySetResult();
        try
        {
            await Released.Task.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            Canceled = true;
            throw;
        }

        return await base.ReadAsync(buffer, cancellationToken);
    }
}
