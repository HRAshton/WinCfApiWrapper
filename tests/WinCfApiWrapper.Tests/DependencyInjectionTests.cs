using Microsoft.Extensions.DependencyInjection;
using WinCfApiWrapper.Public;
using WinCfApiWrapper.Public.Containers;
using Xunit;

namespace WinCfApiWrapper.Tests;

public sealed class DependencyInjectionTests
{
    [Fact]
    public async Task AddWinCfApiWrapper_RegistersReusableFactoryAndManagerWithoutLoggingSetup()
    {
        await using ServiceProvider provider = new ServiceCollection()
            .AddWinCfApiWrapper()
            .BuildServiceProvider();

        ICloudMountFactory factory = provider.GetRequiredService<ICloudMountFactory>();
        Assert.Same(factory, provider.GetRequiredService<ICloudMountFactory>());
        Assert.Same(
            provider.GetRequiredService<ISyncRootManager>(),
            provider.GetRequiredService<ISyncRootManager>()
        );
        await using ICloudMount mount = factory.Open(
            new CloudMountOptions
            {
                Root = Path.Combine(Path.GetTempPath(), "rzm-factory-test"),
                Container = new ZipContainer(_ =>
                    File.OpenRead(Path.Combine(Path.GetTempPath(), "source.zip"))
                ),
            }
        );
        Assert.Equal(MountState.Created, mount.State);
    }
}
