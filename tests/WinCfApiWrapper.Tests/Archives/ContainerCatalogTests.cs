using System.Text;
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
        Assert.Throws<InvalidDataException>(() => new ContainerCatalog(entries!, "1234abcd"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("1234567")]
    [InlineData("123456789")]
    [InlineData("1234abcg")]
    [InlineData("1234abc/")]
    public void TrustedIdentity_RejectsInvalidIdentifiers(string identity)
    {
        ArgumentException error = Assert.Throws<ArgumentException>(() =>
            new ContainerCatalog([], identity)
        );
        Assert.Equal("trustedContentId", error.ParamName);
    }

    [Theory]
    [InlineData("1234abcd")]
    [InlineData("1234ABCD")]
    public void TrustedIdentity_ReplacesListingHash_WhilePreservingEntries(string identity)
    {
        var catalog = new ContainerCatalog([new ContainerEntry("file", 4, "v1")], identity);
        Assert.Equal(identity, catalog.Fingerprint);
        Assert.Equal(4, Assert.Single(catalog.Entries.Values).Length);
    }

    [Fact]
    public void Constructor_NullEntryVersionIsRejectedAtBoundary()
    {
        ContainerEntry entry = new("file", 0, null!);

        Assert.Throws<ArgumentNullException>(() => new ContainerCatalog([entry]));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Constructor_RejectsDeepPathBeforeExpandingParents(bool directory)
    {
        string name = string.Concat(Enumerable.Repeat("a/", 29_000)) + "f";
        var entry = new ContainerEntry(name, 0, directory ? string.Empty : "v1", directory);

        Assert.Throws<InvalidDataException>(() => new ContainerCatalog([entry], "1234abcd"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Constructor_UsesUtf8BytesNotUtf16LengthAtIdentityBoundary(bool directory)
    {
        string prefix = string.Join('/', Enumerable.Repeat(new string('é', 200), 10));
        string boundary = prefix + "/" + new string('x', 86);
        Assert.Equal(4096, Encoding.UTF8.GetByteCount(boundary));
        string rejected = boundary + "y";

        string version = directory ? string.Empty : "v1";
        var accepted = new ContainerCatalog(
            [new ContainerEntry(boundary, 0, version, directory)],
            "1234abcd"
        );
        Assert.Equal(
            1,
            directory
                ? accepted.Directories.Count(d => d == boundary.Replace('/', '\\'))
                : accepted.Entries.Count
        );
        Assert.Throws<InvalidDataException>(() =>
            new ContainerCatalog([new ContainerEntry(rejected, 0, version, directory)], "1234abcd")
        );
    }

    [Fact]
    public void SharedParents_KeepStableDirectorySetAndFingerprint()
    {
        var catalog = new ContainerCatalog([
            new ContainerEntry("A/Sub/one.txt", 1, "v1"),
            new ContainerEntry("A/Sub/two.txt", 2, "v2"),
            new ContainerEntry("empty/nested/", 0, string.Empty, true),
        ]);

        Assert.Equal(4, catalog.Directories.Count);
        Assert.Contains(@"A\Sub", catalog.Directories);
        Assert.Contains(@"A", catalog.Directories);
        Assert.Contains(@"empty\nested", catalog.Directories);
        Assert.Equal(
            "ECBF9612088E6BBD0D7A689AD56327F1B778CB6DB466C1BE49309C25829753F7",
            catalog.Fingerprint
        );
    }

    [Fact]
    public void SharedParentAcrossCaseVariants_IsNotExpandedAgain()
    {
        ContainerEntry[] entries =
        [
            new("Dir/Sub/one.txt", 1, "v1"),
            new("DIR/SUB/two.txt", 2, "v2"),
        ];
        var catalog = new ContainerCatalog(entries, "1234abcd");

        Assert.Equal(2, catalog.Directories.Count);
        Assert.Contains(@"Dir\Sub", catalog.Directories);
        Assert.Contains(@"Dir", catalog.Directories);
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
