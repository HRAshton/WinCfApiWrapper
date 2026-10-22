using System.IO.Compression;
using WinCfApiWrapper.Public;
using WinCfApiWrapper.Public.Containers;
using Xunit;

namespace WinCfApiWrapper.Tests.Archives;

public sealed class ArchiveStressTests
{
    public static bool RunArchiveStress =>
        Environment.GetEnvironmentVariable("RZM_RUN_ARCHIVE_STRESS") == "1";

    [Fact(
        Skip = "Set RZM_RUN_ARCHIVE_STRESS=1 to run archive stress tests.",
        SkipUnless = nameof(RunArchiveStress)
    )]
    [Trait("Category", "ArchiveStress")]
    public async Task Zip_IndexesTwoHundredThousandFiles()
    {
        const int fileCount = 200_000;
        using var output = new MemoryStream();
        using (var writer = new ZipArchive(output, ZipArchiveMode.Create, true))
        {
            for (int i = 0; i < fileCount; i++)
                writer.CreateEntry($"file-{i:D6}.txt");
        }

        byte[] archive = output.ToArray();
        var container = new ZipContainer(_ => new MemoryStream(archive));

        IReadOnlyCollection<ContainerEntry> entries = await container.GetEntriesAsync(
            TestContext.Current.CancellationToken
        );

        Assert.Equal(fileCount, entries.Count);
        Assert.Contains(entries, entry => entry.RelativePath == "file-199999.txt");
        ContainerEntry last = entries.Single(entry => entry.RelativePath == "file-199999.txt");
        await using Stream opened = await container.OpenReadAsync(
            last,
            TestContext.Current.CancellationToken
        );
        Assert.Equal(-1, opened.ReadByte());
    }
}
