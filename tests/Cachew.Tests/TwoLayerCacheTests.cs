using System.Text;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace Cachew.Tests;

public class TwoLayerCacheTests
{
    private static readonly CachewOptions DefaultOptions = new();

    [Fact]
    public async Task GetAsync_ReturnsDefault_WhenKeyIsMissing()
    {
        var cache = CreateCache(out _);

        var value = await cache.GetAsync<string>("missing");

        Assert.Null(value);
    }

    [Fact]
    public async Task SetAsync_ThenGetAsync_ReturnsValue()
    {
        var cache = CreateCache(out _);

        await cache.SetAsync("key", "value");

        Assert.Equal("value", await cache.GetAsync<string>("key"));
    }

    [Fact]
    public async Task GetAsync_ReadsFromDistributedLayer_WhenMemoryLayerIsEmpty()
    {
        var distributed = CreateDistributedCache();
        var writer = CreateCache(distributed);
        var reader = CreateCache(distributed);

        await writer.SetAsync("key", new Payload(42, "answer"));
        var value = await reader.GetAsync<Payload>("key");

        Assert.Equal(new Payload(42, "answer"), value);
    }

    [Fact]
    public async Task GetAsync_PopulatesMemoryLayer_FromDistributedLayer()
    {
        var distributed = CreateDistributedCache();
        var writer = CreateCache(distributed);
        var reader = CreateCache(distributed);

        await writer.SetAsync("key", "value");
        Assert.Equal("value", await reader.GetAsync<string>("key"));

        // Remove from the distributed layer only. The value must now be served from memory.
        await distributed.RemoveAsync(DefaultOptions.KeyPrefix + "key");
        Assert.Equal("value", await reader.GetAsync<string>("key"));
    }

