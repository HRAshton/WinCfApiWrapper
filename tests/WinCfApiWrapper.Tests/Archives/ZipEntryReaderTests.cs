using System.IO.Compression;
using WinCfApiWrapper.Archives;
using Xunit;

namespace WinCfApiWrapper.Tests.Archives;

public sealed class ZipEntryReaderTests
{
    [Fact]
    public void OpenEntry_UnchangedArchive_ReturnsContentAndOwnsSource()
    {
        byte[] bytes = BuildArchive("docs/one.txt", "content");
        var catalog = new ZipCatalog(new MemoryStream(bytes));
        ZipEntryInfo entry = catalog.Entries["docs\\one.txt"];
        var source = new MemoryStream(bytes);

        using (var reader = new ZipEntryReader(source))
        using (var contents = new StreamReader(reader.OpenEntry(entry)))
            Assert.Equal("content", contents.ReadToEnd());

        Assert.False(source.CanRead);
    }

    [Fact]
    public void OpenEntry_ChangedMetadata_RejectsArchive()
    {
        byte[] original = BuildArchive("docs/one.txt", "original");
        var catalog = new ZipCatalog(new MemoryStream(original));
        ZipEntryInfo entry = catalog.Entries["docs\\one.txt"];
        using var reader = new ZipEntryReader(
            new MemoryStream(BuildArchive("docs/one.txt", "changed content"))
        );

        Assert.Throws<IOException>(() => reader.OpenEntry(entry));
    }

    [Fact]
    public void OpenEntry_MissingMember_ThrowsFileNotFound()
    {
        byte[] original = BuildArchive("docs/one.txt", "content");
        var catalog = new ZipCatalog(new MemoryStream(original));
        ZipEntryInfo entry = catalog.Entries["docs\\one.txt"];
        using var reader = new ZipEntryReader(
            new MemoryStream(BuildArchive("docs/two.txt", "content"))
        );

        Assert.Throws<FileNotFoundException>(() => reader.OpenEntry(entry));
    }

    [Fact]
    public void Constructor_InvalidArchive_DisposesSource()
    {
        var source = new MemoryStream([1, 2, 3]);

        Assert.Throws<InvalidDataException>(() => new ZipEntryReader(source));
        Assert.False(source.CanRead);
    }

    private static byte[] BuildArchive(string name, string content)
    {
        using var source = new MemoryStream();
        using (var archive = new ZipArchive(source, ZipArchiveMode.Create, true))
        using (var writer = new StreamWriter(archive.CreateEntry(name).Open()))
            writer.Write(content);

        return source.ToArray();
    }
}
