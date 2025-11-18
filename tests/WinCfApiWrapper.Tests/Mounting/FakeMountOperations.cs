using Microsoft.Win32.SafeHandles;
using WinCfApiWrapper.Mounting;
using WinCfApiWrapper.Public;
using Windows.Win32.Storage.CloudFilters;

namespace WinCfApiWrapper.Tests.Mounting;

internal sealed class FakeMountOperations(
    Action<string>? fault = null,
    Action? afterHydrate = null,
    TaskCompletionSource? hydrateStarted = null,
    TaskCompletionSource? releaseHydrate = null,
    bool honorHydrationCancellation = false,
    bool registered = false
) : IMountOperations
{
    private readonly List<string> _calls = [];
    private int _activeHydrates;
    private int _maxActiveHydrates;

    public IList<string> Calls => _calls;

    public int ActiveHydrates => Volatile.Read(ref _activeHydrates);

    public int MaxActiveHydrates => Volatile.Read(ref _maxActiveHydrates);

    public uint[]? CallbackTypes { get; private set; }

    public bool Registered { get; private set; } = registered;

    public CF_CALLBACK_REGISTRATION[]? Callbacks { get; private set; }

    public TaskCompletionSource? HydrateStarted => hydrateStarted;

    public TaskCompletionSource? ReleaseHydrate => releaseHydrate;

    public Action<string>? Fault { get; set; } = fault;

    public Action? AfterHydrate => afterHydrate;

    public void Register(string root)
    {
        Step("register");
        if (Registered)
            throw new IOException("Already registered");

        Registered = true;
    }

    public void Unregister(string root)
    {
        Step("unregister");
        Registered = false;
    }

    public long Connect(string root, CF_CALLBACK_REGISTRATION[] callbacks)
    {
        Step("connect");
        if (!Registered)
            throw new IOException("Not registered");

        CallbackTypes = callbacks.Select(callback => unchecked((uint)callback.Type)).ToArray();
        Callbacks = callbacks;

        return 1;
    }

    public void Disconnect(long key)
    {
        Step("disconnect");
    }

    public void CreatePlaceholder(string parent, string fileName, ContainerEntry entry)
    {
        Step("placeholder");
        File.WriteAllText(Path.Combine(parent, fileName), "placeholder");
        Step("placeholder-created");
    }

    public void ValidatePlaceholder(string path, ContainerEntry entry)
    {
        Step("validate");
        if (!File.Exists(path))
            throw new IOException("Missing placeholder");
    }

    public bool IsRegistered(string root)
    {
        return Registered;
    }

    public async Task HydrateAsync(SafeFileHandle handle, CancellationToken cancellationToken)
    {
        int active = Interlocked.Increment(ref _activeHydrates);
        try
        {
            int observed;
            while (
                (observed = Volatile.Read(ref _maxActiveHydrates)) < active
                && Interlocked.CompareExchange(ref _maxActiveHydrates, active, observed) != observed
            )
            {
                // Spin until we successfully update the max active hydrates
            }

            Step("hydrate");
            hydrateStarted?.TrySetResult();
            if (releaseHydrate is not null)
            {
                if (honorHydrationCancellation)
                    await releaseHydrate.Task.WaitAsync(cancellationToken);
                else
                    await releaseHydrate.Task;
            }

            afterHydrate?.Invoke();
        }
        finally
        {
            Interlocked.Decrement(ref _activeHydrates);
        }
    }

    private void Step(string name)
    {
        _calls.Add(name);
        Fault?.Invoke(name);
    }
}
