using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.AetherAnalysis.Tests;

public sealed class ServiceRegistratorTests
{
    [Fact]
    public void RegisterServicesDoesNotRequireInitializedPluginInstance()
    {
        var services = new ServiceCollection();

        new ServiceRegistrator().RegisterServices(services, null!);

        Assert.NotEmpty(services);
    }
}
