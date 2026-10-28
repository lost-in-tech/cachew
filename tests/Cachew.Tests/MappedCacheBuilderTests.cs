using System.Text.Json;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace Cachew.Tests;

public class MappedCacheBuilderTests
{
    private static readonly CachewOptions DefaultOptions = new();

    [Fact]
    public async Task A_mapped_value_is_cached_and_returned()
    {
        var cache = CreateCache(out _);
        var calls = 0;

        var builder = cache.OnCacheMiss(
            _ => { calls++; return Task.FromResult(new Response<string>(true, "value")); },
            response => response.Value!);

        Assert.Equal("value", await builder.GetAsync("key"));
        Assert.Equal("value", await builder.GetAsync("key"));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task CacheIf_sees_the_factory_result_not_the_mapped_value()
    {
        var cache = CreateCache(out _);
        var calls = 0;

        // The mapped value looks cacheable on its own, but the response it came from does not, so the
        // predicate over the response is what decides.
        var builder = cache
            .OnCacheMiss(
                _ => { calls++; return Task.FromResult(new Response<string>(false, "partial")); },
                response => response.Value!)
            .CacheIf(response => response.Succeeded);

        Assert.Equal("partial", await builder.GetAsync("key"));
        Assert.Equal("partial", await builder.GetAsync("key"));
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task A_result_accepted_by_CacheIf_is_cached()
    {
        var cache = CreateCache(out _);
        var calls = 0;

        var builder = cache
            .OnCacheMiss(
                _ => { calls++; return Task.FromResult(new Response<string>(true, "value")); },
                response => response.Value!)
            .CacheIf(response => response.Succeeded);

        Assert.Equal("value", await builder.GetAsync("key"));
        Assert.Equal("value", await builder.GetAsync("key"));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task A_null_mapped_value_from_a_rejected_result_is_returned_and_not_cached()
    {
        var cache = CreateCache(out _);
        var calls = 0;

        var builder = cache
            .OnCacheMiss(
                _ => { calls++; return Task.FromResult(new Response<string>(false, null)); },
                response => response.Value)
            .CacheIf(response => response.Succeeded);

        Assert.Null(await builder.GetAsync("key"));
        Assert.Null(await builder.GetAsync("key"));
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task A_zero_expiry_keeps_the_mapped_value_out_of_the_distributed_layer()
    {
        var cache = CreateCache(DefaultOptions, out var distributed, out _);

        await cache
            .OnCacheMiss(_ => Task.FromResult(new Response<string>(true, "value")), response => response.Value!)
            .ExpireIn(30, 0)
            .GetAsync("key");

        Assert.Null(await distributed.GetAsync(DefaultOptions.KeyPrefix + "key"));
        Assert.Equal("value", await cache.GetAsync<string>("key"));
    }

    [Fact]
    public async Task The_cached_payload_is_the_mapped_value()
    {
        var cache = CreateCache(DefaultOptions, out var distributed, out _);

        await cache
            .OnCacheMiss(_ => Task.FromResult(new Response<string>(true, "value")), response => response.Value!)
            .ExpireIn(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60))
            .GetAsync("key");

        var payload = await distributed.GetAsync(DefaultOptions.KeyPrefix + "key");
        var envelope = JsonSerializer.Deserialize<Envelope<string>>(
            payload!, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.Equal("value", envelope!.Value);
    }

    [Fact]
    public async Task The_extension_method_works_without_the_builder()
    {
        var cache = CreateCache(out _);
        var calls = 0;

        async Task<Response<string>> Factory(CancellationToken _)
        {
            calls++;
            await Task.Yield();
            return new Response<string>(true, "value");
        }

        var first = await cache.GetOrCreateAsync("key", Factory, response => response.Value!, result => result.Succeeded);
        var second = await cache.GetOrCreateAsync("key", Factory, response => response.Value!, result => result.Succeeded);

        Assert.Equal("value", first);
        Assert.Equal("value", second);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Concurrent_misses_run_the_factory_once_and_share_the_mapped_value()
    {
        var cache = CreateCache(out _);
        var calls = 0;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var callers = Enumerable.Range(0, 10)
            .Select(_ => Task.Run(() => cache
                .GetOrCreateAsync(
                    "key",
                    async _ =>
                    {
                        Interlocked.Increment(ref calls);
                        await gate.Task;
                        return new Response<string>(true, "value");
                    },
                    response => response.Value!)
                .AsTask()))
            .ToArray();

        // Let every caller reach the single flight before any of them returns, so they genuinely overlap.
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (Volatile.Read(ref calls) < 1 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        gate.SetResult();
        var results = await Task.WhenAll(callers);

        Assert.Equal(1, calls);
        Assert.All(results, value => Assert.Equal("value", value));
    }

    private static ICachew CreateCache(out IDistributedCache distributed)
        => CreateCache(DefaultOptions, out distributed, out _);

    private static ICachew CreateCache(CachewOptions options, out IDistributedCache distributed, out IMemoryCache memory)
    {
        distributed = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
        memory = new MemoryCache(new MemoryCacheOptions());
        return new TwoLayerCache(memory, distributed, Options.Create(options));
    }

    private sealed record Response<T>(bool Succeeded, T? Value);

    private sealed record Envelope<T>(T Value, DateTimeOffset? ExpiresAtUtc);
}
