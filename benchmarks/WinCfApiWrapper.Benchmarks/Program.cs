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
            Console.WriteLine("Options: --entries N --bytes-per-file N --random-reads N --archive ZIP --http-url URL --work-parent DIR --output JSON");
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
        using var client = options.HttpUrl is null
            ? null
            : new HttpClient(new RangeCounterHandler(new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.None,
            }, counts));
        using var services = new ServiceCollection().AddWinCfApiWrapper().BuildServiceProvider();
        ICloudMountFactory factory = services.GetRequiredService<ICloudMountFactory>();
        ISyncRootManager manager = services.GetRequiredService<ISyncRootManager>();
        ICloudMount? active = null;
        string? mountRoot = null;
        var phases = new List<PhaseResult>();

        try
        {
            if (options.Archive is null)
                GenerateArchive(archivePath, options.Entries, options.BytesPerFile);
            if (!File.Exists(archivePath))
                throw new FileNotFoundException("Archive not found", archivePath);

            // Separate indexing measurement does not prime the cold mount's cache.
            ZipContainer indexOnly = Container(archivePath, options, client, counts);
            await Measure("zip-index-only", async () => { _ = await indexOnly.GetEntriesAsync(); }, counts, phases);

            ZipContainer original = Container(archivePath, options, client, counts);
            active = factory.Open(MountOptions(original, mountBase));
            ICloudMount cold = active;
            await Measure("cold-mount", () => cold.MountAsync(), counts, phases);
            mountRoot = cold.Root;
            string[] files = (await original.GetEntriesAsync())
                .Where(e => !e.IsDirectory).Select(e => e.RelativePath).ToArray();
            if (files.Length == 0)
                throw new InvalidDataException("Archive contains no files.");

            await Measure("random-file-reads", () => RandomReads(cold, files, options.RandomReads), counts, phases);
            await Measure("hydrate-all", () => cold.HydrateAllAsync(), counts, phases);
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

            string json = JsonSerializer.Serialize(new
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
                TrustedResume = true,
                ValidateEntryMetadata = false,
                Phases = phases,
            }, new JsonSerializerOptions { WriteIndented = true });

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
            // Do not un-register an active connection if its native disconnect failed.
            if (active is not null)
                await active.DisposeAsync();
            if (mountRoot is not null && Directory.Exists(mountRoot))
                manager.Unregister(mountRoot);
            manager.CleanupMounts(scratch); // Covers a failed partial mount.
            Directory.Delete(scratch, recursive: true); // Scratch is always a generated GUID child.
        }
    }

    private static CloudMountOptions MountOptions(ZipContainer container, string path) => new()
    {
        Container = container,
        Root = path,
        TrustedContentId = "b17da7a1",
        SkipResumeValidation = true,
    };

    private static ZipContainer Container(string path, Options options, HttpClient? client, Counters counts)
    {
        if (client is not null)
            return ZipContainer.FromHttp(client, new Uri(options.HttpUrl!, UriKind.Absolute), validateEntryMetadata: false);

        return new ZipContainer(_ =>
        {
            Interlocked.Increment(ref counts.FileOpens);
            return new ReadCounterStream(File.OpenRead(path), counts);
        }, validateEntryMetadata: false);
    }

    private static async Task RandomReads(ICloudMount mount, string[] files, int count)
    {
        var random = new Random(5271);
        byte[] buffer = new byte[4096];
        for (int i = 0; i < count; i++)
        {
            string path = mount.Resolve(files[random.Next(files.Length)]);
            await using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                4096, FileOptions.Asynchronous);
            _ = await stream.ReadAsync(buffer);
        }
    }

    private static async Task Measure(string name, Func<Task> action, Counters counts, List<PhaseResult> phases)
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
        phases.Add(new PhaseResult(
            name, timer.Elapsed.TotalMilliseconds,
            GC.GetTotalAllocatedBytes(precise: true) - allocations,
            process.WorkingSet64 - workingSet, process.PeakWorkingSet64,
            after.FileOpens - before.FileOpens, after.FileBytes - before.FileBytes,
            after.RangeRequests - before.RangeRequests, after.RangeBytes - before.RangeBytes));
    }

    private static void GenerateArchive(string path, int entries, int bytesPerFile)
    {
        var content = new byte[bytesPerFile];
        new Random(1234).NextBytes(content);
        using var output = File.Create(path);
        using var zip = new ZipArchive(output, ZipArchiveMode.Create);
        for (int i = 0; i < entries; i++)
        {
            ZipArchiveEntry entry = zip.CreateEntry(
                $"build/{i / 100:D5}/file-{i:D7}.bin", CompressionLevel.NoCompression);
            using Stream writer = entry.Open();
            writer.Write(content);
        }
    }

    private sealed record PhaseResult(
        string Phase, double Milliseconds, long AllocatedBytesProcessWide,
        long WorkingSetDeltaBytes, long PeakWorkingSetBytesProcessLifetime,
        long LocalArchiveOpens, long LocalArchiveReadBytes,
        long HttpRangeRequests, long HttpRequestedRangeBytes);

    private readonly record struct Snapshot(
        long FileOpens, long FileBytes, long RangeRequests, long RangeBytes);

    private sealed class Counters
    {
        public long FileOpens;
        public long FileBytes;
        public long RangeRequests;
        public long RangeBytes;

        public Snapshot Capture() => new(
            Interlocked.Read(ref FileOpens), Interlocked.Read(ref FileBytes),
            Interlocked.Read(ref RangeRequests), Interlocked.Read(ref RangeBytes));
    }

    private sealed class RangeCounterHandler(HttpMessageHandler inner, Counters counters)
        : DelegatingHandler(inner)
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            HttpResponseMessage response = await base.SendAsync(request, cancellationToken);
            if (request.Headers.Range is { } range)
            {
                Interlocked.Increment(ref counters.RangeRequests);
                foreach (var item in range.Ranges)
                {
                    if (item.From.HasValue && item.To.HasValue)
                        Interlocked.Add(ref counters.RangeBytes, item.To.Value - item.From.Value + 1);
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
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void Flush() => inner.Flush();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count)
        {
            int read = inner.Read(buffer, offset, count);
            Interlocked.Add(ref counters.FileBytes, read);
            return read;
        }

        public override int Read(Span<byte> buffer)
        {
            int read = inner.Read(buffer);
            Interlocked.Add(ref counters.FileBytes, read);
            return read;
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            int read = await inner.ReadAsync(buffer, cancellationToken);
            Interlocked.Add(ref counters.FileBytes, read);
            return read;
        }

        public override Task<int> ReadAsync(
            byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                inner.Dispose();
            base.Dispose(disposing);
        }
    }

    private sealed record Options(
        int Entries, int BytesPerFile, int RandomReads, string? Archive,
        string? HttpUrl, string? WorkParent, string? Output)
    {
        public static Options Parse(string[] args)
        {
            int entries = 10000, fileBytes = 1024, reads = 128;
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
                    case "--archive": archive = Path.GetFullPath(value); break;
                    case "--http-url": url = value; break;
                    case "--work-parent": parent = value; break;
                    case "--output": output = value; break;
                    default: throw new ArgumentException($"Unknown option: {args[i]}");
                }
            }

            if (entries < 1 || entries > 250000 || fileBytes < 1
                || fileBytes > 1048576 || reads < 0)
                throw new ArgumentOutOfRangeException(nameof(args), "Invalid benchmark size.");
            if (url is not null &&
                (archive is null || !Uri.TryCreate(url, UriKind.Absolute, out Uri? parsed)
                    || parsed.Scheme is not ("http" or "https")))
                throw new ArgumentException("HTTP mode requires --archive and an HTTP(S) URL for the identical ZIP.");
            return new Options(entries, fileBytes, reads, archive, url, parent, output);
        }
    }
}
