using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;
using StackExchange.Redis;

namespace Cachew.Redis.Tests;

public class ServiceCollectionExtensionsTests
{
    [Fact]
    public void The_default_connection_string_name_is_read_from_configuration()
    {
        Assert.Equal("Cachew", ServiceCollectionExtensions.DefaultConnectionStringName);

        var services = new ServiceCollection();
        AddConfiguration(services, ("ConnectionStrings:Cachew", "localhost:6379"));

        services.AddCachewRedis();

        AssertConnectionAndCacheRegistered(services);
    }

    [Fact]
    public void A_missing_default_connection_string_fails_when_the_multiplexer_is_resolved()
    {
        var services = new ServiceCollection();
        AddConfiguration(services, ("ConnectionStrings:Other", "localhost:6379"));

        services.AddCachewRedis();

        using var provider = services.BuildServiceProvider();
        Assert.ThrowsAny<ArgumentException>(() => provider.GetRequiredService<IConnectionMultiplexer>());
    }

    [Fact]
    public void A_connection_string_registers_the_multiplexer_and_the_cache()
    {
        var services = new ServiceCollection();

        services.AddCachewRedis("localhost:6379");

        AssertConnectionAndCacheRegistered(services);
    }

    [Fact]
    public void A_connection_string_factory_registers_the_multiplexer_and_the_cache()
    {
        var services = new ServiceCollection();
        AddConfiguration(services, ("ConnectionStrings:redis", "localhost:6379"));

        services.AddCachewRedis(configuration => configuration.GetConnectionString("redis"));

        AssertConnectionAndCacheRegistered(services);
    }

    [Fact]
    public void A_connection_string_factory_receives_configuration_and_an_empty_result_fails()
    {
        var services = new ServiceCollection();
        AddConfiguration(services, ("ConnectionStrings:redis", "localhost:6379"));
        IConfiguration? received = null;

        services.AddCachewRedis(configuration =>
        {
            received = configuration;
            return configuration.GetConnectionString("missing");
        });

        using var provider = services.BuildServiceProvider();
        Assert.ThrowsAny<ArgumentException>(() => provider.GetRequiredService<IConnectionMultiplexer>());

        Assert.NotNull(received);
        Assert.Equal("localhost:6379", received!.GetConnectionString("redis"));
    }

    [Fact]
    public void A_connection_factory_supplies_the_multiplexer()
    {
        var services = new ServiceCollection();
        var connection = Substitute.For<IConnectionMultiplexer>();

        services.AddCachewRedis(_ => connection);

        using var provider = services.BuildServiceProvider();
        Assert.Same(connection, provider.GetRequiredService<IConnectionMultiplexer>());
        Assert.IsType<RedisDistributedCache>(provider.GetRequiredService<IDistributedCache>());
    }

    [Fact]
    public void An_existing_multiplexer_registration_is_kept()
    {
        var services = new ServiceCollection();
        var existing = Substitute.For<IConnectionMultiplexer>();
        services.AddSingleton(existing);

        services.AddCachewRedis(_ => Substitute.For<IConnectionMultiplexer>());

        using var provider = services.BuildServiceProvider();
        Assert.Same(existing, provider.GetRequiredService<IConnectionMultiplexer>());
    }

    [Fact]
    public void A_connection_string_overload_applies_the_cache_options()
    {
        var services = new ServiceCollection();

        services.AddCachewRedis("localhost:6379", options => options.KeyPrefix = "myapp:");

        using var provider = services.BuildServiceProvider();
        Assert.Equal("myapp:", provider.GetRequiredService<IOptions<CachewOptions>>().Value.KeyPrefix);
    }

    [Fact]
    public void One_registration_call_is_enough_for_the_cache()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Substitute.For<IConnectionMultiplexer>());

        services.AddCachewRedis();

        using var provider = services.BuildServiceProvider();
        Assert.NotNull(provider.GetRequiredService<ICachew>());
    }

    [Fact]
    public void The_cache_is_built_over_the_registered_multiplexer()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Substitute.For<IConnectionMultiplexer>());

        services.AddCachewRedis();

        using var provider = services.BuildServiceProvider();
        Assert.IsType<RedisDistributedCache>(provider.GetRequiredService<IDistributedCache>());
    }

    [Fact]
    public void The_container_owns_a_multiplexer_it_created()
    {
        var services = new ServiceCollection();
        var connection = Substitute.For<IConnectionMultiplexer>();
        services.AddCachewRedis(_ => connection);

        var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IConnectionMultiplexer>();
        provider.Dispose();

        connection.Received().Dispose();
    }

    [Fact]
    public void The_container_leaves_a_registered_multiplexer_alone()
    {
        var services = new ServiceCollection();
        var connection = Substitute.For<IConnectionMultiplexer>();
        services.AddSingleton(connection);
        services.AddCachewRedis();

        var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IConnectionMultiplexer>();
        provider.Dispose();

        Assert.DoesNotContain(connection.ReceivedCalls(), call => call.GetMethodInfo().Name == "Dispose");
    }

    private static void AssertConnectionAndCacheRegistered(IServiceCollection services)
    {
        Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(IConnectionMultiplexer));
        Assert.Contains(services, descriptor =>
            descriptor.ServiceType == typeof(IDistributedCache) &&
            descriptor.ImplementationType == typeof(RedisDistributedCache));
        Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(ICachew));
    }

    private static void AddConfiguration(IServiceCollection services, params (string Key, string Value)[] values)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values.ToDictionary(pair => pair.Key, pair => (string?)pair.Value))
            .Build();
        services.AddSingleton<IConfiguration>(configuration);
    }
}
