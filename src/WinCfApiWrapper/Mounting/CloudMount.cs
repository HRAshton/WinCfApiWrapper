using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32.SafeHandles;
using WinCfApiWrapper.Containers;
using WinCfApiWrapper.Hydration;
using WinCfApiWrapper.Public;
using Windows.Win32.Storage.CloudFilters;

namespace WinCfApiWrapper.Mounting;

internal sealed class CloudMount : ICloudMount
{
    private readonly HashSet<Task> _activeHydrations = [];
    private readonly string _basePath;
    private readonly CF_CALLBACK _cancelFetchCallback;
    private readonly IContentContainer _container;
    private readonly CF_CALLBACK _fetchCallback;

    // Protected by _hydrationLock. A slot is kept only while at least one
    // hydration owns or is waiting for it, preventing split-key semaphore races.
    private readonly Dictionary<string, FileHydrationSlot> _fileHydrationSlots = new(
        StringComparer.OrdinalIgnoreCase
    );

    private readonly object _hydrationLock = new();
    private readonly SemaphoreSlim _hydrationSlots;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly ILogger<CloudMount> _logger;
    private readonly Func<ContainerEntry, Stream>? _openEntry;
    private readonly IMountOperations _operations;
    private readonly CloudMountOptions _options;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly NativeTransfer? _transfer;
    private ContainerCatalog? _catalog;
    private GCHandle _callbackRoot;
    private bool _connected;
    private long _connectionKey;
    private FetchCoordinator? _coordinator;
    private bool _disposed;
    private bool _registrationFailed;
    private string? _root;
    private bool _rootCreatedByAttempt;
    private volatile MountState _state = MountState.Created;

    internal CloudMount(CloudMountOptions options)
        : this(options, new NativeMountOperations(), NullLogger<CloudMount>.Instance) { }

    internal CloudMount(CloudMountOptions options, ILogger<CloudMount> logger)
        : this(options, new NativeMountOperations(), logger) { }

    internal CloudMount(
        CloudMountOptions options,
        IMountOperations operations,
        NativeTransfer? transfer = null,
        Func<ContainerEntry, Stream>? openEntry = null
    )
        : this(options, operations, NullLogger<CloudMount>.Instance, transfer, openEntry) { }

