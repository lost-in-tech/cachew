using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Cachew.Redis.Tests;

/// <summary>
/// Walks the feature list through the public API: dependency injection, a real Redis, a real memory
/// layer and the real serializer. The unit tests cover the same code with fakes, so these exist to
/// assert what a deployment observes, which means looking at Redis itself for keys, expiries and
/// payloads.
/// </summary>
[Collection(RedisCollection.Name)]
public sealed class CachewEndToEndTests
{
    private readonly IConnectionMultiplexer _connection;
    private readonly IConnectionMultiplexer _secondConnection;
    private readonly IDatabase _database;

    public CachewEndToEndTests(RedisFixture fixture)
    {
        _connection = fixture.Connection;
        _secondConnection = fixture.SecondConnection;
        _database = fixture.Connection.GetDatabase();
    }

    [Fact]
    public async Task SetThenGet_RoundTrips_AndReachesBothLayers()
    {
        using var provider = CreateProvider(_connection);
        var cache = provider.GetRequiredService<ICachew>();
        var memory = provider.GetRequiredService<IMemoryCache>();
        const string key = "e2e-round-trip";

        await cache.SetAsync(key, "value");

        Assert.Equal("value", await cache.GetAsync<string>(key));
        Assert.True(memory.TryGetValue(Prefixed(key), out _));
        await TestWait.Eventually(() => Task.FromResult(_database.KeyExists(Prefixed(key))));
    }

    [Fact]
    public async Task ReadsComeFromMemory_EvenWhenRedisChangesUnderneath()
    {
        using var provider = CreateProvider(_connection);
        var cache = provider.GetRequiredService<ICachew>();
        const string key = "e2e-memory-first";

        await cache.SetAsync(key, "v1");
        await TestWait.Eventually(() => Task.FromResult(_database.KeyExists(Prefixed(key))));

        // Someone else changes the stored value behind this instance's back.
        await _database.StringSetAsync(Prefixed(key), Payload("v2"), TimeSpan.FromMinutes(5));

        Assert.Equal("v1", await cache.GetAsync<string>(key));
    }

    [Fact]
    public async Task WithTheMemoryLayerDisabled_EveryReadGoesToRedis()
    {
        using var provider = CreateProvider(_connection, options => options.DefaultInMemoryTimeToLive = TimeSpan.Zero);
        var cache = provider.GetRequiredService<ICachew>();
        var memory = provider.GetRequiredService<IMemoryCache>();
        const string key = "e2e-no-memory";

        await cache.SetAsync(key, "v1");
        await TestWait.Eventually(() => Task.FromResult(_database.KeyExists(Prefixed(key))));
        Assert.False(memory.TryGetValue(Prefixed(key), out _));

        await _database.StringSetAsync(Prefixed(key), Payload("v2"), TimeSpan.FromMinutes(5));

        Assert.Equal("v2", await cache.GetAsync<string>(key));
    }

    [Fact]
    public async Task WithTheDistributedLayerDisabled_WritesStayInMemory()
    {
        using var provider = CreateProvider(_connection, options => options.DefaultDistributedTimeToLive = TimeSpan.Zero);
        var cache = provider.GetRequiredService<ICachew>();
        var memory = provider.GetRequiredService<IMemoryCache>();
        const string key = "e2e-no-redis";

        await cache.SetAsync(key, "value");

        Assert.Equal("value", await cache.GetAsync<string>(key));
        Assert.True(memory.TryGetValue(Prefixed(key), out _));
        await Task.Delay(200);
        Assert.False(_database.KeyExists(Prefixed(key)));
    }

