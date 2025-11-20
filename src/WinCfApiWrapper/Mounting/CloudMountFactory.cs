using Microsoft.Extensions.Logging;
using WinCfApiWrapper.Public;

namespace WinCfApiWrapper.Mounting;

internal sealed class CloudMountFactory : ICloudMountFactory
{
    private readonly ILogger<CloudMount> _logger;

    public CloudMountFactory(ILogger<CloudMount> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public ICloudMount Open(CloudMountOptions options)
    {
        return new CloudMount(options, _logger);
    }
}
