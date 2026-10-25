using System.Reflection;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace Cachew.Tests;

/// <summary>
/// Contention beyond the ten callers the other tests use. The single flight bookkeeping is the one place
/// a leak or a deadlock could hide, so these drive many keys and mixed operations at once and then check
/// that nothing was left behind.
/// </summary>
public class TwoLayerCacheConcurrencyTests
{
    private static readonly CachewOptions DefaultOptions = new();

    [Fact]
    public async Task ConcurrentMissesAcrossManyKeys_RunEachFactoryOnce()
    {
        var cache = CreateCache(out _);
        var keys = Enumerable.Range(0, 50).Select(index => $"key{index}").ToArray();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;

        async Task<string> Factory(string key, CancellationToken _)
        {
            Interlocked.Increment(ref calls);
            await gate.Task;
            return key;
        }

        var callers = new List<(string Key, Task<string> Task)>();
        foreach (var key in keys)
        {
            for (var i = 0; i < 10; i++)
            {
                var captured = key;
                callers.Add((captured, Task.Run(() => cache.GetOrCreateAsync(captured, ct => Factory(captured, ct)))));
            }
        }

        // Let every key reach its factory before any of them returns, so the callers genuinely overlap.
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (Volatile.Read(ref calls) < keys.Length && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        gate.SetResult();
        await Task.WhenAll(callers.Select(caller => caller.Task));

        Assert.Equal(keys.Length, calls);
        foreach (var (key, task) in callers)
        {
            Assert.Equal(key, await task);
        }

        Assert.Equal(0, InFlight(cache));
    }

    [Fact]
    public async Task MixedOperationsUnderContention_LeaveNothingBehind()
    {
        var cache = CreateCache(out _);
        var keys = Enumerable.Range(0, 20).Select(index => $"mixed{index}").ToArray();

        var operations = new List<Task>();
        foreach (var key in keys)
        {
            var captured = key;
            operations.Add(Task.Run(() => cache.SetAsync(captured, captured)));
            operations.Add(Task.Run(() => cache.GetAsync<string>(captured)));
            operations.Add(Task.Run(() => cache.GetOrCreateAsync(captured, _ => Task.FromResult(captured))));
            operations.Add(Task.Run(() => cache.RemoveAsync(captured)));
        }

        await Task.WhenAll(operations);

        Assert.Equal(0, InFlight(cache));
        Assert.Equal("after", await cache.GetOrCreateAsync("after", _ => Task.FromResult("after")));
        Assert.Equal(0, InFlight(cache));
    }

    [Fact]
    public async Task ConcurrentMisses_WithAFailingDistributedLayer_StillProduceValues()
    {
        var cache = new TwoLayerCache(
            new MemoryCache(new MemoryCacheOptions()),
            new FailingDistributedCache(new CacheUnavailableException()),
            Options.Create(DefaultOptions));
        var calls = 0;

        var callers = Enumerable.Range(0, 100)
            .Select(_ => Task.Run(() => cache.GetOrCreateAsync("key", __ => Task.FromResult(Interlocked.Increment(ref calls)))))
            .ToArray();

        var results = await Task.WhenAll(callers);

        // Every caller gets the one value the factory produced, even though both the read from the
        // distributed layer and the write to it fail throughout.
        Assert.All(results, value => Assert.Equal(1, value));
        Assert.Equal(1, calls);
        Assert.Equal(0, InFlight(cache));
    }

    private static TwoLayerCache CreateCache(out IDistributedCache distributed)
    {
        distributed = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
        return new TwoLayerCache(new MemoryCache(new MemoryCacheOptions()), distributed, Options.Create(DefaultOptions));
    }

    /// <summary>
    /// Reaches into the single flight map, because a leaked entry here is a memory leak that no
    /// behavioural assertion would notice. Reflection keeps it out of the production surface.
    /// </summary>
    private static int InFlight(TwoLayerCache cache)
        => ((System.Collections.ICollection)typeof(TwoLayerCache)
            .GetField("_inFlight", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(cache)!).Count;
}
