using System.Collections.Concurrent;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Logging;
using WinCfApiWrapper.Archives;
using WinCfApiWrapper.Hydration;
using WinCfApiWrapper.Mounting;
using WinCfApiWrapper.Public;
using WinCfApiWrapper.Public.Containers;
using Windows.Win32.Storage.CloudFilters;
using Xunit;

namespace WinCfApiWrapper.Tests.Mounting;

// Fault callbacks intentionally use the operation name only to select the failure point.
// ReSharper disable ParameterOnlyUsedForPreconditionCheck.Local
public sealed class CloudMountTransactionTests : IDisposable
{
    private readonly string _fingerprint;

    private readonly string _scratch = Path.Combine(
        Path.GetTempPath(),
        "WinCfApiWrapper.Unit",
        Guid.NewGuid().ToString("N")
    );

    public CloudMountTransactionTests()
    {
        Directory.CreateDirectory(_scratch);
        using (ZipArchive archive = ZipFile.Open(Source, ZipArchiveMode.Create))
        {
            foreach (string name in new[] { "docs/one.txt", "docs/two.txt" })
            {
                using var writer = new StreamWriter(archive.CreateEntry(name).Open());
                writer.Write("content");
            }
        }

        var catalog = new ZipCatalog(File.OpenRead(Source), maxEntries: 2);
        _fingerprint = catalog.Fingerprint;
    }

    private string BaseRoot => Path.Combine(_scratch, "root");

    private string Root => MountPath.For(BaseRoot, _fingerprint);

    private string Source => Path.Combine(_scratch, "source.zip");

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TrustedResume_ReusesIdentity_AndHonorsPlaceholderValidation(
        bool skipValidation
    )
    {
        var reports = new List<OperationProgress>();
        var fake = new FakeMountOperations();
        int opens = 0;
        var container = new ZipContainer(_ =>
        {
            opens++;
            return File.OpenRead(Source);
        });
        var options = new CloudMountOptions
        {
            Container = container,
            Root = BaseRoot,
            TrustedContentId = "1234abcd",
            SkipResumeValidation = skipValidation,
            Progress = new InlineProgress(reports.Add),
        };
        string firstRoot;
        await using (var first = new CloudMount(options, fake))
        {
            await first.MountAsync(TestContext.Current.CancellationToken);
            firstRoot = first.Root;
        }

        fake.Calls.Clear();
        await using (var resumed = new CloudMount(options, fake))
        {
            await resumed.MountAsync(TestContext.Current.CancellationToken);
            Assert.Equal(firstRoot, resumed.Root);
            Assert.EndsWith(".rzm-1234abcd", resumed.Root);
        }

        Assert.Equal(1, opens);
        Assert.Equal(skipValidation ? 0 : 2, fake.Calls.Count(call => call == "validate"));
        Assert.Contains(reports, report => report.Stage == "Indexing" && report.Total is null);
        Assert.Contains(
            reports,
            report => report.Stage == "CreatingPlaceholders" && report.Completed == report.Total
        );
        Assert.Contains(reports, report => report.Stage == "Mounted");
        Assert.Contains(
            reports,
            report => report.Stage == "Validating" && report.Completed == report.Total
        );
        Assert.Equal("Stopped", reports[^1].Stage);
    }

    [Fact]
    public async Task CallbackRoot_ExistsOnlyDuringAnActiveConnection()
    {
        var fake = new FakeMountOperations(retainCallbacks: false);
        CloudMount mount = Open(fake);
        Assert.False(mount.CallbacksRooted);
        try
        {
            await mount.MountAsync(TestContext.Current.CancellationToken);
            Assert.True(mount.CallbacksRooted);
        }
        finally
        {
            await mount.DisposeAsync();
        }

        Assert.False(mount.CallbacksRooted);
        Assert.Equal(1, fake.Calls.Count(call => call == "disconnect"));
    }

    [Fact]
    public async Task CallbackRoot_IsReleasedWhenConnectFails()
    {
        var fake = new FakeMountOperations(name =>
        {
            if (name == "connect")
                throw new IOException("Injected connection failure.");
        }, retainCallbacks: false);
        await using CloudMount mount = Open(fake);

        await Assert.ThrowsAsync<IOException>(() =>
            mount.MountAsync(TestContext.Current.CancellationToken)
        );
        Assert.False(mount.CallbacksRooted);
        Assert.DoesNotContain("disconnect", fake.Calls);
    }

    [Fact]
    public async Task CallbackRoot_SurvivesDisconnectFailureUntilSuccessfulRetry()
    {
        bool rejectDisconnect = true;
        var fake = new FakeMountOperations(name =>
        {
            if (name == "disconnect" && rejectDisconnect)
                throw new IOException("Injected disconnect failure.");
        }, retainCallbacks: false);
        CloudMount mount = Open(fake);
        try
        {
            await mount.MountAsync(TestContext.Current.CancellationToken);
            Assert.True(mount.CallbacksRooted);

            await Assert.ThrowsAsync<IOException>(() => mount.DisposeAsync().AsTask());
            Assert.Equal(MountState.Faulted, mount.State);
            Assert.True(mount.CallbacksRooted);

            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true);
            GC.WaitForPendingFinalizers();
            Assert.True(mount.CallbacksRooted);
        }
        finally
        {
            rejectDisconnect = false;
            await mount.DisposeAsync();
        }

