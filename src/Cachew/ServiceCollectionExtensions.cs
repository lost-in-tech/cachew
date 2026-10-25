using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Cachew;

/// <summary>Registration helpers for <see cref="ICachew"/>.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the two layer cache. The memory layer is registered for you; register an
    /// <see cref="IDistributedCache"/> (for example Redis) before or after this call.
    /// </summary>
    public static IServiceCollection AddCachew(this IServiceCollection services, Action<CachewOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddMemoryCache();
        services.AddOptions<CachewOptions>();
        if (configure is not null)
        {
            services.Configure(configure);
        }

        services.TryAddSingleton<ICachew, TwoLayerCache>();
        return services;
    }
}
