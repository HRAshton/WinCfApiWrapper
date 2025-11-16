using WinCfApiWrapper.Containers;
using Xunit;

namespace WinCfApiWrapper.Tests.Archives;

public sealed class ContainerPathTests
{
    [Theory]
    [InlineData("../escape.txt")]
    [InlineData("a//b.txt")]
    [InlineData("C:/temp/file.txt")]
    [InlineData("/root/file.txt")]
    [InlineData("a/./b.txt")]
    [InlineData("a/CON.txt")]
    [InlineData("a/foo. ")]
    [InlineData("a/file?.txt")]
    public void Normalize_UnsafePath_Throws(string path)
    {
        Assert.Throws<InvalidDataException>(() => ContainerPath.Normalize(path));
    }

    [Theory]
    [InlineData("folder/file.txt", @"folder\file.txt")]
    [InlineData("folder/sub/", @"folder\sub")]
    public void Normalize_ValidPath_ReturnsWindowsRelativePath(string path, string expected)
    {
        Assert.Equal(expected, ContainerPath.Normalize(path));
    }
}