    internal unsafe CloudMount(
        CloudMountOptions options,
        IMountOperations operations,
        ILogger<CloudMount> logger,
        NativeTransfer? transfer = null,
        Func<ContainerEntry, Stream>? openEntry = null
    )
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 16299))
        {
            throw new PlatformNotSupportedException(
                "Cloud Files API requires Windows 10 version 1709 or later."
            );
        }

        _options = options ?? throw new ArgumentNullException(nameof(options));
        _operations = operations ?? throw new ArgumentNullException(nameof(operations));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Root);
        ArgumentNullException.ThrowIfNull(options.Container);
        if (options.SkipResumeValidation && options.TrustedContentId is null)
        {
            throw new ArgumentException(
                "Skipping resume validation requires a trusted content ID.",
                nameof(options)
            );
        }

        bool concurrencyOptionsAreInvalid =
            options.FetchWorkers <= 0
            || options.FetchQueueCapacity <= 0
            || options.BulkHydrationConcurrency <= 0;
        if (concurrencyOptionsAreInvalid)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "Concurrency and capacity must be positive."
            );
        }

        if (
            options.ShutdownTimeout is { } shutdownTimeout
            && (
                shutdownTimeout <= TimeSpan.Zero
                || shutdownTimeout > TimeSpan.FromMilliseconds(uint.MaxValue - 1)
            )
        )
        {
            throw new ArgumentOutOfRangeException(
                nameof(options.ShutdownTimeout),
                shutdownTimeout,
                "ShutdownTimeout must be a positive finite wait or null for unlimited."
            );
        }

        _basePath = Path.GetFullPath(options.Root);
        bool basePathIsVolumeRoot = string.Equals(
            Path.TrimEndingDirectorySeparator(_basePath),
            Path.GetPathRoot(_basePath),
            StringComparison.OrdinalIgnoreCase
        );
        if (basePathIsVolumeRoot)
        {
            throw new ArgumentException(
                "Mount base path must name a folder, not a volume root.",
                nameof(options)
            );
        }

        _container = options.Container;
        _hydrationSlots = new SemaphoreSlim(options.BulkHydrationConcurrency);
        _transfer = transfer;
        _openEntry = openEntry;
        _fetchCallback = OnFetch;
        _cancelFetchCallback = OnCancelFetch;
    }

    public event EventHandler<MountErrorEventArgs>? Error;

    public string Root =>
        _root
        ?? throw new InvalidOperationException(
            "Mount path is available after the container is indexed."
        );

    public MountState State => _state;

    public MountStatistics Statistics => _coordinator?.Statistics ?? default;

    // Internal observation points for lifetime/regression tests.
    internal bool CallbacksRooted => _callbackRoot.IsAllocated;

    internal int ActiveFileHydrationSlotCount
    {
        get
        {
            lock (_hydrationLock)
                return _fileHydrationSlots.Count;
        }
    }

    public async Task MountAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (_state != MountState.Created)
                throw new InvalidOperationException("This instance can only be mounted once.");

            _state = MountState.Mounting;
            _logger.LogInformation("Mounting content source at base path {BasePath}", _basePath);
            try
            {
                ReportProgress(new OperationProgress("Indexing", 0));
                IReadOnlyCollection<ContainerEntry> entries = await _container
                    .GetEntriesAsync(cancellationToken)
                    .ConfigureAwait(false);
                _catalog = await Task.Run(
                        () => new ContainerCatalog(entries, _options.TrustedContentId),
                        cancellationToken
                    )
                    .ConfigureAwait(false);
                _root = MountPath.For(_basePath, _catalog.Fingerprint);
                await Task.Run(() => SetupNative(cancellationToken), cancellationToken)
                    .ConfigureAwait(false);
                _state = MountState.Mounted;
                ReportProgress(new OperationProgress("Mounted", 1, 1));
                _logger.LogInformation(
                    "Mount ready at {Root} with {EntryCount} files",
                    Root,
                    _catalog.Entries.Count
                );
            }
            catch (Exception startupError)
            {
                var cleanupErrors = new List<Exception>();
                try
                {
                    await CleanupAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    cleanupErrors.Add(new IOException("Startup resource cleanup failed.", ex));
                }

                if (!_connected && _rootCreatedByAttempt)
                    RollbackNew(cleanupErrors);

                _state = MountState.Faulted;
                bool startupWasCanceled =
                    startupError is OperationCanceledException
                    && cancellationToken.IsCancellationRequested
                    && cleanupErrors.Count == 0;
                if (startupWasCanceled)
                {
                    _logger.LogInformation("Mount startup canceled at {Root}", _root ?? _basePath);
                }
                else
                {
                    _logger.LogError(
                        startupError,
                        "Mount startup failed at {Root}; {CleanupFailureCount} cleanup errors",
                        _root ?? _basePath,
                        cleanupErrors.Count
                    );
                }

                if (cleanupErrors.Count != 0)
                {
                    cleanupErrors.Insert(0, startupError);
                    throw new AggregateException(
                        "Mount startup failed and rollback was incomplete.",
                        cleanupErrors
                    );
                }

                throw;
            }
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public bool Contains(string relativePath)
    {
        return GetMountedCatalog().Contains(relativePath);
    }

    public string Resolve(string relativePath)
    {
        string name = ContainerPath.Normalize(relativePath);

        return Path.Combine(Root, name);
    }

    public Task HydrateAsync(string relativePath, CancellationToken cancellationToken = default)
    {
        ContainerCatalog catalog = GetMountedCatalog();
        string name = ContainerPath.Normalize(relativePath);
        if (!catalog.Contains(name))
            throw new FileNotFoundException("No such container file", name);

        return HydrateFileAsync(name, cancellationToken);
    }

    public async Task HydrateAllAsync(
        IProgress<HydrationProgress>? progress = null,
        CancellationToken cancellationToken = default
    )
    {
        string[] paths = GetMountedCatalog().Entries.Keys.ToArray();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _shutdown.Token
        );
        CancellationToken linkedToken = linked.Token;
        int done = 0;
        int failed = 0;
        var errors = new ConcurrentBag<Exception>();
        await Parallel
            .ForEachAsync(
                paths,
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = _options.BulkHydrationConcurrency,
                    CancellationToken = linked.Token,
                },
                async (path, token) =>
                {
                    try
                    {
                        token.ThrowIfCancellationRequested();
                        await HydrateFileAsync(path, token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        if (_state != MountState.Mounted)
                        {
                            throw new OperationCanceledException(
                                "Mount stopped during bulk hydration.",
                                ex,
                                linkedToken
                            );
                        }

                        token.ThrowIfCancellationRequested();
                        Interlocked.Increment(ref failed);
                        errors.Add(new IOException($"Hydration failed: {path}", ex));
                    }
                    finally
                    {
                        int completed = Interlocked.Increment(ref done);
                        try
                        {
                            progress?.Report(
                                new HydrationProgress(
                                    completed,
                                    paths.Length,
                                    Volatile.Read(ref failed)
                                )
                            );
                        }
                        catch (Exception ex)
                        {
                            // Observer failures must not interrupt remaining files or
                            // replace cancellation and per-file hydration errors.
                            _logger.LogWarning(
                                ex,
                                "Bulk hydration progress report failed synchronously"
                            );
                        }
                    }
                }
            )
            .ConfigureAwait(false);
        if (_state != MountState.Mounted)
        {
            throw new OperationCanceledException(
                "Mount stopped during bulk hydration.",
                linked.Token
            );
        }

        linked.Token.ThrowIfCancellationRequested();
        if (!errors.IsEmpty)
        {
            _logger.LogWarning(
                "Bulk hydration failed for {FailedCount} of {TotalCount} files at {Root}",
                failed,
                paths.Length,
                Root
            );
            throw new AggregateException("Some files could not be hydrated.", errors);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
                return;

            _disposed = true;
            _state = MountState.Stopping;
            ReportProgress(new OperationProgress("Stopping", 0));
            _logger.LogInformation("Stopping mount at {Root}", _root ?? _basePath);
            try
            {
                await CleanupAsync().ConfigureAwait(false);
                _state = MountState.Stopped;
                ReportProgress(new OperationProgress("Stopped", 1, 1));
                _logger.LogInformation("Mount stopped at {Root}", _root ?? _basePath);
            }
            catch (Exception ex)
            {
                _state = MountState.Faulted;
                _disposed = false; // allow a later retry if native disconnect failed
                _logger.LogError(ex, "Mount shutdown failed at {Root}", _root ?? _basePath);
                throw;
            }
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    private void SetupNative(CancellationToken ct)
    {
        ContainerCatalog catalog = _catalog!;
        bool resume = Directory.Exists(Root);
        long total = catalog.Directories.Count + catalog.Entries.Count;
        long completed = 0;
        string stage = resume ? "Validating" : "CreatingPlaceholders";
        var progress = new ProgressThrottle();
        void ReportSetupProgress(string? path = null)
        {
            if (_options.Progress is not null && progress.ShouldReport(completed, total))
                ReportProgress(new OperationProgress(stage, completed, total, path));
        }

        ReportSetupProgress();
        _logger.LogInformation(
            "{MountAction} sync root {Root}",
            resume ? "Resuming" : "Creating",
            Root
        );
        if (resume)
        {
            if (!_operations.IsRegistered(Root))
                throw new IOException($"Mount folder is not registered: {Root}");

            foreach (string directory in catalog.Directories)
            {
                ct.ThrowIfCancellationRequested();
                string path = Path.Combine(Root, directory);
                if (!IsValidResumeDirectory(path))
                    throw new IOException($"Mount directory is missing or invalid: {path}");
                completed++;
                ReportSetupProgress(directory);
            }

            foreach (ContainerEntry entry in catalog.Entries.Values)
            {
                ct.ThrowIfCancellationRequested();
                string file = Path.Combine(Root, entry.RelativePath);
                if (!_options.SkipResumeValidation)
                    _operations.ValidatePlaceholder(file, entry);
                completed++;
                ReportSetupProgress(entry.RelativePath);
            }
        }
        else
        {
            if (File.Exists(Root))
                throw new IOException("Mount path is a file.");

            Directory.CreateDirectory(Root);
            _rootCreatedByAttempt = true;
        }

        ct.ThrowIfCancellationRequested();
        if (!resume)
        {
            try
            {
                _operations.Register(Root);
            }
            catch
            {
                _registrationFailed = true;
                throw;
            }
        }

        _coordinator = new FetchCoordinator(
            catalog,
            _container,
            _options.FetchWorkers,
            _options.FetchQueueCapacity,
            ReportBackgroundError,
            _transfer,
            _openEntry,
            _options.Progress is null ? null : ReportProgress
        );
        CF_CALLBACK_REGISTRATION[] registrations =
        [
            new()
            {
                Type = CF_CALLBACK_TYPE.CF_CALLBACK_TYPE_FETCH_DATA,
                Callback = _fetchCallback,
            },
            new()
            {
                Type = CF_CALLBACK_TYPE.CF_CALLBACK_TYPE_CANCEL_FETCH_DATA,
                Callback = _cancelFetchCallback,
            },
            new() { Type = CF_CALLBACK_TYPE.CF_CALLBACK_TYPE_NONE, Callback = null! },
        ];
        // CFAPI retains unmanaged callback pointers, not managed delegate references.
        // Root the mount (and its delegates/coordinator) before native connection setup.
        // The root is released only after a successful native disconnect.
        _callbackRoot = GCHandle.Alloc(this);
        try
        {
            _connectionKey = _operations.Connect(Root, registrations);
            _connected = true;
        }
        catch
        {
            // Connect did not return a valid native connection key to disconnect.
            _callbackRoot.Free();
            throw;
        }

        if (!resume)
        {
            foreach (string directory in catalog.Directories.OrderBy(x => x.Count(c => c == '\\')))
            {
                ct.ThrowIfCancellationRequested();
                string path = Path.Combine(Root, directory);
                Directory.CreateDirectory(path);
                completed++;
                ReportSetupProgress(directory);
            }

            foreach (ContainerEntry entry in catalog.Entries.Values)
            {
                ct.ThrowIfCancellationRequested();
                string parent = Path.GetDirectoryName(entry.RelativePath) is { Length: > 0 } dir
                    ? Path.Combine(Root, dir)
                    : Root;
                _operations.CreatePlaceholder(parent, Path.GetFileName(entry.RelativePath), entry);
                completed++;
                ReportSetupProgress(entry.RelativePath);
            }

            ct.ThrowIfCancellationRequested();
        }
    }

    // Query metadata only once per directory while retaining all existing
    // ancestor and reparse checks. Missing/inaccessible paths remain invalid.
    internal static bool IsValidResumeDirectory(string path)
    {
        try
        {
            FileAttributes attributes = File.GetAttributes(path);
            return (attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint))
                == FileAttributes.Directory;
        }
        catch (Exception ex) when (
            ex is IOException
                or UnauthorizedAccessException
                or System.Security.SecurityException
                or ArgumentException
                or NotSupportedException
        )
        {
            return false;
        }
    }

    private void RollbackNew(List<Exception> errors)
    {
        if (_rootCreatedByAttempt)
        {
            try
            {
                MountCleanup.Delete(Root, _operations, _registrationFailed, createdByThisAttempt: true);
            }
            catch (Exception ex)
            {
                errors.Add(
                    new IOException(
                        "Mount rollback is incomplete; fingerprinted folder retained.",
                        ex
                    )
                );
            }
        }
    }

    private unsafe void OnFetch(CF_CALLBACK_INFO* info, CF_CALLBACK_PARAMETERS* parameters)
    {
        try
        {
            _coordinator?.OnFetch((nint)info, (nint)parameters);
        }
        catch (Exception ex)
        {
            ReportBackgroundError(null, ex);
        }
    }

    private unsafe void OnCancelFetch(CF_CALLBACK_INFO* info, CF_CALLBACK_PARAMETERS* parameters)
    {
        try
        {
            _coordinator?.OnCancelFetch((nint)info, (nint)parameters);
        }
        catch (Exception ex)
        {
            ReportBackgroundError(null, ex);
        }
    }

    private void ReportBackgroundError(string? relativePath, Exception exception)
    {
        _logger.LogError(
            exception,
            "Background mount operation failed at {Root}, relative path {RelativePath}",
            _root ?? _basePath,
            relativePath
        );
        EventHandler<MountErrorEventArgs>? handlers = Error;
        if (handlers is null)
            return;

        var args = new MountErrorEventArgs(relativePath, exception);
        foreach (
            EventHandler<MountErrorEventArgs> handler in handlers
                .GetInvocationList()
                .Cast<EventHandler<MountErrorEventArgs>>()
        )
        {
            try
            {
                handler(this, args);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Mount error event handler failed at {Root}",
                    _root ?? _basePath
                );
            }
        }
    }

    private void ReportProgress(OperationProgress progress)
    {
        try
        {
            _options.Progress?.Report(progress);
        }
        catch (Exception ex)
        {
            // Only synchronous Report failures reach this catch. Progress<T>
            // callbacks posted to a UI context must handle their own exceptions.
            _logger.LogWarning(ex, "Progress.Report failed synchronously");
        }
    }

    // Track every holder and waiter before scheduling work. Keep the slot until
    // the last operation completes, including cancellation while waiting.
    private Task HydrateFileAsync(string name, CancellationToken token)
    {
        Task operation;
        FileHydrationSlot slot;
        lock (_hydrationLock)
        {
            if (_state != MountState.Mounted)
                throw new InvalidOperationException("Mount is no longer active.");

            if (!_fileHydrationSlots.TryGetValue(name, out FileHydrationSlot? cached))
            {
                cached = new FileHydrationSlot();
                _fileHydrationSlots.Add(name, cached);
            }

            slot = cached;
            slot.Users++;
            operation = HydrateCoreAsync(name, token, slot);
            _activeHydrations.Add(operation);
        }

        _ = operation.ContinueWith(
            completed =>
            {
                lock (_hydrationLock)
                    _activeHydrations.Remove(completed);
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default
        );

        return operation;
    }

    private sealed class FileHydrationSlot
    {
        public SemaphoreSlim Semaphore { get; } = new(1);

        public int Users { get; set; }
    }

    private async Task HydrateCoreAsync(
        string name,
        CancellationToken token,
        FileHydrationSlot slot
    )
    {
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                token,
                _shutdown.Token
            );
            await slot.Semaphore.WaitAsync(linked.Token).ConfigureAwait(false);
            try
            {
                await _hydrationSlots.WaitAsync(linked.Token).ConfigureAwait(false);
                try
                {
                    linked.Token.ThrowIfCancellationRequested();
                    ReportProgress(new OperationProgress("Hydrating", 0, 1, name));
                    using SafeFileHandle handle = File.OpenHandle(
                        Resolve(name),
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete,
                        FileOptions.Asynchronous
                    );
                    await _operations.HydrateAsync(handle, linked.Token).ConfigureAwait(false);
                    ReportProgress(new OperationProgress("Hydrating", 1, 1, name));
                }
                finally
                {
                    _hydrationSlots.Release();
                }
            }
            finally
            {
                slot.Semaphore.Release();
            }
        }
        finally
        {
            // Execute before completing the caller task: a canceled waiter
            // cannot dispose a semaphore while another request still holds it.
            lock (_hydrationLock)
            {
                if (--slot.Users == 0)
                {
                    _fileHydrationSlots.Remove(name);
                    slot.Semaphore.Dispose();
                }
            }
        }
    }

    private ContainerCatalog GetMountedCatalog()
    {
        if (_state != MountState.Mounted || _catalog is null)
            throw new InvalidOperationException("Mount is not active.");

        return _catalog;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private async Task CleanupAsync()
    {
        // A configured observation deadline spans fetch and explicit-hydration drains.
        // It does not terminate provider code or authorize an early native disconnect.
        Stopwatch? elapsed = _options.ShutdownTimeout.HasValue ? Stopwatch.StartNew() : null;

        // Prevent new explicit hydrations and allow any already inside CFAPI to finish.
        Task[] active;
        lock (_hydrationLock)
        {
            _state = MountState.Stopping;
            active = _activeHydrations.ToArray();
        }

        await _shutdown.CancelAsync().ConfigureAwait(false);

        // Stop transport first: a native hydrate may be waiting on a fetch worker.
        if (_coordinator is not null)
        {
            await AwaitShutdownDrainAsync(
                _coordinator.DisposeAsync().AsTask(),
                elapsed,
                "fetch workers"
            ).ConfigureAwait(false);
        }

        Task explicitDrain = Task.WhenAll(active);
        try
        {
            await AwaitShutdownDrainAsync(
                explicitDrain,
                elapsed,
                "explicit hydrations"
            ).ConfigureAwait(false);
        }
        catch (TimeoutException) when (!explicitDrain.IsCompleted)
        {
            // Retain connection and callback root while native hydration is pending.
            throw;
        }
        catch
        {
            /* The individual hydrate caller observes its own exception. */
        }

        // A failed disconnect retains callback and container state for a later retry.
        if (_connected)
        {
            _operations.Disconnect(_connectionKey);
            _connected = false;
            _callbackRoot.Free();
        }

        _coordinator = null;
        _catalog = null;
    }

    private async Task AwaitShutdownDrainAsync(Task drain, Stopwatch? elapsed, string phase)
    {
        if (_options.ShutdownTimeout is not { } timeout)
        {
            await drain.ConfigureAwait(false);
            return;
        }

        TimeSpan remaining = timeout - elapsed!.Elapsed;
        if (remaining < TimeSpan.Zero)
            remaining = TimeSpan.Zero;

        try
        {
            await drain.WaitAsync(remaining).ConfigureAwait(false);
        }
        catch (TimeoutException ex) when (!drain.IsCompleted)
        {
            // A pending drain can exist only after the fetch coordinator was initialized.
            FetchCoordinator coordinator = _coordinator!;
            int activeFetches = coordinator.Statistics.ActiveFetches;
            int pendingFetches = coordinator.PendingRequestCount;
            int activeHydrations;
            lock (_hydrationLock)
                activeHydrations = _activeHydrations.Count;

            _logger.LogWarning(
                ex,
                "Shutdown wait expired for {Phase}; active fetches {ActiveFetches}, pending fetches {PendingFetches}, active explicit hydrations {ActiveHydrations}. Native connection retained.",
                phase,
                activeFetches,
                pendingFetches,
                activeHydrations
            );
            throw new TimeoutException(
                $"Shutdown wait expired for {phase}; native connection retained. "
                    + $"Active fetches: {activeFetches}; pending fetches: {pendingFetches}; "
                    + $"explicit hydrations: {activeHydrations}.",
                ex
            );
        }
    }
}
