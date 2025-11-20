using WinCfApiWrapper.Containers;
using WinCfApiWrapper.Public;
using WinCfApiWrapper.Public.Containers;

namespace WinCfApiWrapper.Tests.Hydration;

internal static class TestZipContainer
{
    public static (ContainerCatalog Catalog, IContentContainer Container) Create(string path)
    {
        var container = new ZipContainer(_ => File.OpenRead(path));
        IReadOnlyCollection<ContainerEntry> entries = container
            .GetEntriesAsync()
            .AsTask()
            .GetAwaiter()
            .GetResult();

        return (new ContainerCatalog(entries), container);
    }
}
