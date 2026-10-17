using WinCfApiWrapper.Containers;
using WinCfApiWrapper.Public;
using Xunit;

namespace WinCfApiWrapper.Tests.Archives;

public sealed class ContainerCatalogTests
{
    [Fact]
    public void Constructor_RejectsMissingEntryCollection()
    {
        Assert.Throws<ArgumentNullException>(() => new ContainerCatalog(null!));
    }

    [Theory]
    [InlineData("null entry")]
    [InlineData("empty path")]
    [InlineData("negative file length")]
    [InlineData("version line break")]
    [InlineData("file without version")]
    [InlineData("directory with length")]
    [InlineData("directory with version")]
    [InlineData("duplicate path")]
    [InlineData("file and directory conflict")]
    [InlineData("unsafe path")]
    public void Constructor_RejectsInvalidContainerEntries(string scenario)
    {
        ContainerEntry?[] entries = scenario switch
        {
            "null entry" => [null],
            "empty path" => [new ContainerEntry(string.Empty, 0, "version")],
            "negative file length" => [new ContainerEntry("file", -1, "version")],
            "version line break" => [new ContainerEntry("file", 0, "bad\nversion")],
            "file without version" => [new ContainerEntry("file", 0, " ")],
            "directory with length" => [new ContainerEntry("folder", 1, string.Empty, true)],
            "directory with version" => [new ContainerEntry("folder", 0, "version", true)],
            "duplicate path" =>
            [
                new ContainerEntry("file", 0, "first"),
                new ContainerEntry("FILE", 0, "second"),
            ],
            "file and directory conflict" =>
            [
                new ContainerEntry("folder", 0, "version"),
                new ContainerEntry("folder/child", 0, "version"),
            ],
            "unsafe path" => [new ContainerEntry("../escape", 0, "version")],
            _ => throw new ArgumentOutOfRangeException(nameof(scenario), scenario, null),
        };

        Assert.Throws<InvalidDataException>(() => new ContainerCatalog(entries!));
    }

    [Fact]
    public void Constructor_NullEntryVersionIsRejectedAtBoundary()
    {
        ContainerEntry entry = new("file", 0, null!);

        Assert.Throws<ArgumentNullException>(() => new ContainerCatalog([entry]));
    }

    [Fact]
    public void Catalog_NormalizesEntriesAndRejectsFileLookupsAsDirectories()
    {
        var catalog = new ContainerCatalog([
            new ContainerEntry("docs/", 0, string.Empty, true),
            new ContainerEntry("docs/readme.txt", 4, "v1"),
        ]);

        Assert.True(catalog.Contains("docs/readme.txt"));
        Assert.False(catalog.Contains("docs"));
        Assert.Equal("docs\\readme.txt", Assert.Single(catalog.Entries.Values).RelativePath);
        Assert.Contains("docs", catalog.Directories);
    }
}
