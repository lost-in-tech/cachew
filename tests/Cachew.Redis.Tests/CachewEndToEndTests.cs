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
    private readonly string _connectionString;

    public CachewEndToEndTests(RedisFixture fixture)
    {
        _connection = fixture.Connection;
        _secondConnection = fixture.SecondConnection;
        _database = fixture.Connection.GetDatabase();
        _connectionString = fixture.ConnectionString;
    }

    [Fact]
    public async Task A_stored_value_round_trips_and_reaches_both_layers()
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
    public async Task One_registration_call_is_enough_for_both_layers()
    {
        var services = new ServiceCollection();
        services.AddSingleton(_connection);

        // Deliberately no AddCachew: registering the Redis layer registers the cache over it.
        services.AddCachewRedis();

        using var provider = services.BuildServiceProvider();
        var cache = provider.GetRequiredService<ICachew>();
        var memory = provider.GetRequiredService<IMemoryCache>();
        const string key = "e2e-one-call";

        await cache.SetAsync(key, "value");

        Assert.Equal("value", await cache.GetAsync<string>(key));
        Assert.True(memory.TryGetValue(Prefixed(key), out _));
        await TestWait.Eventually(() => Task.FromResult(_database.KeyExists(Prefixed(key))));
    }

    [Fact]
    public async Task Settings_passed_to_the_registration_take_effect_end_to_end()
    {
        // The connection string overload, as a consumer would write it. It opens its own connection,
        // which the container owns, so this does not disturb the connection the fixture shares.
        var services = new ServiceCollection();
        services.AddCachewRedis(_connectionString, options =>
        {
            options.KeyPrefix = "e2e-settings:";
            options.DefaultInMemoryTimeToLive = TimeSpan.FromSeconds(30);
        });

        using var provider = services.BuildServiceProvider();
        var cache = provider.GetRequiredService<ICachew>();
        const string key = "settings";

        await cache.SetAsync(key, "value");

        Assert.Equal("value", await cache.GetAsync<string>(key));
        await TestWait.Eventually(() => Task.FromResult(_database.KeyExists("e2e-settings:" + key)));
    }

    [Fact]
    public async Task A_read_comes_from_memory_even_when_redis_changes_underneath()
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
    public async Task A_disabled_memory_layer_sends_every_read_to_redis()
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
    public async Task A_disabled_distributed_layer_keeps_writes_in_memory()
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
    public async Task The_factory_result_is_cached()
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
    public async Task Concurrent_misses_for_one_key_run_the_factory_once()
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

        var callers = Enumerable.Range(0, 10).Select(_ => cache.GetOrCreateAsync(key, Factory).AsTask()).ToArray();
        await Task.Delay(100);
        Assert.Equal(1, calls);

        gate.SetResult();

        Assert.All(await Task.WhenAll(callers), value => Assert.Equal(42, value));
    }

    [Fact]
    public async Task A_cached_null_is_a_hit_on_another_instance()
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
    public async Task A_value_rejected_by_ShouldCache_is_not_stored()
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
    public async Task A_per_entry_expiry_is_applied_to_the_stored_entry()
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
    public async Task The_fluent_builder_round_trips()
    {
        using var provider = CreateProvider(_connection);
        var cache = provider.GetRequiredService<ICachew>();
        const string key = "e2e-fluent";
        var calls = 0;

        var builder = cache
            .OnCacheMiss(_ => Task.FromResult(++calls))
            .CacheIf(value => value > 0)
            .ExpireIn(inMemorySeconds: 30, distributedSeconds: 60);

        Assert.Equal(1, await builder.GetAsync(key));
        Assert.Equal(1, await builder.GetAsync(key));
        Assert.Equal(1, calls);
        await TestWait.Eventually(() => Task.FromResult(_database.KeyExists(Prefixed(key))));
    }

    [Fact]
    public async Task Removing_a_key_purges_both_layers()
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
    public async Task An_expired_entry_in_redis_is_a_miss_that_reloads()
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
    public async Task An_unreadable_entry_in_redis_is_a_miss_that_is_replaced()
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
    public async Task The_stored_payload_is_camel_case_without_nulls_and_with_named_enums()
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
    public async Task A_key_prefix_namespaces_stored_keys()
    {
        using var provider = CreateProvider(_connection, options => options.KeyPrefix = "e2e-prefix:");
        var cache = provider.GetRequiredService<ICachew>();

        await cache.SetAsync("key", "value");

        await TestWait.Eventually(() => Task.FromResult(_database.KeyExists("e2e-prefix:key")));
        Assert.False(_database.KeyExists("key"));
    }

    [Fact]
    public async Task A_disabled_cache_stores_nothing_and_runs_the_factory_every_time()
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
    public async Task A_cache_disabled_from_configuration_is_off_end_to_end()
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
    public async Task A_write_is_stored_when_it_returns_with_fire_and_forget_off()
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

    [Fact]
    public async Task A_size_limit_configured_after_registration_is_honoured()
    {
        var services = new ServiceCollection();
        services.AddSingleton(_connection);
        services.AddCachewRedis();

        // The memory layer is registered first, and bounded afterwards, which has to work because the
        // options are read when the cache is resolved rather than when it is registered.
        services.AddCachew();
        services.Configure<MemoryCacheOptions>(options => options.SizeLimit = 2);

        using var provider = services.BuildServiceProvider();
        var cache = provider.GetRequiredService<ICachew>();
        var memory = provider.GetRequiredService<IMemoryCache>();

        for (var i = 0; i < 5; i++)
        {
            await cache.SetAsync($"limit-{i}", i);
        }

        var inMemory = Enumerable.Range(0, 5).Count(i => memory.TryGetValue("cachew:limit-" + i, out _));

        Assert.True(inMemory <= 2, $"expected at most 2 of 5 entries in memory, found {inMemory}");
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
