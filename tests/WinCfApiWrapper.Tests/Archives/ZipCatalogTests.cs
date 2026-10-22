using System.IO.Compression;
using System.Text;
using WinCfApiWrapper.Archives;
using Xunit;

namespace WinCfApiWrapper.Tests.Archives;

public sealed class ZipCatalogTests
{
    [Fact]
    public void Constructor_NullSource_ThrowsArgumentNullException()
    {
        ArgumentNullException error = Assert.Throws<ArgumentNullException>(() =>
            new ZipCatalog(null!, maxEntries: 1));

        Assert.Equal("stream", error.ParamName);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Constructor_NonPositiveEntryLimit_RejectsAndDisposesSource(int maxEntries)
    {
        using MemoryStream source = BuildArchive("file.txt");

        ArgumentOutOfRangeException error = Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ZipCatalog(source, maxEntries)
        );

        Assert.Equal("maxEntries", error.ParamName);
        Assert.False(source.CanRead);
    }

    [Fact]
    public void Constructor_ValidArchive_IndexesFilesAndDirectories()
    {
        using MemoryStream stream = BuildArchive("dir/a.txt", "dir/nested/b.txt");
        var catalog = new ZipCatalog(stream, maxEntries: 2);
        Assert.True(catalog.Contains("dir/a.txt"));
        Assert.Contains(@"dir\nested", catalog.Directories);
        Assert.Equal(2, catalog.Entries.Count);
        Assert.Equal(64, catalog.Fingerprint.Length);
        Assert.Same(catalog.Fingerprint, catalog.Fingerprint);
    }

    [Fact]
    public void Constructor_ValidArchive_ReleasesIndexStreamBeforeReturning()
    {
        MemoryStream stream = BuildArchive("dir/a.txt");

        var catalog = new ZipCatalog(stream, maxEntries: 1);

        Assert.False(stream.CanRead);
        Assert.True(catalog.Contains("dir/a.txt"));
    }

    [Fact]
    public void Constructor_DuplicateCaseInsensitivePath_Throws()
    {
        using MemoryStream stream = BuildArchive("a.txt", "A.txt");
        Assert.Throws<InvalidDataException>(() => new ZipCatalog(stream, maxEntries: 2));
        Assert.False(stream.CanRead);
    }

    [Fact]
    public void Constructor_FileConflictsWithDirectory_Throws()
    {
        using MemoryStream stream = BuildArchive("folder", "folder/file.txt");
        Assert.Throws<InvalidDataException>(() => new ZipCatalog(stream, maxEntries: 2));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Constructor_DeepPathFailsWithoutExpandingThousandsOfParents(bool directory)
    {
        string path = string.Concat(Enumerable.Repeat("a/", 29_000)) + "f";
        if (directory)
            path += "/";

        using MemoryStream archive = BuildArchive(path);
        Assert.Throws<InvalidDataException>(() => new ZipCatalog(archive, maxEntries: 1));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Constructor_Utf8IdentityBudgetRespectsByteBoundary(bool directory)
    {
        string prefix = string.Join('/', Enumerable.Repeat(new string('é', 200), 10));
        string name = prefix + "/" + new string('x', 86);
        Assert.Equal(4096, Encoding.UTF8.GetByteCount(name));
        string suffix = directory ? "/" : string.Empty;

        using (MemoryStream accepted = BuildArchive(name + suffix))
        {
            var catalog = new ZipCatalog(accepted, maxEntries: 1);
            Assert.Equal(1, directory ? catalog.Directories.Count(d => d == name.Replace('/', '\\')) : catalog.Entries.Count);
        }

        using MemoryStream rejected = BuildArchive(name + "y" + suffix);
        Assert.Throws<InvalidDataException>(() => new ZipCatalog(rejected, maxEntries: 1));
    }

    [Fact]
    public void Constructor_SameArchive_ProducesStableFingerprint()
    {
        byte[] bytes;
        using (MemoryStream stream = BuildArchive("a.txt"))
            bytes = stream.ToArray();

        var first = new ZipCatalog(new MemoryStream(bytes), maxEntries: 1);
        var second = new ZipCatalog(new MemoryStream(bytes), maxEntries: 1);
        Assert.Equal(first.Fingerprint, second.Fingerprint);
    }

    [Fact]
    public void Fingerprint_IncludesEmptyDirectories()
    {
        using MemoryStream firstStream = BuildArchive();
        using var secondStream = new MemoryStream();
        using (var archive = new ZipArchive(secondStream, ZipArchiveMode.Create, true))
            archive.CreateEntry("empty/");

        secondStream.Position = 0;

        var first = new ZipCatalog(firstStream, maxEntries: 1);
        var second = new ZipCatalog(secondStream, maxEntries: 1);
        Assert.NotEqual(first.Fingerprint, second.Fingerprint);
    }

    private static MemoryStream BuildArchive(params string[] names)
    {
        var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, true))
        {
            foreach (string name in names)
            {
                using var writer = new StreamWriter(archive.CreateEntry(name).Open());
                writer.Write("hello");
            }
        }

        stream.Position = 0;

        return stream;
    }
}
