using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Cachew.Redis.Tests;

/// <summary>
/// The settings a deployment is most likely to change have to be drivable from configuration, so that
/// turning the cache off during an incident is a configuration change rather than a deployment. These
/// live in this project because it is the one with the configuration packages.
/// </summary>
public class CachewOptionsConfigurationTests
{
    [Fact]
    public void CachewOptions_BindFromAConfigurationSection()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Cachew:Enabled"] = "false",
                ["Cachew:KeyPrefix"] = "myapp:",
                ["Cachew:DefaultInMemoryTimeToLive"] = "00:00:30",
                ["Cachew:DefaultDistributedTimeToLive"] = "00:05:00",
            })
            .Build();

        var services = new ServiceCollection();
        services.Configure<CachewOptions>(configuration.GetSection("Cachew"));

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<CachewOptions>>().Value;

        Assert.False(options.Enabled);
        Assert.Equal("myapp:", options.KeyPrefix);
        Assert.Equal(TimeSpan.FromSeconds(30), options.DefaultInMemoryTimeToLive);
        Assert.Equal(TimeSpan.FromMinutes(5), options.DefaultDistributedTimeToLive);
    }
}