    [Fact]
    public async Task GetOrCreateAsync_InvokesFactory_OnlyOnMiss()
    {
        var cache = CreateCache(out _);
        var calls = 0;

        var first = await cache.GetOrCreateAsync("key", _ => Task.FromResult(++calls));
        var second = await cache.GetOrCreateAsync("key", _ => Task.FromResult(++calls));

        Assert.Equal(1, first);
        Assert.Equal(1, second);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task RemoveAsync_ClearsBothLayers()
    {
        var distributed = CreateDistributedCache();
        var cache = CreateCache(distributed);

        await cache.SetAsync("key", "value");
        await cache.RemoveAsync("key");

        Assert.Null(await cache.GetAsync<string>("key"));
        Assert.Null(await distributed.GetAsync(DefaultOptions.KeyPrefix + "key"));
    }

    [Fact]
    public async Task KeysAreNamespacedByPrefix()
    {
        var distributed = CreateDistributedCache();
        var cache = CreateCache(distributed);

        await cache.SetAsync("key", "value");

        Assert.NotNull(await distributed.GetAsync("cachew:key"));
        Assert.Null(await distributed.GetAsync("key"));
    }

    [Fact]
    public async Task GetOrCreateAsync_CoalescesConcurrentMisses()
    {
        var cache = CreateCache(out _);
        var calls = 0;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<int> Factory(CancellationToken _)
        {
            Interlocked.Increment(ref calls);
            await gate.Task;
            return 42;
        }

        var callers = Enumerable.Range(0, 10)
            .Select(_ => cache.GetOrCreateAsync("key", Factory))
            .ToArray();

        // Let every caller reach the coalescing point before the factory completes.
        await Task.Delay(100);
        Assert.Equal(1, calls);

        gate.SetResult();
        var results = await Task.WhenAll(callers);

        Assert.All(results, value => Assert.Equal(42, value));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task GetOrCreateAsync_PropagatesFailure_AndAllowsRetry()
    {
        var cache = CreateCache(out _);
        var calls = 0;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<int> Factory(CancellationToken _)
        {
            Interlocked.Increment(ref calls);
            await gate.Task;
            throw new InvalidOperationException("boom");
        }

        var first = cache.GetOrCreateAsync("key", Factory);
        var second = cache.GetOrCreateAsync("key", Factory);

        await Task.Delay(100);
        gate.SetResult();

        await Assert.ThrowsAsync<InvalidOperationException>(() => first);
        await Assert.ThrowsAsync<InvalidOperationException>(() => second);
        Assert.Equal(1, calls);

        // A failed attempt is not cached, so a later call retries.
        Assert.Equal(7, await cache.GetOrCreateAsync("key", _ => Task.FromResult(7)));
    }

    [Fact]
    public async Task SetAsync_WithZeroInMemoryTimeToLive_DoesNotPopulateMemoryLayer()
    {
        var cache = CreateCache(DefaultOptions, out var distributed, out var memory);

        await cache.SetAsync("key", "value", new CacheEntryOptions<string> { InMemoryTimeToLive = TimeSpan.Zero });

        Assert.False(memory.TryGetValue(DefaultOptions.KeyPrefix + "key", out _));
        Assert.NotNull(await distributed.GetAsync(DefaultOptions.KeyPrefix + "key"));
    }

    [Fact]
    public async Task SetAsync_WithZeroDistributedTimeToLive_DoesNotPopulateDistributedLayer()
    {
        var cache = CreateCache(DefaultOptions, out var distributed, out var memory);

        await cache.SetAsync("key", "value", new CacheEntryOptions<string> { DistributedTimeToLive = TimeSpan.Zero });

        Assert.Null(await distributed.GetAsync(DefaultOptions.KeyPrefix + "key"));
        Assert.True(memory.TryGetValue(DefaultOptions.KeyPrefix + "key", out _));
    }

    [Fact]
    public async Task SetAsync_WithBothLayersDisabled_DoesNotCache()
    {
        var cache = CreateCache(DefaultOptions, out var distributed, out var memory);

        await cache.SetAsync("key", "value", new CacheEntryOptions<string>
        {
            InMemoryTimeToLive = TimeSpan.Zero,
            DistributedTimeToLive = TimeSpan.Zero,
        });

        Assert.Null(await distributed.GetAsync(DefaultOptions.KeyPrefix + "key"));
        Assert.False(memory.TryGetValue(DefaultOptions.KeyPrefix + "key", out _));
        Assert.Null(await cache.GetAsync<string>("key"));
    }

    [Fact]
    public async Task GetOrCreateAsync_WhenShouldCacheIsFalse_DoesNotCache()
    {
        var cache = CreateCache(out _);
        var calls = 0;
        var options = new CacheEntryOptions<int> { ShouldCache = value => value > 100 };

        var first = await cache.GetOrCreateAsync("key", _ => Task.FromResult(++calls), options);
        var second = await cache.GetOrCreateAsync("key", _ => Task.FromResult(++calls), options);

        Assert.Equal(1, first);
        Assert.Equal(2, second);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task GetOrCreateAsync_WhenShouldCacheIsTrue_Caches()
    {
        var cache = CreateCache(out _);
        var calls = 0;
        var options = new CacheEntryOptions<int> { ShouldCache = value => value > 0 };

        var first = await cache.GetOrCreateAsync("key", _ => Task.FromResult(++calls), options);
        var second = await cache.GetOrCreateAsync("key", _ => Task.FromResult(++calls), options);

        Assert.Equal(1, first);
        Assert.Equal(1, second);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task GetAsync_WithInMemoryDisabled_ReadsFromDistributedLayerWithoutPopulatingMemory()
    {
        var options = new CachewOptions { DefaultInMemoryTimeToLive = TimeSpan.Zero };
        var distributed = CreateDistributedCache();
        var writer = CreateCache(distributed);
        var memory = new MemoryCache(new MemoryCacheOptions());
        var reader = new TwoLayerCache(memory, distributed, Options.Create(options));

        await writer.SetAsync("key", "value");

        Assert.Equal("value", await reader.GetAsync<string>("key"));
        Assert.False(memory.TryGetValue(options.KeyPrefix + "key", out _));
    }

    [Fact]
    public async Task GetAsync_WithDistributedDisabled_DoesNotReadDistributedLayer()
    {
        var options = new CachewOptions { DefaultDistributedTimeToLive = TimeSpan.Zero };
        var distributed = CreateDistributedCache();
        var writer = CreateCache(distributed);
        var reader = new TwoLayerCache(new MemoryCache(new MemoryCacheOptions()), distributed, Options.Create(options));

        await writer.SetAsync("key", "value");

        Assert.Null(await reader.GetAsync<string>("key"));
    }

    [Fact]
    public async Task GetAsync_PromotedEntry_ExpiresWithInMemoryTimeToLive()
    {
        var options = new CachewOptions { DefaultInMemoryTimeToLive = TimeSpan.FromMilliseconds(100) };
        var distributed = CreateDistributedCache();
        var writer = CreateCache(distributed);
        var memory = new MemoryCache(new MemoryCacheOptions());
        var reader = new TwoLayerCache(memory, distributed, Options.Create(options));

        await writer.SetAsync("key", "value");

        // First read promotes the value into memory for the configured in-memory time to live.
        Assert.Equal("value", await reader.GetAsync<string>("key"));
        Assert.True(memory.TryGetValue(options.KeyPrefix + "key", out _));

        await Task.Delay(500);

        Assert.False(memory.TryGetValue(options.KeyPrefix + "key", out _));
        Assert.Equal("value", await reader.GetAsync<string>("key"));
    }

    [Fact]
    public async Task RemoveAsync_WithDistributedDisabled_DoesNotTouchDistributedLayer()
    {
        var options = new CachewOptions { DefaultDistributedTimeToLive = TimeSpan.Zero };
        var distributed = CreateDistributedCache();
        await distributed.SetAsync(
            options.KeyPrefix + "key",
            [1, 2, 3],
            new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5) });
        var cache = new TwoLayerCache(new MemoryCache(new MemoryCacheOptions()), distributed, Options.Create(options));

        await cache.RemoveAsync("key");

        Assert.NotNull(await distributed.GetAsync(options.KeyPrefix + "key"));
    }

    [Fact]
    public async Task GetOrCreateAsync_CancellingTheLeader_DoesNotAbandonTheSharedWork()
    {
        var cache = CreateCache(out _);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var leader = new CancellationTokenSource();

        async Task<int> Factory(CancellationToken ct)
        {
            await gate.Task.WaitAsync(ct);
            return 42;
        }

        var first = cache.GetOrCreateAsync("key", Factory, null, leader.Token);
        await Task.Delay(50);
        var second = cache.GetOrCreateAsync("key", Factory, null, CancellationToken.None);

        leader.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);

        gate.SetResult();

        Assert.Equal(42, await second);
        Assert.Equal(42, await cache.GetAsync<int>("key"));
    }

    [Fact]
    public async Task GetAsync_TreatsPayloadWithoutExpiryAsMiss()
    {
        var distributed = CreateDistributedCache();
        await distributed.SetAsync(
            DefaultOptions.KeyPrefix + "key",
            Encoding.UTF8.GetBytes("{\"value\":\"value\",\"expiresAtUtc\":null}"),
            new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5) });
        var cache = CreateCache(distributed);

