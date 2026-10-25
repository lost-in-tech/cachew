using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Cachew;

/// <summary>Registration helpers for <see cref="ICachew"/>.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the two layer cache. The memory layer is registered for you; register an
    /// <see cref="IDistributedCache"/> (for example Redis) before or after this call.
    /// </summary>
    /// <remarks>
    /// A logger is used if logging is registered in the container, and is optional otherwise, which
    /// the container satisfies from the constructor's default value.
    /// </remarks>
    public static IServiceCollection AddCachew(this IServiceCollection services, Action<CachewOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddMemoryCache();

        // Applied after any configuration of MemoryCacheOptions, and only when that has not set a
        // limit, so a limit the consumer configures themselves always wins whatever the order.
        services.AddOptions<MemoryCacheOptions>()
            .PostConfigure<IOptions<CachewOptions>>((memoryCache, cachew) =>
                memoryCache.SizeLimit ??= cachew.Value.MemoryCacheSizeLimit);

        services.AddOptions<CachewOptions>();
        if (configure is not null)
        {
            services.Configure(configure);
        }

        services.TryAddSingleton<ICachew, TwoLayerCache>();
        return services;
    }
}
