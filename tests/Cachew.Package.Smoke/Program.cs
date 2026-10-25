// Consumes the packed packages the way a consumer would, written against the README rather than the
// source. Requires a Redis; pass its endpoint as the first argument (default localhost:6379).

using System.Diagnostics;
using Cachew;
using Cachew.Redis;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

var redis = args.Length > 0 ? args[0] : "localhost:6379";

var failures = new List<string>();
void Check(bool ok, string what)
{
    Console.WriteLine((ok ? "PASS  " : "FAIL  ") + what);
    if (!ok)
    {
        failures.Add(what);
    }
}

// ---------------------------------------------------------------- the cache, over a distributed memory layer
{
    var services = new ServiceCollection();
    services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
    services.AddDistributedMemoryCache();
    services.AddCachew(o =>
    {
        o.KeyPrefix = "smoke:";
        o.DefaultInMemoryTimeToLive = TimeSpan.FromMinutes(1);
        o.DefaultDistributedTimeToLive = TimeSpan.FromMinutes(10);
    });

    await using var provider = services.BuildServiceProvider();
    var cache = provider.GetRequiredService<ICachew>();

    var calls = 0;
    async Task<int> Factory(CancellationToken ct)
    {
        Interlocked.Increment(ref calls);
        await Task.Delay(20, ct);
        return 42;
    }

    var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => cache.GetOrCreateAsync("answer", Factory)));
    Check(results.All(value => value == 42), "a miss returns the factory value to every caller");
    Check(calls == 1, $"concurrent misses run the factory once (ran {calls} time(s))");

    await cache.SetAsync("greeting", "hello");
    Check(await cache.GetAsync<string>("greeting") == "hello", "SetAsync then GetAsync round trips");
    await cache.RemoveAsync("greeting");
    Check(await cache.GetAsync<string>("greeting") is null, "RemoveAsync clears the entry");

    var produced = await cache.GetOrCreateAsync(
        "not-cachable",
        _ => Task.FromResult("value"),
        new CacheEntryOptions<string> { ShouldCache = _ => false });
    Check(produced == "value" && await cache.GetAsync<string>("not-cachable") is null,
        "ShouldCache returning false returns the value without caching it");

    var distributedOnly = await cache.GetOrCreateAsync(
        "distributed-only",
        _ => Task.FromResult("value"),
        new CacheEntryOptions<string> { InMemoryTimeToLive = TimeSpan.Zero });
    Check(distributedOnly == "value" && await cache.GetAsync<string>("distributed-only") == "value",
        "a zero in-memory lifetime still reads and writes the distributed layer");

    var fluent = await cache
        .OnCacheMiss(_ => Task.FromResult("built"))
        .CacheIf(value => value is not null)
        .Duration(inMemorySeconds: 30, distributedSeconds: 60)
        .GetAsync("fluent");
    Check(fluent == "built" && await cache.GetAsync<string>("fluent") == "built", "the fluent API works");

    Check(await cache.GetAsync<int>("answer") == 42, "a value is read back as its own type");
}

// ---------------------------------------------------------------- the incident switch
{
    var services = new ServiceCollection();
    services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
    services.AddDistributedMemoryCache();
    services.AddCachew(o => o.Enabled = false);

    await using var provider = services.BuildServiceProvider();
    var cache = provider.GetRequiredService<ICachew>();

    var calls = 0;
    await cache.GetOrCreateAsync("off", _ => Task.FromResult(Interlocked.Increment(ref calls)));
    await cache.GetOrCreateAsync("off", _ => Task.FromResult(Interlocked.Increment(ref calls)));
    await cache.SetAsync("off-set", "value");
    Check(calls == 2, "Enabled = false runs the factory on every call");
    Check(await cache.GetAsync<string>("off-set") is null, "Enabled = false writes nothing");
}

// ---------------------------------------------------------------- the Redis provider
{
    var services = new ServiceCollection();
    services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
    services.AddCachewRedis(redis, options => options.KeyPrefix = "smoke:");

    await using var provider = services.BuildServiceProvider();
    var cache = provider.GetRequiredService<ICachew>();
    var raw = provider.GetRequiredService<IDistributedCache>();

    await cache.SetAsync("redis-key", "redis-value");

    // Writes are fire and forget, so wait for it to land. Read the distributed cache directly, since
    // reading through the cache would be answered from memory and hide a failed write.
    var stopwatch = Stopwatch.StartNew();
    while (await raw.GetStringAsync("smoke:redis-key") is null && stopwatch.Elapsed < TimeSpan.FromSeconds(5))
    {
        await Task.Delay(50);
    }

    var payload = await raw.GetStringAsync("smoke:redis-key");
    Check(payload is not null, "a write reaches Redis, under the configured key prefix");
    Check(payload is not null && payload.Contains("redis-value"), "the payload is readable JSON");
    Check(await cache.GetAsync<string>("redis-key") == "redis-value", "a value round trips through Redis");

    await cache.RemoveAsync("redis-key");
    Check(await cache.GetAsync<string>("redis-key") is null, "RemoveAsync clears both layers");
}

// ---------------------------------------------------------------- the backplane wires up
{
    var services = new ServiceCollection();
    services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
    services.AddCachewRedis(redis);
    services.AddCachewBackplane();

    await using var provider = services.BuildServiceProvider();
    var cache = provider.GetRequiredService<ICachew>();

    await cache.SetAsync("backplane-key", "value");
    Check(await cache.GetAsync<string>("backplane-key") == "value", "the cache works with the backplane registered");
}

Console.WriteLine();
Console.WriteLine(failures.Count == 0 ? "ALL CHECKS PASSED" : $"{failures.Count} CHECK(S) FAILED");
return failures.Count == 0 ? 0 : 1;