        Assert.Null(await cache.GetAsync<string>("key"));
    }

    [Fact]
    public async Task GetAsync_WhenDistributedLayerFails_ReturnsMiss()
    {
        var cache = CreateCache(new FailingDistributedCache(new CacheUnavailableException()));

        Assert.Null(await cache.GetAsync<string>("key"));
    }

    [Fact]
    public async Task GetOrCreateAsync_WhenDistributedLayerFails_UsesMemoryLayer()
    {
        var cache = CreateCache(new FailingDistributedCache(new CacheUnavailableException()));
        var calls = 0;

        var first = await cache.GetOrCreateAsync("key", _ => Task.FromResult(++calls));
        var second = await cache.GetOrCreateAsync("key", _ => Task.FromResult(++calls));

        Assert.Equal(1, first);
        Assert.Equal(1, second);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task SetAsync_WhenDistributedLayerFails_StillCachesInMemory()
    {
        var cache = CreateCache(new FailingDistributedCache(new CacheUnavailableException()));

        await cache.SetAsync("key", "value");

        Assert.Equal("value", await cache.GetAsync<string>("key"));
    }

    [Fact]
    public async Task RemoveAsync_WhenDistributedLayerFails_StillClearsMemoryLayer()
    {
        var cache = CreateCache(new FailingDistributedCache(new CacheUnavailableException()));

        await cache.SetAsync("key", "value");
        Assert.Equal("value", await cache.GetAsync<string>("key"));

        await cache.RemoveAsync("key");

        Assert.Null(await cache.GetAsync<string>("key"));
    }

    [Fact]
    public async Task GetAsync_WhenDistributedLayerIsCancelled_PropagatesCancellation()
    {
        var cache = CreateCache(new FailingDistributedCache(new OperationCanceledException()));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cache.GetAsync<string>("key"));
    }

    [Fact]
    public async Task Cache_WithSizeLimitedMemoryCache_Works()
    {
        var memory = new MemoryCache(new MemoryCacheOptions { SizeLimit = 10 });
        var cache = new TwoLayerCache(memory, CreateDistributedCache(), Options.Create(DefaultOptions));

        await cache.SetAsync("key", "value");

        Assert.Equal("value", await cache.GetAsync<string>("key"));
    }

    private static ICachew CreateCache(IDistributedCache distributed)
        => new TwoLayerCache(new MemoryCache(new MemoryCacheOptions()), distributed, Options.Create(DefaultOptions));

    private static ICachew CreateCache(CachewOptions options, out IDistributedCache distributed, out IMemoryCache memory)
    {
        distributed = CreateDistributedCache();
        memory = new MemoryCache(new MemoryCacheOptions());
        return new TwoLayerCache(memory, distributed, Options.Create(options));
    }

    private static ICachew CreateCache(out IDistributedCache distributed)
    {
        distributed = CreateDistributedCache();
        return CreateCache(distributed);
    }

    private static MemoryDistributedCache CreateDistributedCache()
        => new(Options.Create(new MemoryDistributedCacheOptions()));

    public sealed record Payload(int Id, string Name);
}