        Assert.False(mount.CallbacksRooted);
        Assert.Equal(2, fake.Calls.Count(call => call == "disconnect"));
    }

    [Fact]
    public async Task OrphanedMount_RemainsRootedUntilExplicitDisconnection()
    {
        var fake = new FakeMountOperations(retainCallbacks: false);
        WeakReference<CloudMount> weakMount = await OpenWithoutRetainingMountAsync(fake);

        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true);
        GC.WaitForPendingFinalizers();

        CloudMount recovered = Assert.IsType<CloudMount>(
            weakMount.TryGetTarget(out CloudMount? target) ? target : null
        );
        try
        {
            Assert.True(recovered.CallbacksRooted);
        }
        finally
        {
            await recovered.DisposeAsync();
        }

        Assert.False(recovered.CallbacksRooted);
    }

    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.NoInlining
    )]
    private async Task<WeakReference<CloudMount>> OpenWithoutRetainingMountAsync(
        FakeMountOperations fake
    )
    {
        CloudMount mount = Open(fake);
        await mount.MountAsync(TestContext.Current.CancellationToken);
        return new WeakReference<CloudMount>(mount);
    }

    [Fact]
    public void SkipResumeValidation_RequiresExplicitTrustedIdentity()
    {
        var options = new CloudMountOptions
        {
            Container = new ZipContainer(_ => File.OpenRead(Source)),
            Root = BaseRoot,
            SkipResumeValidation = true,
        };
        Assert.Throws<ArgumentException>(() => new CloudMount(options, new FakeMountOperations()));
    }

    [Fact]
    public async Task SynchronouslyThrowingProgressReport_DoesNotBreakMountOrCleanup()
    {
        var options = new CloudMountOptions
        {
            Container = new ZipContainer(_ => File.OpenRead(Source)),
            Root = BaseRoot,
            Progress = new InlineProgress(_ => throw new InvalidOperationException("UI failed")),
        };
        await using var mount = new CloudMount(options, new FakeMountOperations());
        await mount.MountAsync(TestContext.Current.CancellationToken);
        Assert.Equal(MountState.Mounted, mount.State);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PostedProgressCallbacks_MustHandleTheirOwnExceptions(bool handleInCallback)
    {
        var context = new QueuedProgressContext();
        var handled = new List<Exception>();
        SynchronizationContext? previous = SynchronizationContext.Current;
        Progress<OperationProgress> progress;
        SynchronizationContext.SetSynchronizationContext(context);
        try
        {
            progress = new Progress<OperationProgress>(_ =>
            {
                try
                {
                    throw new InvalidOperationException("UI failed");
                }
                catch (Exception error) when (handleInCallback)
                {
                    handled.Add(error);
                }
            });
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }

        var options = new CloudMountOptions
        {
            Container = new ZipContainer(_ => File.OpenRead(Source)),
            Root = BaseRoot,
            Progress = progress,
        };
        var mount = new CloudMount(options, new FakeMountOperations());
        await using (mount)
            await mount.MountAsync(TestContext.Current.CancellationToken);
        Assert.Equal(MountState.Stopped, mount.State);
        Assert.Empty(handled);
        Assert.True(context.Count > 0);
        if (handleInCallback)
        {
            int pending = context.Count;
            context.Drain();
            Assert.Equal(pending, handled.Count);
        }
        else
        {
            // Observe the UI-dispatch exception here rather than letting it
            // become an unhandled exception in the test process.
            Assert.Throws<InvalidOperationException>(() => context.Drain());
        }
    }

    public void Dispose()
    {
        Directory.Delete(_scratch, true);
    }

    [Fact]
    public async Task Mount_InMemoryArchiveFactory_IndexesWithoutAFilePath()
    {
        CancellationToken testCancellationToken = TestContext.Current.CancellationToken;

        byte[] bytes = await File.ReadAllBytesAsync(Source, testCancellationToken);
        int opens = 0;
        var options = new CloudMountOptions
        {
            Root = BaseRoot,
            Container = new ZipContainer(_ =>
            {
                Interlocked.Increment(ref opens);

                return new MemoryStream(bytes, false);
            }),
        };
        await using var mount = new CloudMount(options, new FakeMountOperations());

        await mount.MountAsync(testCancellationToken);

        Assert.True(mount.Contains("docs/one.txt"));
        Assert.Equal(1, opens);
    }

    [Fact]
    public async Task Mount_Failure_IsLoggedWithException()
    {
        var logger = new RecordingLogger();
        var fake = new FakeMountOperations(name =>
        {
            if (name == "connect")
                throw new IOException("connect");
        });
        await using var mount = new CloudMount(
            new CloudMountOptions
            {
                Root = BaseRoot,
                Container = new ZipContainer(_ => File.OpenRead(Source)),
            },
            fake,
            logger
        );

        IOException error = await Assert.ThrowsAsync<IOException>(() =>
            mount.MountAsync(TestContext.Current.CancellationToken)
        );

        Assert.Contains(
            logger.Entries,
            entry => entry.Level == LogLevel.Error && ReferenceEquals(entry.Exception, error)
        );
    }

    [Fact]
    public Task Mount_RegistrationFailure_LeavesRootUsable()
    {
        return AssertRollback("register");
    }

    [Fact]
    public async Task Mount_ConnectionFailure_UnregistersOnlyOwnRegistration()
    {
        await AssertRollback("connect");
    }

    [Fact]
    public Task Mount_PlaceholderCreationFailure_RollsBack()
    {
        return AssertRollback("placeholder-created");
    }

    [Fact]
    public async Task Mount_CancellationDuringPopulation_RollsBack()
    {
        using var cancellation = new CancellationTokenSource();
        var fake = new FakeMountOperations(name =>
        {
            if (name == "placeholder-created")
            {
                // The callback runs during MountAsync, before the token source leaves this scope.
                // ReSharper disable once AccessToDisposedClosure
                cancellation.Cancel();
            }
        });
        await using CloudMount mount = Open(fake);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            mount.MountAsync(cancellation.Token)
        );
        Assert.False(fake.Registered);
        Assert.False(Directory.Exists(Root));
    }

    [Fact]
    public async Task Mount_Failure_AllowsFreshMountAtSamePath()
    {
        CancellationToken testCancellationToken = TestContext.Current.CancellationToken;

        var fake = new FakeMountOperations(name =>
        {
            if (name == "connect")
                throw new IOException("connect");
        });
        await using (CloudMount failed = Open(fake))
            await Assert.ThrowsAsync<IOException>(() => failed.MountAsync(testCancellationToken));

        fake.Fault = null;
        await using CloudMount fresh = Open(fake);
        await fresh.MountAsync(testCancellationToken);
        Assert.Equal(MountState.Mounted, fresh.State);
    }

    [Fact]
    public async Task Resume_ValidationFailure_PreservesExistingPlaceholdersAndFolder()
    {
        CancellationToken testCancellationToken = TestContext.Current.CancellationToken;

        var fake = new FakeMountOperations();
        await using (CloudMount first = Open(fake))
            await first.MountAsync(testCancellationToken);

        string path = Path.Combine(Root, "docs", "one.txt");
        string content = await File.ReadAllTextAsync(path, testCancellationToken);
        fake.Fault = name =>
        {
            if (name == "validate")
                throw new IOException("identity mismatch");
        };
        await using CloudMount resumed = Open(fake);
        await Assert.ThrowsAsync<IOException>(() => resumed.MountAsync(testCancellationToken));
        Assert.Equal(content, await File.ReadAllTextAsync(path, testCancellationToken));
        Assert.True(Directory.Exists(Root));
        Assert.True(fake.Registered);
        Assert.DoesNotContain("unregister", fake.Calls);
    }

    [Fact]
    public async Task Resume_ValidRoot_ConnectsWithoutUpdatingRegistration()
    {
        CancellationToken testCancellationToken = TestContext.Current.CancellationToken;

        var fake = new FakeMountOperations();
        await using (CloudMount first = Open(fake))
            await first.MountAsync(testCancellationToken);

        fake.Calls.Clear();
        await using CloudMount resumed = Open(fake);
        await resumed.MountAsync(testCancellationToken);
        Assert.Equal(MountState.Mounted, resumed.State);
        Assert.DoesNotContain("register", fake.Calls);
    }

    [Fact]
    public void Open_InvalidShutdownWait_RejectsConfiguration()
    {
        foreach (TimeSpan timeout in new[]
        {
            TimeSpan.Zero,
            TimeSpan.FromTicks(-1),
            TimeSpan.FromDays(60),
        })
        {
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                new CloudMount(
                    new CloudMountOptions
                    {
                        Root = BaseRoot,
                        Container = new ZipContainer(_ => File.OpenRead(Source)),
                        ShutdownTimeout = timeout,
                    },
                    new FakeMountOperations()
                )
            );
        }
    }

    [Fact]
    public async Task Dispose_StalledFetch_RetainsNativeCallbacksUntilWorkerDrains()
    {
        CancellationToken token = TestContext.Current.CancellationToken;

        // This test invokes the registered callback through the fake adapter.
        var fake = new FakeMountOperations(retainCallbacks: true);
        var logger = new RecordingLogger();
        var stalled = new StalledReadStream();
        var mount = new CloudMount(
            new CloudMountOptions
            {
                Root = BaseRoot,
                Container = new ZipContainer(_ => File.OpenRead(Source)),
                FetchWorkers = 1,
                ShutdownTimeout = TimeSpan.FromMilliseconds(200),
            },
            fake,
            logger,
            new NativeTransfer((_, _) => { }),
            _ => stalled
        );

        await mount.MountAsync(token);
        try
        {
            SimulateFetch(fake);
            await stalled.ReadEntered.WaitAsync(TimeSpan.FromSeconds(5), token);

            TimeoutException error = await Assert.ThrowsAsync<TimeoutException>(() =>
                mount.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5), token)
            );
            Assert.Contains("fetch workers", error.Message);
            Assert.Equal(MountState.Faulted, mount.State);
            Assert.True(mount.CallbacksRooted);
            Assert.Equal(1, mount.Statistics.ActiveFetches);
            Assert.DoesNotContain("disconnect", fake.Calls);
            Assert.Contains(
                logger.Entries,
                item => item.Level == LogLevel.Warning && item.Exception is TimeoutException
            );
        }
        finally
        {
            stalled.Release();
            await mount.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5), token);
        }

        Assert.Equal(MountState.Stopped, mount.State);
        Assert.False(mount.CallbacksRooted);
        Assert.Equal(1, fake.Calls.Count(call => call == "disconnect"));
    }

    [Fact]
    public async Task Dispose_StalledNativeHydration_ReportsPhaseAndAllowsRetry()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        var fake = new FakeMountOperations(
            retainCallbacks: false,
            hydrateStarted: new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously
            ),
            releaseHydrate: new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously
            )
        );
        var logger = new RecordingLogger();
        var mount = new CloudMount(
            new CloudMountOptions
            {
                Root = BaseRoot,
                Container = new ZipContainer(_ => File.OpenRead(Source)),
                ShutdownTimeout = TimeSpan.FromMilliseconds(200),
            },
            fake,
            logger
        );
        await mount.MountAsync(token);
        Task hydration = mount.HydrateAsync("docs/one.txt", token);
        try
        {
            await fake.HydrateStarted!.Task.WaitAsync(TimeSpan.FromSeconds(5), token);

            TimeoutException error = await Assert.ThrowsAsync<TimeoutException>(() =>
                mount.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5), token)
            );
            Assert.Contains("explicit hydrations", error.Message);
            Assert.Equal(MountState.Faulted, mount.State);
            Assert.True(mount.CallbacksRooted);
            Assert.DoesNotContain("disconnect", fake.Calls);
            Assert.Contains(
                logger.Entries,
                item => item.Level == LogLevel.Warning && item.Exception is TimeoutException
            );
        }
        finally
        {
            fake.ReleaseHydrate!.TrySetResult();
            await mount.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5), token);
        }

        try
        {
            await hydration;
        }
        catch (OperationCanceledException)
        {
            // The caller may observe shutdown cancellation after the native call.
        }

        Assert.Equal(MountState.Stopped, mount.State);
        Assert.False(mount.CallbacksRooted);
        Assert.Equal(1, fake.Calls.Count(call => call == "disconnect"));
    }

    [Fact]
    public async Task Dispose_ConcurrentHydration_DoesNotReleaseResourcesEarly()
    {
        CancellationToken testCancellationToken = TestContext.Current.CancellationToken;

        var fake = new FakeMountOperations(
            hydrateStarted: new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously
            ),
            releaseHydrate: new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously
            )
        );
        CloudMount mount = Open(fake);
        await mount.MountAsync(testCancellationToken);
        Task hydration = mount.HydrateAsync("docs/one.txt", testCancellationToken);
        await fake.HydrateStarted!.Task.WaitAsync(TimeSpan.FromSeconds(5), testCancellationToken);
        Task disposal = mount.DisposeAsync().AsTask();
        Assert.False(disposal.IsCompleted);
        Assert.DoesNotContain("disconnect", fake.Calls);
        fake.ReleaseHydrate!.SetResult();
        await hydration;
        await disposal;
        Assert.Contains("disconnect", fake.Calls);
    }

    [Fact]
    public async Task Dispose_CancellableHydration_CancelsReadBeforeDisconnect()
    {
        CancellationToken testCancellationToken = TestContext.Current.CancellationToken;

        var fake = new FakeMountOperations(
            honorHydrationCancellation: true,
            hydrateStarted: new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously
            ),
            releaseHydrate: new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously
            )
        );
        CloudMount mount = Open(fake);
        await mount.MountAsync(testCancellationToken);
        Task hydration = mount.HydrateAsync("docs/one.txt", testCancellationToken);
        await fake.HydrateStarted!.Task.WaitAsync(TimeSpan.FromSeconds(5), testCancellationToken);
        await mount
            .DisposeAsync()
            .AsTask()
            .WaitAsync(TimeSpan.FromSeconds(5), testCancellationToken);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => hydration);
        Assert.Equal(0, fake.ActiveHydrates);
        Assert.Contains("disconnect", fake.Calls);
    }

    [Fact]
    public async Task Dispose_DuringBulkHydration_CancelsBulkOperation()
    {
        CancellationToken testCancellationToken = TestContext.Current.CancellationToken;

        var fake = new FakeMountOperations(
            honorHydrationCancellation: true,
            hydrateStarted: new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously
            ),
            releaseHydrate: new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously
            )
        );
        var mount = new CloudMount(
            new CloudMountOptions
            {
                Root = BaseRoot,
                Container = new ZipContainer(_ => File.OpenRead(Source)),
                BulkHydrationConcurrency = 1,
            },
            fake
        );
        await mount.MountAsync(testCancellationToken);
        Task hydration = mount.HydrateAllAsync(cancellationToken: testCancellationToken);
        await fake.HydrateStarted!.Task.WaitAsync(TimeSpan.FromSeconds(5), testCancellationToken);

        await mount
            .DisposeAsync()
            .AsTask()
            .WaitAsync(TimeSpan.FromSeconds(5), testCancellationToken);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => hydration);
        Assert.Equal(1, fake.Calls.Count(call => call == "hydrate"));
    }

    [Fact]
    public async Task Dispose_WhenNativeHydrationFails_ReportsBulkCancellation()
    {
        CancellationToken testCancellationToken = TestContext.Current.CancellationToken;

        var fake = new FakeMountOperations(
            hydrateStarted: new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously
            ),
            releaseHydrate: new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously
            ),
            afterHydrate: () => throw new IOException("native read failed")
        );
        var mount = new CloudMount(
            new CloudMountOptions
            {
                Root = BaseRoot,
                Container = new ZipContainer(_ => File.OpenRead(Source)),
                BulkHydrationConcurrency = 1,
            },
            fake
        );
        await mount.MountAsync(testCancellationToken);
        Task hydration = mount.HydrateAllAsync(cancellationToken: testCancellationToken);
        await fake.HydrateStarted!.Task.WaitAsync(TimeSpan.FromSeconds(5), testCancellationToken);
        Task disposal = mount.DisposeAsync().AsTask();
        Assert.Equal(MountState.Stopping, mount.State);

        fake.ReleaseHydrate!.SetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => hydration);
        await disposal.WaitAsync(TimeSpan.FromSeconds(5), testCancellationToken);
        Assert.Equal(MountState.Stopped, mount.State);
    }

    [Fact]
    public async Task HydrateAll_FileFailures_ReportProgressAndPreserveEveryError()
    {
        CancellationToken testCancellationToken = TestContext.Current.CancellationToken;

        var fake = new FakeMountOperations(name =>
        {
            if (name == "hydrate")
                throw new IOException("read failed");
        });
        await using CloudMount mount = new(
            new CloudMountOptions
            {
                Root = BaseRoot,
                Container = new ZipContainer(_ => File.OpenRead(Source)),
                BulkHydrationConcurrency = 1,
            },
            fake
        );
        await mount.MountAsync(testCancellationToken);
        var progress = new RecordingProgress();

        AggregateException error = await Assert.ThrowsAsync<AggregateException>(() =>
            mount.HydrateAllAsync(progress, cancellationToken: testCancellationToken)
        );

        Assert.Equal(2, error.InnerExceptions.Count);
        Assert.All(
            error.InnerExceptions,
            exception => Assert.Contains("read failed", exception.InnerException?.Message)
        );
        Assert.Equal(2, progress.Updates.Count);
        Assert.Contains(
            progress.Updates,
            update => update is { Completed: 2, Total: 2, Failed: 2 }
        );
    }

    [Fact]
    public async Task HydrateAll_SynchronouslyThrowingProgress_DoesNotInterruptOtherFiles()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        var logger = new RecordingLogger();
        var fake = new FakeMountOperations();
        await using var mount = new CloudMount(
            new CloudMountOptions
            {
                Root = BaseRoot,
                Container = new ZipContainer(_ => File.OpenRead(Source)),
                BulkHydrationConcurrency = 1,
            },
            fake,
            logger
        );
        await mount.MountAsync(token);
        var progress = new ThrowingHydrationProgress();

        await mount.HydrateAllAsync(progress, token);

        Assert.Equal(2, fake.Calls.Count(call => call == "hydrate"));
        Assert.Equal(2, progress.Reports);
        Assert.Equal(
            2,
            logger.Entries.Count(entry =>
                entry.Level == LogLevel.Warning
                && entry.Exception is InvalidOperationException
            )
        );
    }

    [Fact]
    public async Task HydrateAll_ThrowingProgress_DoesNotMaskFileFailures()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        var logger = new RecordingLogger();
        var fake = new FakeMountOperations(name =>
        {
            if (name == "hydrate")
                throw new IOException("read failed");
        });
        await using var mount = new CloudMount(
            new CloudMountOptions
            {
                Root = BaseRoot,
                Container = new ZipContainer(_ => File.OpenRead(Source)),
                BulkHydrationConcurrency = 1,
            },
            fake,
            logger
        );
        await mount.MountAsync(token);
        var progress = new ThrowingHydrationProgress();

        AggregateException error = await Assert.ThrowsAsync<AggregateException>(() =>
            mount.HydrateAllAsync(progress, token)
        );

        Assert.Equal(2, error.InnerExceptions.Count);
        Assert.All(
            error.InnerExceptions,
            exception => Assert.Contains("read failed", exception.InnerException?.Message)
        );
        Assert.Equal(2, progress.Reports);
        Assert.Equal(
            2,
            logger.Entries.Count(entry =>
                entry.Level == LogLevel.Warning
                && entry.Exception is InvalidOperationException
            )
        );
    }

    [Fact]
    public async Task HydrateAll_ThrowingProgress_DoesNotMaskCancellation()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        using var canceledByHydration = new CancellationTokenSource();
        var logger = new RecordingLogger();
        var fake = new FakeMountOperations(afterHydrate: canceledByHydration.Cancel);
        await using var mount = new CloudMount(
            new CloudMountOptions
            {
                Root = BaseRoot,
                Container = new ZipContainer(_ => File.OpenRead(Source)),
                BulkHydrationConcurrency = 1,
            },
            fake,
            logger
        );
        await mount.MountAsync(token);
        var progress = new ThrowingHydrationProgress();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            mount.HydrateAllAsync(progress, canceledByHydration.Token)
        );

        Assert.True(progress.Reports > 0);
        Assert.Contains(
            logger.Entries,
            entry =>
                entry.Level == LogLevel.Warning
                && entry.Exception is InvalidOperationException
        );
    }

    [Fact]
    public async Task Mount_PublicOperations_RequireActiveMountAndKnownFile()
    {
        CancellationToken testCancellationToken = TestContext.Current.CancellationToken;

        await using CloudMount mount = Open(new FakeMountOperations());
        Assert.Throws<InvalidOperationException>(() => mount.Contains("docs/one.txt"));
        Assert.Throws<InvalidOperationException>(() => mount.Root);
        await mount.MountAsync(testCancellationToken);

        Assert.True(mount.Contains("docs/one.txt"));
        Assert.False(mount.Contains("missing.txt"));
        Assert.Equal(Path.Combine(mount.Root, "docs", "one.txt"), mount.Resolve("docs/one.txt"));
        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            mount.HydrateAsync("missing.txt", testCancellationToken)
        );
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            mount.MountAsync(testCancellationToken)
        );

        // Dispose early to verify the public operations reject a stopped mount; await using remains
        // as cleanup if an earlier assertion fails.
        // ReSharper disable once DisposeOnUsingVariable
        await mount.DisposeAsync();
        Assert.Throws<InvalidOperationException>(() => mount.Contains("docs/one.txt"));
        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            mount.MountAsync(testCancellationToken)
        );
    }

    [Fact]
    public async Task NativeCallbackFailure_NotifiesAllHandlersEvenWhenOneThrows()
    {
        var fake = new FakeMountOperations();
        var logger = new RecordingLogger();
        await using var mount = new CloudMount(
            new CloudMountOptions
            {
                Root = BaseRoot,
                Container = new ZipContainer(_ => File.OpenRead(Source)),
            },
            fake,
            logger
        );
        await mount.MountAsync(TestContext.Current.CancellationToken);
        var reported = new List<MountErrorEventArgs>();
        mount.Error += (_, _) => throw new IOException("bad subscriber");
        mount.Error += (_, args) => reported.Add(args);

        unsafe
        {
            fake.Callbacks![0].Callback(null, null);
            fake.Callbacks[1].Callback(null, null);
        }

        Assert.Equal(2, reported.Count);
        Assert.All(
            reported,
            args =>
            {
                Assert.Null(args.RelativePath);
                Assert.IsType<InvalidDataException>(args.Exception);
            }
        );
        Assert.Equal(2, logger.Entries.Count(entry => entry.Level == LogLevel.Warning));
    }

    [Fact]
    public async Task NativeCallbackFailure_WithoutSubscribers_IsLoggedAndContained()
    {
        var fake = new FakeMountOperations();
        var logger = new RecordingLogger();
        await using var mount = new CloudMount(
            new CloudMountOptions
            {
                Root = BaseRoot,
                Container = new ZipContainer(_ => File.OpenRead(Source)),
            },
            fake,
            logger
        );
        await mount.MountAsync(TestContext.Current.CancellationToken);

        unsafe
        {
            fake.Callbacks![0].Callback(null, null);
        }

        Assert.Contains(
            logger.Entries,
            entry => entry is { Level: LogLevel.Error, Exception: InvalidDataException }
        );
    }

    [Fact]
    public async Task Mount_OversizedFileIdentity_RejectsBeforeCreatingSyncRoot()
    {
        string longName = string.Join('/', Enumerable.Repeat(new string('a', 100), 45)) + ".txt";
        string source = Path.Combine(_scratch, "oversized.zip");
        using (ZipArchive archive = ZipFile.Open(source, ZipArchiveMode.Create))
            archive.CreateEntry(longName);

        var fake = new FakeMountOperations();
        await using var mount = new CloudMount(
            new CloudMountOptions
            {
                Root = BaseRoot,
                Container = new ZipContainer(_ => File.OpenRead(source)),
            },
            fake
        );

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            mount.MountAsync(TestContext.Current.CancellationToken)
        );

        // The catalog now rejects the identity before a fingerprinted mount path
        // exists, so Root is intentionally unavailable.
        Assert.Throws<InvalidOperationException>(() => mount.Root);
        Assert.Empty(fake.Calls);
        Assert.Empty(Directory.EnumerateDirectories(_scratch, "*.rzm-*"));
    }

    [Fact]
    public async Task HydrateAll_Success_ReportsCompletedFiles()
    {
        CancellationToken testCancellationToken = TestContext.Current.CancellationToken;

        var fake = new FakeMountOperations();
        await using CloudMount mount = Open(fake);
        await mount.MountAsync(testCancellationToken);
        var progress = new RecordingProgress();

        await mount.HydrateAllAsync(progress, cancellationToken: testCancellationToken);

        Assert.Equal(2, fake.Calls.Count(call => call == "hydrate"));
        Assert.Contains(
            progress.Updates,
            update => update is { Completed: 2, Total: 2, Failed: 0 }
        );
        Assert.Equal(0, mount.Statistics.FailedFetches);
    }

    [Fact]
    public async Task Resume_UnregisteredFolder_PreservesExistingFiles()
    {
        CancellationToken testCancellationToken = TestContext.Current.CancellationToken;

        Directory.CreateDirectory(Root);
        string retained = Path.Combine(Root, "user-file.txt");
        await File.WriteAllTextAsync(retained, "keep", testCancellationToken);
        await using CloudMount mount = Open(new FakeMountOperations());

        IOException error = await Assert.ThrowsAsync<IOException>(() =>
            mount.MountAsync(testCancellationToken)
        );

        Assert.Contains("not registered", error.Message);
        Assert.Equal("keep", await File.ReadAllTextAsync(retained, testCancellationToken));
    }

    [Fact]
    public async Task Mount_RootIsFile_DoesNotReplaceIt()
    {
        CancellationToken testCancellationToken = TestContext.Current.CancellationToken;

        await File.WriteAllTextAsync(Root, "keep", testCancellationToken);
        await using CloudMount mount = Open(new FakeMountOperations());

        await Assert.ThrowsAsync<IOException>(() => mount.MountAsync(testCancellationToken));

        Assert.Equal("keep", await File.ReadAllTextAsync(Root, testCancellationToken));
    }

    [Fact]
    public async Task Mount_StartupAndDisconnectFailures_RetainsRootForCleanupRetry()
    {
        var fake = new FakeMountOperations(name =>
        {
            if (name is "placeholder-created" or "disconnect")
                throw new IOException(name);
        });
        CloudMount mount = Open(fake);
        AggregateException error = await Assert.ThrowsAsync<AggregateException>(() =>
            mount.MountAsync(TestContext.Current.CancellationToken)
        );
        Assert.Equal(2, error.InnerExceptions.Count);
        Assert.Contains("placeholder-created", error.InnerExceptions[0].Message);
        Assert.Contains("Startup resource cleanup failed", error.InnerExceptions[1].Message);
        Assert.True(Directory.Exists(Root));

        fake.Fault = null;
        await mount.DisposeAsync();
        MountCleanup.Delete(Root, fake);
    }

    [Fact]
    public void Open_InvalidConcurrencyOrVolumeRoot_RejectsConfiguration()
    {
        var fake = new FakeMountOperations();
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new CloudMount(
                new CloudMountOptions
                {
                    Root = BaseRoot,
                    Container = new ZipContainer(_ => File.OpenRead(Source)),
                    FetchWorkers = 0,
                },
                fake
            )
        );
        Assert.Throws<ArgumentException>(() =>
            new CloudMount(
                new CloudMountOptions
                {
                    Root = Path.GetPathRoot(BaseRoot)!,
                    Container = new ZipContainer(_ => File.OpenRead(Source)),
                },
                fake
            )
        );
    }

    [Fact]
    public async Task ConcurrentExplicitHydration_64Requests_RespectsNativeConcurrencyLimit()
    {
        CancellationToken testCancellationToken = TestContext.Current.CancellationToken;

        var fake = new FakeMountOperations(
            hydrateStarted: new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously
            ),
            releaseHydrate: new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously
            )
        );
        await using CloudMount mount = Open(fake);
        await mount.MountAsync(testCancellationToken);
        Task[] hydrations = Enumerable
            .Range(0, 64)
            .Select(_ => mount.HydrateAsync("docs/one.txt", testCancellationToken))
            .ToArray();
        await fake.HydrateStarted!.Task.WaitAsync(TimeSpan.FromSeconds(5), testCancellationToken);
        try
        {
            Assert.InRange(fake.ActiveHydrates, 1, 4);
        }
        finally
        {
            fake.ReleaseHydrate!.SetResult();
        }

        await Task.WhenAll(hydrations).WaitAsync(TimeSpan.FromSeconds(10), testCancellationToken);
        Assert.Equal(1, fake.MaxActiveHydrates);
        Assert.Equal(0, fake.ActiveHydrates);
    }

    [Fact]
    public async Task Dispose_CalledTwice_IsSafe()
    {
        var fake = new FakeMountOperations();
        CloudMount mount = Open(fake);
        await mount.MountAsync(TestContext.Current.CancellationToken);
        await mount.DisposeAsync();
        await mount.DisposeAsync();
        Assert.Equal(1, fake.Calls.Count(x => x == "disconnect"));
        Assert.Equal(MountState.Stopped, mount.State);
        Assert.True(fake.Registered);
    }

    [Fact]
    public async Task Mount_RegistersOnlyFetchCallbacks()
    {
        var fake = new FakeMountOperations();
        await using CloudMount mount = Open(fake);
        await mount.MountAsync(TestContext.Current.CancellationToken);
        Assert.Equal(new uint[] { 0, 2, uint.MaxValue }, fake.CallbackTypes);
    }

    [Fact]
    public async Task Dispose_DisconnectFailure_RetainsResourcesAndAllowsRetry()
    {
        var fake = new FakeMountOperations(name =>
        {
            if (name == "disconnect")
                throw new IOException("disconnect failed");
        });
        CloudMount mount = Open(fake);
        await mount.MountAsync(TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<IOException>(() => mount.DisposeAsync().AsTask());
        Assert.Equal(MountState.Faulted, mount.State);
        Assert.True(fake.Registered);
        fake.Fault = null;
        await mount.DisposeAsync();
        Assert.Equal(MountState.Stopped, mount.State);
    }

    [Fact]
    public async Task RollbackFailure_PreservesOriginalErrorAndReportsCleanupError()
    {
        var fake = new FakeMountOperations(name =>
        {
            switch (name)
            {
                case "connect":
                    throw new IOException("original connection error");
                case "unregister":
                    throw new IOException("rollback unregister error");
            }
        });
        await using CloudMount mount = Open(fake);
        AggregateException error = await Assert.ThrowsAsync<AggregateException>(() =>
            mount.MountAsync(TestContext.Current.CancellationToken)
        );
        Assert.Contains("original connection error", error.InnerExceptions[0].Message);
        Assert.Contains("rollback unregister error", error.InnerExceptions[1].ToString());
        Assert.True(fake.Registered);
    }

    [Fact]
    public async Task Rollback_UnregisterFails_PreservesFolder()
    {
        var fake = new FakeMountOperations(name =>
        {
            switch (name)
            {
                case "placeholder-created":
                    throw new IOException("startup failure");
                case "unregister":
                    throw new IOException("unregister failure");
            }
        });
        await using CloudMount mount = Open(fake);
        AggregateException error = await Assert.ThrowsAsync<AggregateException>(() =>
            mount.MountAsync(TestContext.Current.CancellationToken)
        );

        Assert.Contains("startup failure", error.InnerExceptions[0].Message);
        Assert.Contains("unregister failure", error.InnerExceptions[1].ToString());
        Assert.True(fake.Registered);
        Assert.True(Directory.Exists(Root));
        Assert.True(File.Exists(Path.Combine(Root, "docs", "one.txt")));
    }

    [Fact]
    public async Task Rollback_UnregisterFails_CleanupCanBeRetried()
    {
        CancellationToken testCancellationToken = TestContext.Current.CancellationToken;

        var fake = new FakeMountOperations(name =>
        {
            switch (name)
            {
                case "connect":
                    throw new IOException("connect failure");
                case "unregister":
                    throw new IOException("unregister failure");
            }
        });
        await using (CloudMount failed = Open(fake))
        {
            await Assert.ThrowsAsync<AggregateException>(() =>
                failed.MountAsync(testCancellationToken)
            );
        }

        await using (CloudMount blocked = Open(fake))
        {
            IOException error = await Assert.ThrowsAsync<IOException>(() =>
                blocked.MountAsync(testCancellationToken)
            );
            Assert.Contains("Mount directory is missing", error.Message);
        }

        fake.Fault = null;
        MountCleanup.Delete(Root, fake);
        Assert.False(fake.Registered);
        Assert.False(Directory.Exists(Root));
        await using CloudMount fresh = Open(fake);
        await fresh.MountAsync(testCancellationToken);
        Assert.Equal(MountState.Mounted, fresh.State);
    }

    [Fact]
    public async Task Rollback_UnregisterSucceeds_ButFileDeleteFails_RemainsRecoverable()
    {
        FileStream? locked = null;
        var fake = new FakeMountOperations(name =>
        {
            switch (name)
            {
                case "placeholder-created":
                    throw new IOException("startup failure");
                case "unregister":
                    locked = File.Open(
                        Path.Combine(Root, "docs", "one.txt"),
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.None
                    );
                    break;
            }
        });
        try
        {
            await using CloudMount mount = Open(fake);
            await Assert.ThrowsAsync<AggregateException>(() =>
                mount.MountAsync(TestContext.Current.CancellationToken)
            );
            Assert.False(fake.Registered);
            Assert.True(Directory.Exists(Root));
            Assert.False(fake.Registered);
            Assert.True(File.Exists(Path.Combine(Root, "docs", "one.txt")));
        }
        finally
        {
            if (locked is not null)
                await locked.DisposeAsync();
        }

        fake.Fault = null;
        MountCleanup.Delete(Root, fake);
        Assert.False(Directory.Exists(Root));
    }

    private static unsafe void SimulateFetch(FakeMountOperations fake)
    {
        byte[] identity = Encoding.UTF8.GetBytes(@"docs\one.txt");
        fixed (byte* identityPointer = identity)
        {
            var info = new CF_CALLBACK_INFO
            {
                StructSize = (uint)Marshal.SizeOf<CF_CALLBACK_INFO>(),
                FileIdentity = identityPointer,
                FileIdentityLength = (uint)identity.Length,
                FileSize = 7,
                ConnectionKey = (CF_CONNECTION_KEY)1,
                TransferKey = 2,
                RequestKey = 3,
            };
            var parameters = new CF_CALLBACK_PARAMETERS
            {
                ParamSize = (uint)(
                    sizeof(uint)
                    + sizeof(CF_CALLBACK_PARAMETERS._Anonymous_e__Union._FetchData_e__Struct)
                ),
            };
            parameters.FetchData.RequiredFileOffset = 0;
            parameters.FetchData.RequiredLength = 7;
            fake.Callbacks![0].Callback(&info, &parameters);
        }
    }

    private CloudMount Open(FakeMountOperations fake)
    {
        return new CloudMount(
            new CloudMountOptions
            {
                Root = BaseRoot,
                Container = new ZipContainer(_ => File.OpenRead(Source)),
            },
            fake
        );
    }

    private async Task AssertRollback(string stage)
    {
        var fake = new FakeMountOperations(name =>
        {
            if (name == stage)
                throw new IOException(stage);
        });
        await using (CloudMount mount = Open(fake))
        {
            await Assert.ThrowsAsync<IOException>(() => mount.MountAsync());
            Assert.Equal(MountState.Faulted, mount.State);
        }

        Assert.False(fake.Registered);
        Assert.False(Directory.Exists(Root));

        if (stage != "register")
            Assert.Contains("unregister", fake.Calls);
    }

    private sealed class QueuedProgressContext : SynchronizationContext
    {
        private readonly ConcurrentQueue<(SendOrPostCallback Callback, object? State)> _pending =
            new();

        public int Count => _pending.Count;

        public override void Post(SendOrPostCallback d, object? state) =>
            _pending.Enqueue((d, state));

        public void Drain()
        {
            while (_pending.TryDequeue(out var work))
                work.Callback(work.State);
        }
    }

    private sealed class InlineProgress(Action<OperationProgress> report)
        : IProgress<OperationProgress>
    {
        public void Report(OperationProgress value) => report(value);
    }

    private sealed class StalledReadStream : MemoryStream
    {
        private readonly TaskCompletionSource _entered = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

        private readonly TaskCompletionSource _released = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

        public StalledReadStream()
            : base(Encoding.UTF8.GetBytes("content")) { }

        public Task ReadEntered => _entered.Task;

        public void Release() => _released.TrySetResult();

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default
        )
        {
            _entered.TrySetResult();

            // Deliberately ignore the canceled transport token to exercise
            // the provider-stall shutdown behavior without native CFAPI I/O.
            await _released.Task.ConfigureAwait(false);
            return await base.ReadAsync(buffer, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private sealed class RecordingLogger : ILogger<CloudMount>
    {
        public List<(LogLevel Level, Exception? Exception)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
        {
            return null;
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return true;
        }

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        )
        {
            Entries.Add((logLevel, exception));
        }
    }

    private sealed class ThrowingHydrationProgress : IProgress<HydrationProgress>
    {
        private int _reports;

        public int Reports => Volatile.Read(ref _reports);

        public void Report(HydrationProgress value)
        {
            Interlocked.Increment(ref _reports);
            throw new InvalidOperationException("UI progress observer failed");
        }
    }

    private sealed class RecordingProgress : IProgress<HydrationProgress>
    {
        public List<HydrationProgress> Updates { get; } = [];

        public void Report(HydrationProgress value)
        {
            lock (Updates)
                Updates.Add(value);
        }
    }
}

// ReSharper restore ParameterOnlyUsedForPreconditionCheck.Local
