using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using StackExchange.Redis;

namespace Cachew.Redis;

/// <summary>Registration helpers for <see cref="RedisDistributedCache"/>.</summary>
/// <remarks>
/// Every registration uses <c>TryAdd</c>, so an <see cref="IDistributedCache"/> or
/// <see cref="IConnectionMultiplexer"/> that is already registered is left as it is. Call this
/// before another provider that also uses <c>TryAdd</c>, such as <c>AddStackExchangeRedisCache</c>,
/// if you want this cache to be the one that is used.
/// </remarks>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// The connection string name read by <see cref="AddCachewRedis(IServiceCollection)"/>,
    /// that is <c>ConnectionStrings:Cachew</c>.
    /// </summary>
    public const string DefaultConnectionStringName = "Cachew";

    /// <summary>
    /// Registers a Redis backed <see cref="IDistributedCache"/> using the
    /// <c>ConnectionStrings:Cachew</c> entry from the <see cref="IConfiguration"/> registered in the
    /// container. An <see cref="IConnectionMultiplexer"/> that is already registered is used
    /// instead.
    /// </summary>
    /// <param name="services">The service collection.</param>
    public static IServiceCollection AddCachewRedis(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        AddConnectionMultiplexer(services, provider =>
            provider.GetRequiredService<IConfiguration>().GetConnectionString(DefaultConnectionStringName));
        return AddDistributedCache(services);
    }

    /// <summary>
    /// Registers a Redis backed <see cref="IDistributedCache"/> from a connection string. The
    /// connection is opened lazily on first use.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="connectionString">A StackExchange.Redis connection string.</param>
    /// <param name="configure">Optional callback to adjust the parsed connection options.</param>
    public static IServiceCollection AddCachewRedis(this IServiceCollection services, string connectionString, Action<ConfigurationOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrEmpty(connectionString);

        AddConnectionMultiplexer(services, _ => connectionString, configure);
        return AddDistributedCache(services);
    }

    /// <summary>
    /// Registers a Redis backed <see cref="IDistributedCache"/> from a connection string produced by
    /// <paramref name="connectionStringFactory"/>, which receives the <see cref="IConfiguration"/>
    /// registered in the container. Use this to read the connection string from wherever it lives.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="connectionStringFactory">Reads the connection string from configuration.</param>
    /// <param name="configure">Optional callback to adjust the parsed connection options.</param>
    public static IServiceCollection AddCachewRedis(this IServiceCollection services, Func<IConfiguration, string?> connectionStringFactory, Action<ConfigurationOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(connectionStringFactory);

        AddConnectionMultiplexer(services, provider => connectionStringFactory(provider.GetRequiredService<IConfiguration>()), configure);
        return AddDistributedCache(services);
    }

    /// <summary>
    /// Registers a Redis backed <see cref="IDistributedCache"/> using a multiplexer produced by
    /// <paramref name="connectionFactory"/>. Use this to share a connection that is created or
    /// owned elsewhere.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="connectionFactory">A factory that resolves the connection multiplexer.</param>
    public static IServiceCollection AddCachewRedis(this IServiceCollection services, Func<IServiceProvider, IConnectionMultiplexer> connectionFactory)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(connectionFactory);

        services.TryAddSingleton(connectionFactory);
        return AddDistributedCache(services);
    }

    private static IServiceCollection AddDistributedCache(IServiceCollection services)
    {
        services.TryAddSingleton<IDistributedCache, RedisDistributedCache>();
        return services;
    }

    private static void AddConnectionMultiplexer(IServiceCollection services, Func<IServiceProvider, string?> connectionStringFactory, Action<ConfigurationOptions>? configure = null)
    {
        services.TryAddSingleton<IConnectionMultiplexer>(provider =>
        {
            var connectionString = connectionStringFactory(provider);
            ArgumentException.ThrowIfNullOrEmpty(connectionString);

            var options = ConfigurationOptions.Parse(connectionString);
            configure?.Invoke(options);
            return ConnectionMultiplexer.Connect(options);
        });
    }
}