    [Fact]
    public async Task GetOrCreateAsync_CachesWhatTheFactoryProduced()
    {
        using var provider = CreateProvider(_connection);
        var cache = provider.GetRequiredService<ICachew>();
        const string key = "e2e-get-or-create";
        var calls = 0;

        var first = await cache.GetOrCreateAsync(key, _ => Task.FromResult(++calls));
        var second = await cache.GetOrCreateAsync(key, _ => Task.FromResult(++calls));

        Assert.Equal(1, first);
        Assert.Equal(1, second);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task GetOrCreateAsync_CoalescesConcurrentMisses()
    {
        using var provider = CreateProvider(_connection);
        var cache = provider.GetRequiredService<ICachew>();
        const string key = "e2e-coalesce";
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;

        async Task<int> Factory(CancellationToken _)
        {
            Interlocked.Increment(ref calls);
            await gate.Task;
            return 42;
        }

        var callers = Enumerable.Range(0, 10).Select(_ => cache.GetOrCreateAsync(key, Factory)).ToArray();
        await Task.Delay(100);
        Assert.Equal(1, calls);

        gate.SetResult();

        Assert.All(await Task.WhenAll(callers), value => Assert.Equal(42, value));
    }

    [Fact]
    public async Task ACachedNull_IsAHitOnAnotherInstance()
    {
        using var providerA = CreateProvider(_connection);
        using var providerB = CreateProvider(_secondConnection);
        var cacheA = providerA.GetRequiredService<ICachew>();
        var cacheB = providerB.GetRequiredService<ICachew>();
        const string key = "e2e-null";
        var calls = 0;

        await cacheA.SetAsync<string?>(key, null);

        // Writes are fire-and-forget and B has its own connection, so wait for the write to land
        // before reading through the other instance.
        await TestWait.Eventually(() => Task.FromResult(_database.KeyExists(Prefixed(key))));

        var value = await cacheB.GetOrCreateAsync<string?>(key, _ =>
        {
            calls++;
            return Task.FromResult<string?>("produced");
        });

        Assert.Null(value);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task ShouldCacheFalse_StoresNothing()
    {
        using var provider = CreateProvider(_connection);
        var cache = provider.GetRequiredService<ICachew>();
        const string key = "e2e-should-cache";
        var calls = 0;
        var options = new CacheEntryOptions<int> { ShouldCache = _ => false };

        await cache.GetOrCreateAsync(key, _ => Task.FromResult(++calls), options);
        await cache.GetOrCreateAsync(key, _ => Task.FromResult(++calls), options);

        Assert.Equal(2, calls);
        await Task.Delay(200);
        Assert.False(_database.KeyExists(Prefixed(key)));
    }

    [Fact]
    public async Task APerEntryExpiry_IsAppliedToTheStoredEntry()
    {
        using var provider = CreateProvider(_connection);
        var cache = provider.GetRequiredService<ICachew>();
        const string key = "e2e-expiry";

        await cache.SetAsync(key, "value", new CacheEntryOptions<string>
        {
            DistributedTimeToLive = TimeSpan.FromMinutes(2),
        });

        await TestWait.Eventually(() => Task.FromResult(_database.KeyTimeToLive(Prefixed(key)) is not null));

        var timeToLive = _database.KeyTimeToLive(Prefixed(key));
        Assert.NotNull(timeToLive);
        Assert.InRange(timeToLive.Value, TimeSpan.FromSeconds(110), TimeSpan.FromSeconds(125));
    }

    [Fact]
    public async Task TheFluentBuilder_RoundTrips()
    {
        using var provider = CreateProvider(_connection);
        var cache = provider.GetRequiredService<ICachew>();
        const string key = "e2e-fluent";
        var calls = 0;

        var builder = cache
            .OnCacheMiss(_ => Task.FromResult(++calls))
            .CacheIf(value => value > 0)
            .Duration(inMemorySeconds: 30, distributedSeconds: 60);

        Assert.Equal(1, await builder.GetAsync(key));
        Assert.Equal(1, await builder.GetAsync(key));
        Assert.Equal(1, calls);
        await TestWait.Eventually(() => Task.FromResult(_database.KeyExists(Prefixed(key))));
    }

    [Fact]
    public async Task Remove_PurgesBothLayers()
    {
        using var provider = CreateProvider(_connection);
        var cache = provider.GetRequiredService<ICachew>();
        var memory = provider.GetRequiredService<IMemoryCache>();
        const string key = "e2e-remove";

        await cache.SetAsync(key, "value");
        await TestWait.Eventually(() => Task.FromResult(_database.KeyExists(Prefixed(key))));

        await cache.RemoveAsync(key);

        Assert.Null(await cache.GetAsync<string>(key));
        Assert.False(memory.TryGetValue(Prefixed(key), out _));
        await TestWait.Eventually(() => Task.FromResult(!_database.KeyExists(Prefixed(key))));
    }

    [Fact]
    public async Task AnExpiredEntryInRedis_IsAMiss_AndIsReloaded()
    {
        using var provider = CreateProvider(_connection);
        var cache = provider.GetRequiredService<ICachew>();
        const string key = "e2e-expired";
        var calls = 0;

        await _database.StringSetAsync(
            Prefixed(key),
            $"{{\"value\":\"stale\",\"expiresAtUtc\":\"{DateTimeOffset.UtcNow.AddMinutes(-1):O}\"}}",
            TimeSpan.FromMinutes(5));

        Assert.Null(await cache.GetAsync<string>(key));

        var value = await cache.GetOrCreateAsync(key, _ =>
        {
            calls++;
            return Task.FromResult("fresh");
        });

        Assert.Equal("fresh", value);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task AnUnreadableEntryInRedis_IsAMiss_AndIsReplaced()
    {
        using var provider = CreateProvider(_connection);
        var cache = provider.GetRequiredService<ICachew>();
        const string key = "e2e-unreadable";

        await _database.StringSetAsync(Prefixed(key), "not json", TimeSpan.FromMinutes(5));

        Assert.Null(await cache.GetAsync<string>(key));
        Assert.Equal("fresh", await cache.GetOrCreateAsync(key, _ => Task.FromResult("fresh")));

        await TestWait.Eventually(() => Task.FromResult(_database.StringGet(Prefixed(key)).ToString().Contains("fresh")));
    }

    [Fact]
    public async Task TheStoredPayload_IsCamelCase_WithoutNulls_AndWithStringEnums()
    {
        using var providerA = CreateProvider(_connection);
        using var providerB = CreateProvider(_secondConnection);
        var cacheA = providerA.GetRequiredService<ICachew>();
        var cacheB = providerB.GetRequiredService<ICachew>();
        const string key = "e2e-payload";
        var payload = new StoredPayload(1, null, StoredStatus.Inactive);

        await cacheA.SetAsync(key, payload);

        await TestWait.Eventually(() => Task.FromResult(_database.KeyExists(Prefixed(key))));
        var json = _database.StringGet(Prefixed(key)).ToString();
        Assert.Contains("\"id\":1", json);
        Assert.DoesNotContain("\"name\"", json);
        Assert.Contains("\"status\":\"Inactive\"", json);

        Assert.Equal(payload, await cacheB.GetAsync<StoredPayload>(key));
    }

    [Fact]
    public async Task AKeyPrefix_NamespacesStoredKeys()
    {
        using var provider = CreateProvider(_connection, options => options.KeyPrefix = "e2e-prefix:");
        var cache = provider.GetRequiredService<ICachew>();

        await cache.SetAsync("key", "value");

        await TestWait.Eventually(() => Task.FromResult(_database.KeyExists("e2e-prefix:key")));
        Assert.False(_database.KeyExists("key"));
    }

    [Fact]
    public async Task Disabled_StoresNothing_AndCallsTheFactoryEveryTime()
    {
        using var provider = CreateProvider(_connection, options => options.Enabled = false);
        var cache = provider.GetRequiredService<ICachew>();
        const string key = "e2e-disabled";
        var calls = 0;

        await cache.GetOrCreateAsync(key, _ => Task.FromResult(++calls));
        await cache.GetOrCreateAsync(key, _ => Task.FromResult(++calls));

        Assert.Equal(2, calls);
        await Task.Delay(200);
        Assert.False(_database.KeyExists(Prefixed(key)));
    }

    [Fact]
    public async Task DisabledFromConfiguration_IsOffEndToEnd()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Cachew:Enabled"] = "false" })
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton(_connection);
        services.AddCachewRedis();
        services.Configure<CachewOptions>(configuration.GetSection("Cachew"));
        services.AddCachew();
        using var provider = services.BuildServiceProvider();
        var cache = provider.GetRequiredService<ICachew>();
        const string key = "e2e-config-disabled";
        var calls = 0;

        await cache.SetAsync(key, "value");

        Assert.Null(await cache.GetAsync<string>(key));

        // Caching is off, so each call runs the factory and returns its own result rather than the
        // first one.
        Assert.Equal(1, await cache.GetOrCreateAsync(key, _ => Task.FromResult(++calls)));
        Assert.Equal(2, await cache.GetOrCreateAsync(key, _ => Task.FromResult(++calls)));
        Assert.Equal(2, calls);
        await Task.Delay(200);
        Assert.False(_database.KeyExists(Prefixed(key)));
    }

    [Fact]
    public async Task WithFireAndForgetOff_TheWriteIsStoredWhenSetReturns()
    {
        var services = new ServiceCollection();
        services.AddSingleton(_connection);
        services.AddCachewRedis();
        services.Configure<RedisCachewOptions>(options => options.FireAndForget = false);
        services.AddCachew();
        using var provider = services.BuildServiceProvider();
        var cache = provider.GetRequiredService<ICachew>();
        const string key = "e2e-awaited";

        await cache.SetAsync(key, "value");

        // No polling: the write waited for Redis, so it is already stored. With the default
        // fire-and-forget this assertion would be racy, which is why the other tests poll.
        Assert.True(_database.KeyExists(Prefixed(key)));
    }

    private static string Prefixed(string key) => "cachew:" + key;

    private static string Payload(string value) =>
        $"{{\"value\":{JsonSerializer.Serialize(value)},\"expiresAtUtc\":\"{DateTimeOffset.UtcNow.AddMinutes(5):O}\"}}";

    private static ServiceProvider CreateProvider(IConnectionMultiplexer connection, Action<CachewOptions>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton(connection);
        services.AddCachewRedis();
        services.AddCachew(configure);
        return services.BuildServiceProvider();
    }

    private sealed record StoredPayload(int Id, string? Name, StoredStatus Status);

    private enum StoredStatus
    {
        Active,
        Inactive,
    }
}
