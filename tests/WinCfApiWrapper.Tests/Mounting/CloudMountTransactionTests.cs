using System.IO.Compression;
using Microsoft.Extensions.Logging;
using WinCfApiWrapper.Archives;
using WinCfApiWrapper.Mounting;
using WinCfApiWrapper.Public;
using WinCfApiWrapper.Public.Containers;
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

        var catalog = new ZipCatalog(File.OpenRead(Source));
        _fingerprint = catalog.Fingerprint;
    }

    private string BaseRoot => Path.Combine(_scratch, "root");

    private string Root => MountPath.For(BaseRoot, _fingerprint);

    private string Source => Path.Combine(_scratch, "source.zip");

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

        await using var mount = new CloudMount(
            new CloudMountOptions
            {
                Root = BaseRoot,
                Container = new ZipContainer(_ => File.OpenRead(source)),
            },
            new FakeMountOperations()
        );

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            mount.MountAsync(TestContext.Current.CancellationToken)
        );
        Assert.False(Directory.Exists(mount.Root));
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
