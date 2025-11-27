using Microsoft.Win32.SafeHandles;
using WinCfApiWrapper.Mounting;
using WinCfApiWrapper.Public;
using Windows.Win32.Storage.CloudFilters;

namespace WinCfApiWrapper.IntegrationTests;

internal sealed class FaultingNativeMountOperations : IMountOperations
{
    private readonly bool _failUnregister;
    private readonly NativeMountOperations _native = new();
    private int _placeholders;

    public FaultingNativeMountOperations(bool failUnregister = false)
    {
        _failUnregister = failUnregister;
    }

    public void Register(string root)
    {
        _native.Register(root);
    }

    public void Unregister(string root)
    {
        if (_failUnregister)
            throw new IOException("Injected native unregistration failure.");

        _native.Unregister(root);
    }

    public long Connect(string root, CF_CALLBACK_REGISTRATION[] callbacks)
    {
        return _native.Connect(root, callbacks);
    }

    public void Disconnect(long key)
    {
        _native.Disconnect(key);
    }

    public void CreatePlaceholder(string parent, string fileName, ContainerEntry entry)
    {
        _native.CreatePlaceholder(parent, fileName, entry);
        if (Interlocked.Increment(ref _placeholders) == 1)
        {
            throw new IOException(
                "Injected failure after the first native placeholder was created."
            );
        }
    }

    public void ValidatePlaceholder(string path, ContainerEntry entry)
    {
        _native.ValidatePlaceholder(path, entry);
    }

    public bool IsRegistered(string root)
    {
        return _native.IsRegistered(root);
    }

    public Task HydrateAsync(SafeFileHandle handle, CancellationToken cancellationToken)
    {
        return _native.HydrateAsync(handle, cancellationToken);
    }
}
