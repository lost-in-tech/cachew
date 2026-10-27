using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Cachew.Redis.Tests;

/// <summary>
/// The memory layer has no limit unless one is asked for, and a limit configured through
/// <see cref="MemoryCacheOptions"/> has to win over the library's option whatever order the registrations
/// happen in. These live in this project because it is the one with the dependency injection packages.
/// </summary>
public class CachewMemoryCacheLimitTests
{
    [Fact]
    public void There_is_no_memory_limit_by_default()
    {
        using var provider = Provider(services => services.AddCachew());

        Assert.Null(Limit(provider));
    }

    [Fact]
    public void A_limit_in_the_cache_options_is_used()
    {
        using var provider = Provider(services => services.AddCachew(options => options.MemoryCacheSizeLimit = 5));

        Assert.Equal(5L, Limit(provider));
    }

    [Fact]
    public void A_memory_cache_limit_configured_before_registration_is_used()
    {
        using var provider = Provider(services =>
        {
            services.Configure<MemoryCacheOptions>(options => options.SizeLimit = 2);
            services.AddCachew();
        });

        Assert.Equal(2L, Limit(provider));
    }

    [Fact]
    public void A_memory_cache_limit_configured_after_registration_is_used()
    {
        using var provider = Provider(services =>
        {
            services.AddCachew();
            services.Configure<MemoryCacheOptions>(options => options.SizeLimit = 2);
        });

        Assert.Equal(2L, Limit(provider));
    }

    private static long? Limit(IServiceProvider provider)
        => provider.GetRequiredService<IOptions<MemoryCacheOptions>>().Value.SizeLimit;

    private static ServiceProvider Provider(Action<IServiceCollection> configure)
    {
        var services = new ServiceCollection();
        configure(services);
        return services.BuildServiceProvider();
    }
}
