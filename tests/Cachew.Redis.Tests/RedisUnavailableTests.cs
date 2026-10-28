using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;

namespace Cachew.Redis.Tests;

/// <summary>
/// What happens when Redis is not there. The distributed layer is best effort, so an outage has to
/// degrade rather than throw: a read becomes a miss, a write does nothing, and the factory still runs.
/// </summary>
public class RedisUnavailableTests
{
    [Fact]
    public async Task An_unavailable_redis_degrades_to_misses_instead_of_failing()
    {
        await using var connection = await ConnectToNothing();

        var services = new ServiceCollection();
        services.AddSingleton(connection);
        services.AddCachewRedis();
        services.AddCachew();
        using var provider = services.BuildServiceProvider();
        var cache = provider.GetRequiredService<ICachew>();
        var calls = 0;

        // A read degrades to a miss rather than throwing.
        Assert.Null(await cache.GetAsync<string>("key"));

        // A write does not throw.
        await cache.SetAsync("key", "value");

        // And the factory still runs, which is the point of best effort.
        var value = await cache.GetOrCreateAsync("key", _ => Task.FromResult(++calls));

        Assert.Equal(1, value);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task A_connection_string_registration_resolves_while_redis_is_down()
    {
        var services = new ServiceCollection();
        services.AddCachewRedis($"127.0.0.1:{ClosedPort()},connectTimeout=500,syncTimeout=500,asyncTimeout=500");
        using var provider = services.BuildServiceProvider();

        var cache = provider.GetRequiredService<ICachew>();

        Assert.Equal("value", await cache.GetOrCreateAsync("key", _ => Task.FromResult("value")));
    }

    private static async Task<IConnectionMultiplexer> ConnectToNothing()
        => await ConnectionMultiplexer.ConnectAsync(new ConfigurationOptions
        {
            EndPoints = { { "127.0.0.1", ClosedPort() } },
            ConnectTimeout = 500,
            SyncTimeout = 500,
            AsyncTimeout = 500,
            AbortOnConnectFail = false,
        });

    private static int ClosedPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
