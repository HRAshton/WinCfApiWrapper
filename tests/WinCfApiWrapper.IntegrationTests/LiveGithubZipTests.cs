using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using WinCfApiWrapper.IntegrationTests.Fixtures;
using WinCfApiWrapper.Mounting;
using WinCfApiWrapper.Public;
using WinCfApiWrapper.Public.Containers;
using Xunit;

namespace WinCfApiWrapper.IntegrationTests;

// Opt-in: use --filter Category=Live and RZM_RUN_LIVE_TESTS=1.
[Collection("Windows sync roots")]
[Trait("Category", "Live")]
public sealed class LiveGithubZipTests
{
    private const string ArchiveUrl =
        "https://raw.githubusercontent.com/GuglielmoCerri/test-assets/60d27efb076dba774b483b3d4c4e7085749176c1/archives/sample.zip";

    private const string ArchiveHash =
        "2800182D0802EB9773A741D8F9707AF20980BE9EF1A05E20B0C7DAE7FD20343A";

    private const string SampleHash =
        "89045065793E6260AD953E33C231EE62A2A86207E824C0BB3A8831DC77F40B9E";

    private const string CsvHash =
        "5690D17928402728FE857961DDDF327A04A2E45C42BFA1D2721A8859D3C096B4";

    private static readonly HttpClient Client = new();

    [Fact]
    public async Task GithubArchive_ReadsMountedEntriesFromRemoteFactory()
    {
        if (Environment.GetEnvironmentVariable("RZM_RUN_LIVE_TESTS") != "1")
        {
            throw new InvalidOperationException(
                "Set RZM_RUN_LIVE_TESTS=1 to run live GitHub tests."
            );
        }

        string root = FixtureArchive.Root();
        int archiveOpens = 0;

        ICloudMount mount = new CloudMountFactory(NullLogger<CloudMount>.Instance).Open(
            new CloudMountOptions
            {
                Container = new ZipContainer(token =>
                {
                    byte[] archive = Client
                        .GetByteArrayAsync(ArchiveUrl, token)
                        .GetAwaiter()
                        .GetResult();
                    Assert.Equal(ArchiveHash, Convert.ToHexString(SHA256.HashData(archive)));

                    // Multiple fetch workers may call this factory concurrently.
                    // ReSharper disable once AccessToModifiedClosure
                    Interlocked.Increment(ref archiveOpens);

                    return new MemoryStream(archive, false);
                }),
                Root = root,
            }
        );
        bool mounted = false;
        try
        {
            await mount.MountAsync();
            mounted = true;
            Assert.Equal(MountState.Mounted, mount.State);
            Assert.Equal(1, Volatile.Read(ref archiveOpens));
            Assert.Equal(
                SampleHash,
                Convert.ToHexString(
                    SHA256.HashData(await File.ReadAllBytesAsync(mount.Resolve("sample.txt")))
                )
            );
            Assert.True(
                Volatile.Read(ref archiveOpens) > 1,
                "Reading a placeholder did not fetch the archive from GitHub again."
            );

            await mount.HydrateAllAsync();
            Assert.Equal(0, mount.Statistics.FailedFetches);
            Assert.Equal(
                CsvHash,
                Convert.ToHexString(
                    SHA256.HashData(await File.ReadAllBytesAsync(mount.Resolve("sample_data.csv")))
                )
            );
        }
        finally
        {
            await mount.DisposeAsync();
            if (mounted)
                FixtureArchive.CleanupRoot(mount.Root);
        }
    }
}
