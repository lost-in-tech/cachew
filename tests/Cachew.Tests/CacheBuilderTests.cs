using System.Text.Json;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace Cachew.Tests;

public class CacheBuilderTests
{
    private static readonly CachewOptions DefaultOptions = new();

    [Fact]
    public async Task GetAsync_CachesValueAndReturnsIt()
    {
        var cache = CreateCache(out _);
        var calls = 0;

        var builder = cache.OnCacheMiss(_ => Task.FromResult(++calls));

        Assert.Equal(1, await builder.GetAsync("key"));
        Assert.Equal(1, await builder.GetAsync("key"));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task CacheIf_WhenFalse_DoesNotCache()
    {
        var cache = CreateCache(out _);
        var calls = 0;

        var builder = cache.OnCacheMiss(_ => Task.FromResult(++calls)).CacheIf(value => value > 100);

        Assert.Equal(1, await builder.GetAsync("key"));
        Assert.Equal(2, await builder.GetAsync("key"));
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task CacheIf_WhenTrue_Caches()
    {
        var cache = CreateCache(out _);
        var calls = 0;

        var builder = cache.OnCacheMiss(_ => Task.FromResult(++calls)).CacheIf(value => value > 0);

        Assert.Equal(1, await builder.GetAsync("key"));
        Assert.Equal(1, await builder.GetAsync("key"));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task ExpireIn_WithZeroDistributedSeconds_DoesNotUseDistributedLayer()
    {
        var cache = CreateCache(DefaultOptions, out var distributed, out _);

        await cache.OnCacheMiss(_ => Task.FromResult("value")).ExpireIn(30, 0).GetAsync("key");

        Assert.Null(await distributed.GetAsync(DefaultOptions.KeyPrefix + "key"));
        Assert.Equal("value", await cache.GetAsync<string>("key"));
    }

    [Fact]
    public async Task ExpireIn_WithZeroInMemorySeconds_DoesNotUseMemoryLayer()
    {
        var cache = CreateCache(DefaultOptions, out var distributed, out var memory);

        await cache.OnCacheMiss(_ => Task.FromResult("value")).ExpireIn(0, 60).GetAsync("key");

        Assert.False(memory.TryGetValue(DefaultOptions.KeyPrefix + "key", out _));
        Assert.NotNull(await distributed.GetAsync(DefaultOptions.KeyPrefix + "key"));
    }

    [Fact]
    public async Task ExpireIn_SetsDistributedExpiry()
    {
        var cache = CreateCache(DefaultOptions, out var distributed, out _);
        var before = DateTimeOffset.UtcNow;

        await cache
            .OnCacheMiss(_ => Task.FromResult("value"))
            .ExpireIn(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60))
            .GetAsync("key");

        var payload = await distributed.GetAsync(DefaultOptions.KeyPrefix + "key");
        var envelope = JsonSerializer.Deserialize<Envelope<string>>(
            payload!, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.NotNull(envelope);
        Assert.InRange(envelope!.ExpiresAtUtc!.Value, before.AddSeconds(59), DateTimeOffset.UtcNow.AddSeconds(61));
    }

    [Fact]
    public async Task Builder_CanBeReusedForDifferentKeys()
    {
        var cache = CreateCache(out _);
        var calls = 0;
        var builder = cache.OnCacheMiss(_ => Task.FromResult(++calls));

        Assert.Equal(1, await builder.GetAsync("a"));
        Assert.Equal(2, await builder.GetAsync("b"));
        Assert.Equal(1, await builder.GetAsync("a"));
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task ExpireIn_WithNulls_UsesTheConfiguredDefaults()
    {
        var cache = CreateCache(DefaultOptions, out var distributed, out _);

        await cache
            .OnCacheMiss(_ => Task.FromResult("value"))
            .ExpireIn(inMemorySeconds: null, distributedSeconds: null)
            .GetAsync("key");

        Assert.NotNull(await distributed.GetAsync(DefaultOptions.KeyPrefix + "key"));
    }

    private static ICachew CreateCache(out IDistributedCache distributed)
        => CreateCache(DefaultOptions, out distributed, out _);

    private static ICachew CreateCache(CachewOptions options, out IDistributedCache distributed, out IMemoryCache memory)
    {
        distributed = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
        memory = new MemoryCache(new MemoryCacheOptions());
        return new TwoLayerCache(memory, distributed, Options.Create(options));
    }

    private sealed record Envelope<T>(T Value, DateTimeOffset? ExpiresAtUtc);
}
