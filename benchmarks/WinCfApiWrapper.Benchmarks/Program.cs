using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using WinCfApiWrapper;
using WinCfApiWrapper.Public;
using WinCfApiWrapper.Public.Containers;

namespace WinCfApiWrapper.Benchmarks;

// Standalone Windows benchmark. Never part of the production library.
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args.Contains("--help"))
        {
            Console.WriteLine("Options: --entries N --bytes-per-file N --random-reads N --files-per-directory N --resume-probe-passes N --repeat-hydrations N --archive ZIP --http-url URL --work-parent DIR --output JSON");
            return 0;
        }

        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 16299))
        {
            Console.Error.WriteLine("Native CFAPI benchmarks require Windows 10 1709 or later.");
            return 2;
        }

        try
        {
            await RunAsync(Options.Parse(args));
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private static async Task RunAsync(Options options)
    {
        string parent = Path.GetFullPath(options.WorkParent ?? Path.Combine(Path.GetTempPath(), "WinCfApiWrapper.Benchmarks"));
        Directory.CreateDirectory(parent);
        string scratch = Path.Combine(parent, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        string archivePath = options.Archive ?? Path.Combine(scratch, "fixture.zip");
        string mountBase = Path.Combine(scratch, "mount");
        var counts = new Counters();
        using HttpClient? client = CreateHttpClient(options, counts);
        using var services = new ServiceCollection().AddWinCfApiWrapper().BuildServiceProvider();
        ICloudMountFactory factory = services.GetRequiredService<ICloudMountFactory>();
        ISyncRootManager manager = services.GetRequiredService<ISyncRootManager>();
        ICloudMount? active = null;
        string? mountRoot = null;
        var phases = new List<PhaseResult>();

        try
        {
            if (options.Archive is null)
                GenerateArchive(archivePath, options.Entries, options.BytesPerFile, options.FilesPerDirectory);
            if (!File.Exists(archivePath))
                throw new FileNotFoundException("Archive not found", archivePath);

            // Separate indexing measurement does not prime the cold mount's cache.
            ZipContainer indexOnly = Container(archivePath, options, client, counts);
            await Measure(
                "zip-index-only",
                async () => { _ = await indexOnly.GetEntriesAsync(); },
                counts,
                phases
            );

            ZipContainer original = Container(archivePath, options, client, counts);
            active = factory.Open(MountOptions(original, mountBase));
            ICloudMount cold = active;
            await Measure("cold-mount", () => cold.MountAsync(), counts, phases);
            mountRoot = cold.Root;
            IReadOnlyCollection<ContainerEntry> listing = await original.GetEntriesAsync();
            string[] files = listing.Where(e => !e.IsDirectory)
                .Select(e => e.RelativePath).ToArray();
            if (files.Length == 0)
                throw new InvalidDataException("Archive contains no files.");

            List<DirectoryProbeResult>? directoryProbes = null;
            if (options.ResumeProbePasses > 0)
            {
                // Synthetic ZIP entries contain only files; their parent directories
                // are implicit. Enumerate the actual mounted tree so the probe
                // covers every directory checked on resume, not just ZIP entries
                // explicitly marked as directories.
                string[] directories = Directory.GetDirectories(
                    cold.Root,
                    "*",
                    SearchOption.AllDirectories
                );
                if (directories.Length == 0)
                    throw new InvalidOperationException("No directories available for resume probe.");
                directoryProbes = CompareDirectoryProbes(directories, options.ResumeProbePasses);
            }

            await Measure("random-file-reads", () => RandomReads(cold, files, options.RandomReads), counts, phases);
            await Measure("hydrate-all", () => cold.HydrateAllAsync(), counts, phases);
            if (options.RepeatHydrations > 0)
            {
                await Measure(
                    "rehydrate-existing-files",
                    () => RepeatHydrations(cold, files, options.RepeatHydrations),
                    counts,
                    phases
                );
            }

            await Measure("dispose-cold", async () => await cold.DisposeAsync(), counts, phases);
            active = null;

            active = factory.Open(MountOptions(original, mountBase));
            ICloudMount same = active;
            await Measure("warm-resume-same-container", () => same.MountAsync(), counts, phases);
            await Measure("dispose-same-container", async () => await same.DisposeAsync(), counts, phases);
            active = null;

            ZipContainer fresh = Container(archivePath, options, client, counts);
            active = factory.Open(MountOptions(fresh, mountBase));
            ICloudMount changed = active;
            await Measure("warm-resume-new-container", () => changed.MountAsync(), counts, phases);
            await Measure("dispose-new-container", async () => await changed.DisposeAsync(), counts, phases);
            active = null;

            string json = JsonSerializer.Serialize(
                new
                {
                    SchemaVersion = 1,
                    TimestampUtc = DateTimeOffset.UtcNow,
                    Runtime = RuntimeInformation.FrameworkDescription,
                    OperatingSystem = RuntimeInformation.OSDescription,
                    ProcessArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
                    Source = options.HttpUrl is null ? "file" : "http",
                    Artifact = options.Archive is null ? "synthetic" : "provided",
                    FileCount = files.Length,
                    options.BytesPerFile,
                    options.RandomReads,
                    options.FilesPerDirectory,
                    options.RepeatHydrations,
                    DirectoryProbes = directoryProbes,
                    TrustedResume = true,
                    ValidateEntryMetadata = false,
                    Phases = phases,
                },
                new JsonSerializerOptions { WriteIndented = true }
            );

            Console.WriteLine(json);
            if (options.Output is not null)
            {
                string path = Path.GetFullPath(options.Output);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await File.WriteAllTextAsync(path, json);
            }
        }
        finally
        {
            await CleanupBenchmarkAsync(active, mountRoot, scratch, manager);
        }
    }

    private static async Task CleanupBenchmarkAsync(
        ICloudMount? active,
        string? mountRoot,
        string scratch,
        ISyncRootManager manager
    )
    {
        // Never let cleanup mask the benchmark's original exception. In particular,
        // do not delete a folder if a live CFAPI connection failed to disconnect.
        if (active is not null)
        {
            try
            {
                await active.DisposeAsync();
            }
            catch (Exception error)
            {
                Console.Error.WriteLine($"Benchmark cleanup: disconnect failed; leaving {scratch}: {error}");
                return;
            }
        }

        try
        {
            if (mountRoot is not null && Directory.Exists(mountRoot))
                manager.Unregister(mountRoot);

            // Covers a partial initial mount which created a root before throwing.
            manager.CleanupMounts(scratch);
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"Benchmark cleanup: unregister failed; leaving {scratch}: {error}");
            return;
        }

        try
        {
            // Scratch is always a newly generated GUID child, never a caller-owned directory.
            Directory.Delete(scratch, recursive: true);
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"Benchmark cleanup: unable to remove {scratch}: {error}");
        }
    }

    private static HttpClient? CreateHttpClient(Options options, Counters counts)
    {
        if (options.HttpUrl is null)
            return null;

        return new HttpClient(
            new RangeCounterHandler(
                new HttpClientHandler { AutomaticDecompression = DecompressionMethods.None },
                counts
            )
        );
    }

    private static CloudMountOptions MountOptions(ZipContainer container, string path) => new()
    {
        Container = container,
        Root = path,
        TrustedContentId = "b17da7a1",
        SkipResumeValidation = true,
    };

    private static ZipContainer Container(
        string path,
        Options options,
        HttpClient? client,
        Counters counts
    )
    {
        if (client is not null)
            return ZipContainer.FromHttp(client, new Uri(options.HttpUrl!, UriKind.Absolute), validateEntryMetadata: false);

        return new ZipContainer(
            _ =>
            {
                FileStream stream = File.OpenRead(path);
                counts.CountFileOpen();
                return new ReadCounterStream(stream, counts);
            },
            validateEntryMetadata: false
        );
    }

    private static async Task RandomReads(ICloudMount mount, string[] files, int count)
    {
        var random = new Random(5271);
        byte[] buffer = new byte[4096];
        for (int i = 0; i < count; i++)
        {
            string path = mount.Resolve(files[random.Next(files.Length)]);
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                4096,
                FileOptions.Asynchronous
            );
            _ = await stream.ReadAsync(buffer);
        }
    }

    private static async Task RepeatHydrations(ICloudMount mount, string[] files, int repetitions)
    {
        for (int i = 0; i < repetitions; i++)
            await mount.HydrateAsync(files[i % files.Length]).ConfigureAwait(false);
    }

    private static async Task Measure(
        string name,
        Func<Task> action,
        Counters counts,
        List<PhaseResult> phases
    )
    {
        Snapshot before = counts.Capture();
        long allocations = GC.GetTotalAllocatedBytes(precise: true);
        using Process process = Process.GetCurrentProcess();
        process.Refresh();
        long workingSet = process.WorkingSet64;
        var timer = Stopwatch.StartNew();
        await action();
        timer.Stop();
        process.Refresh();
        Snapshot after = counts.Capture();
        phases.Add(
            new PhaseResult(
                name,
                timer.Elapsed.TotalMilliseconds,
                GC.GetTotalAllocatedBytes(precise: true) - allocations,
                process.WorkingSet64 - workingSet,
                process.PeakWorkingSet64,
                after.FileOpens - before.FileOpens,
                after.FileBytes - before.FileBytes,
                after.RangeRequests - before.RangeRequests,
                after.RangeBytes - before.RangeBytes
            )
        );
    }

    // Optional A/B comparison on the same mounted directories, outside the
    // timed mount phases. Alternate pass ordering to reduce warm-cache bias.
    private static List<DirectoryProbeResult> CompareDirectoryProbes(string[] paths, int passes)
    {
        var samples = new List<DirectoryProbeResult>(passes * 2);
        for (int pass = 0; pass < passes; pass++)
        {
            for (int order = 0; order < 2; order++)
            {
                bool singleProbe = (pass + order) % 2 == 0;
                long allocationStart = GC.GetTotalAllocatedBytes(precise: true);
                Stopwatch clock = Stopwatch.StartNew();
                foreach (string path in paths)
                {
                    bool valid;
                    if (singleProbe)
                    {
                        FileAttributes attributes = File.GetAttributes(path);
                        valid = (attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint))
                            == FileAttributes.Directory;
                    }
                    else
                    {
                        valid = Directory.Exists(path)
                            && (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0;
                    }

                    if (!valid)
                        throw new IOException($"Resume directory failed probe: {path}");
                }

                clock.Stop();
                samples.Add(new DirectoryProbeResult(
                    singleProbe ? "single-get-attributes" : "exists-then-attributes",
                    pass,
                    paths.Length,
                    clock.Elapsed.TotalMilliseconds,
                    GC.GetTotalAllocatedBytes(precise: true) - allocationStart
                ));
            }
        }

        return samples;
    }

    private sealed record DirectoryProbeResult(
        string Mode,
        int Pass,
        int DirectoryCount,
        double Milliseconds,
        long AllocatedBytesProcessWide
    );

    private static void GenerateArchive(string path, int entries, int bytesPerFile, int filesPerDirectory)
    {
        var content = new byte[bytesPerFile];
        new Random(1234).NextBytes(content);
        using var output = File.Create(path);
        using var zip = new ZipArchive(output, ZipArchiveMode.Create);
        for (int i = 0; i < entries; i++)
        {
            ZipArchiveEntry entry = zip.CreateEntry(
                $"build/{i / filesPerDirectory:D5}/file-{i:D7}.bin",
                CompressionLevel.NoCompression
            );
            using Stream writer = entry.Open();
            writer.Write(content);
        }
    }

    private readonly record struct Snapshot(
        long FileOpens,
        long FileBytes,
        long RangeRequests,
        long RangeBytes
    );

    private sealed record PhaseResult(
        string Phase,
        double Milliseconds,
        long AllocatedBytesProcessWide,
        long WorkingSetDeltaBytes,
        long PeakWorkingSetBytesProcessLifetime,
        long LocalArchiveOpens,
        long LocalArchiveReadBytes,
        long HttpRangeRequests,
        long HttpRequestedRangeBytes
    );

    private sealed class Counters
    {
        private long _fileOpens;
        private long _fileBytes;
        private long _rangeRequests;
        private long _rangeBytes;

        public void CountFileOpen() => Interlocked.Increment(ref _fileOpens);

        public void CountFileBytes(long size) => Interlocked.Add(ref _fileBytes, size);

        public void CountRangeRequest() => Interlocked.Increment(ref _rangeRequests);

        public void CountRangeBytes(long size) => Interlocked.Add(ref _rangeBytes, size);

        public Snapshot Capture() => new(
            Interlocked.Read(ref _fileOpens),
            Interlocked.Read(ref _fileBytes),
            Interlocked.Read(ref _rangeRequests),
            Interlocked.Read(ref _rangeBytes)
        );
    }

    private sealed class RangeCounterHandler(HttpMessageHandler inner, Counters counters)
        : DelegatingHandler(inner)
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            HttpResponseMessage response = await base.SendAsync(request, cancellationToken);
            if (request.Headers.Range is { } range)
            {
                counters.CountRangeRequest();
                foreach (var item in range.Ranges)
                {
                    if (item.From.HasValue && item.To.HasValue)
                        counters.CountRangeBytes(item.To.Value - item.From.Value + 1);
                }
            }

            return response;
        }
    }

    private sealed class ReadCounterStream(Stream inner, Counters counters) : Stream
    {
        public override bool CanRead => inner.CanRead;

        public override bool CanSeek => inner.CanSeek;

        public override bool CanWrite => false;

        public override long Length => inner.Length;

        public override long Position
        {
            get => inner.Position;
            set => inner.Position = value;
        }

        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);

        public override void Flush() => inner.Flush();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count)
        {
            int read = inner.Read(buffer, offset, count);
            counters.CountFileBytes(read);
            return read;
        }

        public override int Read(Span<byte> buffer)
        {
            int read = inner.Read(buffer);
            counters.CountFileBytes(read);
            return read;
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            int read = await inner.ReadAsync(buffer, cancellationToken);
            counters.CountFileBytes(read);
            return read;
        }

        public override Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken
        ) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                inner.Dispose();
            base.Dispose(disposing);
        }
    }

    private sealed record Options(
        int Entries,
        int BytesPerFile,
        int RandomReads,
        int FilesPerDirectory,
        int ResumeProbePasses,
        int RepeatHydrations,
        string? Archive,
        string? HttpUrl,
        string? WorkParent,
        string? Output
    )
    {
        public static Options Parse(string[] args)
        {
            int entries = 10000, fileBytes = 1024, reads = 128;
            int filesPerDirectory = 100, probePasses = 0, repeatHydrations = 0;
            string? archive = null, url = null, parent = null, output = null;
            for (int i = 0; i < args.Length; i += 2)
            {
                if (i + 1 >= args.Length)
                    throw new ArgumentException($"Missing value for {args[i]}");
                string value = args[i + 1];
                switch (args[i])
                {
                    case "--entries": entries = int.Parse(value); break;
                    case "--bytes-per-file": fileBytes = int.Parse(value); break;
                    case "--random-reads": reads = int.Parse(value); break;
                    case "--files-per-directory": filesPerDirectory = int.Parse(value); break;
                    case "--resume-probe-passes": probePasses = int.Parse(value); break;
                    case "--repeat-hydrations": repeatHydrations = int.Parse(value); break;
                    case "--archive": archive = Path.GetFullPath(value); break;
                    case "--http-url": url = value; break;
                    case "--work-parent": parent = value; break;
                    case "--output": output = value; break;
                    default: throw new ArgumentException($"Unknown option: {args[i]}");
                }
            }

            if (entries < 1 || entries > 250000 || fileBytes < 1
                || fileBytes > 1048576 || reads < 0
                || filesPerDirectory < 1 || probePasses < 0 || probePasses > 50
                || repeatHydrations < 0 || repeatHydrations > 1000000)
                throw new ArgumentOutOfRangeException(nameof(args), "Invalid benchmark size.");
            if (
                url is not null
                && (
                    archive is null
                    || !Uri.TryCreate(url, UriKind.Absolute, out Uri? parsed)
                    || parsed.Scheme is not ("http" or "https")
                )
            )
                throw new ArgumentException("HTTP mode requires --archive and an HTTP(S) URL for the identical ZIP.");
            return new Options(
                entries, fileBytes, reads, filesPerDirectory, probePasses, repeatHydrations,
                archive, url, parent, output
            );
        }
    }
}
