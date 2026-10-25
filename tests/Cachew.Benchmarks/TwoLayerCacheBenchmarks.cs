using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;

namespace Cachew.Benchmarks;

/// <summary>
/// Benchmarks the read and write paths of <see cref="ICachew"/>, plus a raw
/// <see cref="IMemoryCache"/> lookup as the floor the library sits on top of.
/// </summary>
[MemoryDiagnoser]
[HideColumns("Error", "StdDev", "Median", "Gen1", "Gen2")]
public class TwoLayerCacheBenchmarks
{
    private const string Key = "product:42";
    private const string CacheKey = "bench:" + Key;

    private static readonly Product Value = new(42, "Widget", 19.99m);

    private ICachew _default = null!;
    private ICachew _memoryOnly = null!;
    private ICachew _distributedOnly = null!;
    private IMemoryCache _rawMemory = null!;

    [GlobalSetup]
    public void Setup()
    {
        _default = BuildCache(_ => { });
        _memoryOnly = BuildCache(o => o.DefaultDistributedTimeToLive = TimeSpan.Zero);
        _distributedOnly = BuildCache(o => o.DefaultInMemoryTimeToLive = TimeSpan.Zero);

        _default.SetAsync(Key, Value).GetAwaiter().GetResult();
        _memoryOnly.SetAsync(Key, Value).GetAwaiter().GetResult();
        _distributedOnly.SetAsync(Key, Value).GetAwaiter().GetResult();

        _rawMemory = new MemoryCache(new MemoryCacheOptions());
        _rawMemory.Set(CacheKey, Value);
    }

    [GlobalCleanup]
    public void Cleanup() => _rawMemory.Dispose();

    private static ICachew BuildCache(Action<CachewOptions> configure)
    {
        var services = new ServiceCollection();
        services.AddDistributedMemoryCache();
        services.AddCachew(o =>
        {
            o.KeyPrefix = "bench:";
            configure(o);
        });

        return services.BuildServiceProvider().GetRequiredService<ICachew>();
    }

    [Benchmark(Baseline = true)]
    public bool RawMemoryCache_Get() => _rawMemory.TryGetValue(CacheKey, out Product? _);

    [Benchmark]
    public ValueTask<Product?> Get_MemoryHit() => _default.GetAsync<Product>(Key);

    [Benchmark]
    public ValueTask<Product?> Get_DistributedHit() => _distributedOnly.GetAsync<Product>(Key);

    [Benchmark]
    public ValueTask<Product?> GetOrCreate_MemoryHit() => _default.GetOrCreateAsync(Key, Factory);

    [Benchmark]
    public ValueTask<Product?> Get_Miss() => _distributedOnly.GetAsync<Product>("product:404");

    [Benchmark]
    public ValueTask Set() => _default.SetAsync(Key, Value);

    // Break the write path into its layers, so the library's own allocations are visible apart from
    // the ones the serialization and the distributed provider bring.
    [Benchmark]
    public ValueTask Set_MemoryOnly() => _memoryOnly.SetAsync(Key, Value);

    [Benchmark]
    public ValueTask Set_DistributedOnly() => _distributedOnly.SetAsync(Key, Value);

    private static Task<Product?> Factory(CancellationToken _) => Task.FromResult<Product?>(Value);

    public sealed record Product(int Id, string Name, decimal Price);
}
