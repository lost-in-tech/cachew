using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Cachew.Redis;

/// <summary>Registration helpers for a cache over Redis.</summary>
/// <remarks>
/// One call is enough: a Redis backed <see cref="IDistributedCache"/> is registered, and the cache over
/// it, so <c>AddCachew</c> does not have to be called as well. Every registration uses <c>TryAdd</c>, so
/// an <see cref="IDistributedCache"/>, an <see cref="IConnectionMultiplexer"/> or an
/// <see cref="ICachewBackplane"/> that is already registered is left as it is. Call this before another
/// provider that also uses <c>TryAdd</c>, such as <c>AddStackExchangeRedisCache</c>, if you want this
/// cache to be the one that is used.
/// </remarks>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// The connection string name read by <see cref="AddCachewRedis(IServiceCollection)"/>,
    /// that is <c>ConnectionStrings:Cachew</c>.
    /// </summary>
    public const string DefaultConnectionStringName = "Cachew";

    /// <summary>
    /// Registers the cache over Redis, using the <c>ConnectionStrings:Cachew</c> entry from the
    /// <see cref="IConfiguration"/> registered in the container. An
    /// <see cref="IConnectionMultiplexer"/> that is already registered is used instead.
    /// </summary>
    /// <param name="services">The service collection.</param>
    public static IServiceCollection AddCachewRedis(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        AddConnectionMultiplexer(services, provider =>
            provider.GetRequiredService<IConfiguration>().GetConnectionString(DefaultConnectionStringName));
        return AddRedisBackedCache(services, configure: null);
    }

    /// <summary>
    /// Registers the cache over Redis, using the given connection string. The connection is opened
    /// lazily on first use.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="connectionString">A StackExchange.Redis connection string.</param>
    /// <param name="configure">
    /// Optional callback to set the cache options, the same ones <see cref="CachewOptions"/> exposes.
    /// </param>
    /// <param name="configureConnection">
    /// Optional callback to adjust the parsed connection options. Tuning that the connection string
    /// cannot express, such as a reconnect retry policy, can also be done by registering a multiplexer
    /// yourself and using the overload that takes one.
    /// </param>
    public static IServiceCollection AddCachewRedis(
        this IServiceCollection services,
        string connectionString,
        Action<CachewOptions>? configure = null,
        Action<ConfigurationOptions>? configureConnection = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrEmpty(connectionString);

        AddConnectionMultiplexer(services, _ => connectionString, configureConnection);
        return AddRedisBackedCache(services, configure);
    }

    /// <summary>
    /// Registers the cache over Redis, using a connection string produced by
    /// <paramref name="connectionStringFactory"/>, which receives the <see cref="IConfiguration"/>
    /// registered in the container. Use this to read the connection string from wherever it lives.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="connectionStringFactory">Reads the connection string from configuration.</param>
    /// <param name="configure">
    /// Optional callback to set the cache options, the same ones <see cref="CachewOptions"/> exposes.
    /// </param>
    /// <param name="configureConnection">
    /// Optional callback to adjust the parsed connection options. Tuning that the connection string
    /// cannot express, such as a reconnect retry policy, can also be done by registering a multiplexer
    /// yourself and using the overload that takes one.
    /// </param>
    public static IServiceCollection AddCachewRedis(
        this IServiceCollection services,
        Func<IConfiguration, string?> connectionStringFactory,
        Action<CachewOptions>? configure = null,
        Action<ConfigurationOptions>? configureConnection = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(connectionStringFactory);

        AddConnectionMultiplexer(services, provider => connectionStringFactory(provider.GetRequiredService<IConfiguration>()), configureConnection);
        return AddRedisBackedCache(services, configure);
    }

    /// <summary>
    /// Registers the cache over Redis, using a multiplexer produced by
    /// <paramref name="connectionFactory"/>. Use this to build the connection yourself, with tuning that
    /// the connection string cannot express.
    /// </summary>
    /// <remarks>
    /// The container owns what the factory returns and disposes it, so to share a connection that
    /// something else owns, register the instance yourself and call an overload that does not take a
    /// factory: an <see cref="IConnectionMultiplexer"/> that is already registered is used as it is and
    /// is left alone.
    /// </remarks>
    /// <param name="services">The service collection.</param>
    /// <param name="connectionFactory">A factory that resolves the connection multiplexer.</param>
    /// <param name="configure">
    /// Optional callback to set the cache options, the same ones <see cref="CachewOptions"/> exposes.
    /// </param>
    public static IServiceCollection AddCachewRedis(
        this IServiceCollection services,
        Func<IServiceProvider, IConnectionMultiplexer> connectionFactory,
        Action<CachewOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(connectionFactory);

        services.TryAddSingleton(connectionFactory);
        return AddRedisBackedCache(services, configure);
    }

    /// <summary>
    /// Registers a Redis pub/sub backplane, so that invalidating a key on one instance also drops the
    /// in-memory copy held by the other instances.
    /// </summary>
    /// <remarks>
    /// Notifications are best effort: an instance that misses one stays stale until its entry
    /// expires. Requires an <see cref="IConnectionMultiplexer"/>, which
    /// <see cref="AddCachewRedis(IServiceCollection, string, Action{CachewOptions}?, Action{ConfigurationOptions}?)"/>
    /// and the other <c>AddCachewRedis</c> overloads register. A backplane that is already registered is
    /// kept, so a custom <see cref="ICachewBackplane"/> wins.
    /// </remarks>
    /// <param name="services">The service collection.</param>
    /// <param name="channel">The pub/sub channel every instance must agree on.</param>
    public static IServiceCollection AddCachewBackplane(this IServiceCollection services, string channel = RedisCacheBackplane.DefaultChannel)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrEmpty(channel);

        services.TryAddSingleton<ICachewBackplane>(provider => new RedisCacheBackplane(
            provider.GetRequiredService<IConnectionMultiplexer>(),
            channel,
            provider.GetService<ILogger<RedisCacheBackplane>>(),
            provider.GetService<IOptions<RedisCachewOptions>>()));

        return services;
    }

    /// <summary>
    /// Registers the Redis layer and the cache over it, which is why one <c>AddCachewRedis</c> call is
    /// enough. <c>AddCachew</c> uses <c>TryAdd</c> throughout, so calling it as well, to configure
    /// options or to register the cache yourself, still works.
    /// </summary>
    private static IServiceCollection AddRedisBackedCache(IServiceCollection services, Action<CachewOptions>? configure)
    {
        services.AddOptions<RedisCachewOptions>();
        services.TryAddSingleton<IDistributedCache, RedisDistributedCache>();
        services.AddCachew(configure);

        return services;
    }

    private static void AddConnectionMultiplexer(IServiceCollection services, Func<IServiceProvider, string?> connectionStringFactory, Action<ConfigurationOptions>? configure = null)
    {
        services.TryAddSingleton<IConnectionMultiplexer>(provider =>
        {
            var connectionString = connectionStringFactory(provider);
            ArgumentException.ThrowIfNullOrEmpty(connectionString);

            var options = ConfigurationOptions.Parse(connectionString);

            // The distributed layer is best effort, so Redis being down must neither stop the cache
            // from resolving nor hold each command in the backlog until it times out: failing at once
            // turns a read into a plain miss. The multiplexer keeps reconnecting in the background.
            options.AbortOnConnectFail = false;
            options.BacklogPolicy = BacklogPolicy.FailFast;
            configure?.Invoke(options);
            return ConnectionMultiplexer.Connect(options);
        });
    }
}
