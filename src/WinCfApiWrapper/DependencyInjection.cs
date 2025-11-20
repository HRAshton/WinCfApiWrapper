using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using WinCfApiWrapper.Mounting;
using WinCfApiWrapper.Public;

namespace WinCfApiWrapper;

/// <summary>
/// Registration for host services.
/// </summary>
public static class DependencyInjection
{
    /// <summary>
    /// Adds the mount factory and sync-root manager to a service collection.
    /// </summary>
    /// <param name="services">The service collection to add the library services to.</param>
    /// <returns>The same service collection so registrations can be chained.</returns>
    public static IServiceCollection AddWinCfApiWrapper(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        return services
            .AddSingleton<ICloudMountFactory>(provider => new CloudMountFactory(
                provider.GetService<ILogger<CloudMount>>() ?? NullLogger<CloudMount>.Instance
            ))
            .AddSingleton<ISyncRootManager>(provider => new SyncRootManager(
                provider.GetService<ILogger<SyncRootManager>>()
                    ?? NullLogger<SyncRootManager>.Instance
            ));
    }
}
