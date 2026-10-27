using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;
using Testcontainers.Redis;
using Xunit.Sdk;

namespace Cachew.Redis.Tests;

public sealed class RedisFixture : IAsyncLifetime
{
    private readonly RedisContainer _container = new RedisBuilder("redis:7.4").Build();

    /// <summary>Stands in for one application's connection.</summary>
    public IConnectionMultiplexer Connection { get; private set; } = null!;

    /// <summary>Stands in for a second application's connection, so cross instance tests do not share one.</summary>
    public IConnectionMultiplexer SecondConnection { get; private set; } = null!;

    public string ConnectionString { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        ConnectionString = _container.GetConnectionString();
        Connection = await ConnectionMultiplexer.ConnectAsync(ConnectionString);
        SecondConnection = await ConnectionMultiplexer.ConnectAsync(ConnectionString);
    }

    public async Task DisposeAsync()
    {
        await SecondConnection.DisposeAsync();
        await Connection.DisposeAsync();
        await _container.DisposeAsync();
    }
}

[CollectionDefinition(Name)]
public sealed class RedisCollection : ICollectionFixture<RedisFixture>
{
    public const string Name = "redis";
}

[Collection(RedisCollection.Name)]
public sealed class RedisDistributedCacheIntegrationTests
{
    private readonly IConnectionMultiplexer _connection;
    private readonly string _connectionString;
    private readonly IDatabase _database;
    private readonly IDistributedCache _cache;

    public RedisDistributedCacheIntegrationTests(RedisFixture fixture)
    {
        _connection = fixture.Connection;
        _connectionString = fixture.ConnectionString;
        _database = fixture.Connection.GetDatabase();
        _cache = new RedisDistributedCache(fixture.Connection);
    }

    [Fact]
    public async Task A_stored_value_round_trips_against_redis()
    {
        var payload = new byte[] { 1, 2, 3 };

        await _cache.SetAsync("cachew:round-trip", payload, Expiring());

        await TestWait.Eventually(() => Task.FromResult(_database.KeyExists("cachew:round-trip")));
        Assert.Equal(payload, await _cache.GetAsync("cachew:round-trip"));
    }

    [Fact]
    public async Task A_write_applies_its_expiry_in_redis()
    {
        await _cache.SetAsync("cachew:expiry", [9], Expiring());

        await TestWait.Eventually(() => Task.FromResult(_database.KeyTimeToLive("cachew:expiry") is not null));

        var timeToLive = _database.KeyTimeToLive("cachew:expiry");
        Assert.NotNull(timeToLive);
        Assert.True(timeToLive > TimeSpan.Zero);
    }

    [Fact]
    public async Task A_write_without_an_expiry_leaves_the_key_persistent()
    {
        await _cache.SetAsync("cachew:persistent", [5], new DistributedCacheEntryOptions());

        await TestWait.Eventually(() => Task.FromResult(_database.KeyExists("cachew:persistent")));

        // PTTL is -1 for a key with no expiry. StackExchange.Redis maps that to null, the same as a
        // missing key, so ask Redis directly.
        Assert.Equal(-1, (long)_database.Execute("PTTL", "cachew:persistent"));
    }

    [Fact]
    public async Task A_remove_deletes_the_key_in_redis()
    {
        await _cache.SetAsync("cachew:remove", [7], Expiring());
        await TestWait.Eventually(() => Task.FromResult(_database.KeyExists("cachew:remove")));

        await _cache.RemoveAsync("cachew:remove");

        await TestWait.Eventually(() => Task.FromResult(!_database.KeyExists("cachew:remove")));
        Assert.Null(await _cache.GetAsync("cachew:remove"));
    }

    [Fact]
    public async Task A_missing_key_reads_as_nothing_from_redis()
    {
        Assert.Null(await _cache.GetAsync("cachew:missing"));
    }

    [Fact]
    public async Task A_key_reads_as_nothing_after_its_entry_expires()
    {
        await _cache.SetAsync(
            "cachew:short",
            [1],
            new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromMilliseconds(200) });

        await TestWait.Eventually(() => Task.FromResult(!_database.KeyExists("cachew:short")));
        Assert.Null(await _cache.GetAsync("cachew:short"));
    }

    [Fact]
    public async Task The_cache_round_trips_through_real_redis()
    {
        var services = new ServiceCollection();
        services.AddSingleton(_connection);
        services.AddCachewRedis();
        services.AddCachew(options => options.DefaultInMemoryTimeToLive = TimeSpan.Zero);
        using var provider = services.BuildServiceProvider();
        var cache = provider.GetRequiredService<ICachew>();

        await cache.SetAsync("order:1", "value");

        await TestWait.Eventually(async () => (await cache.GetAsync<string>("order:1")) is not null);
        Assert.Equal("value", await cache.GetAsync<string>("order:1"));
    }

    [Fact]
    public async Task The_synchronous_members_round_trip_against_redis()
    {
        _cache.Set("cachew:sync", [1, 2, 3], Expiring());
        await TestWait.Eventually(() => Task.FromResult(_database.KeyExists("cachew:sync")));

        _cache.Refresh("cachew:sync");
        Assert.Equal(new byte[] { 1, 2, 3 }, _cache.Get("cachew:sync"));
        Assert.Null(_cache.Get("cachew:not-there"));

        _cache.Remove("cachew:sync");
        await TestWait.Eventually(() => Task.FromResult(!_database.KeyExists("cachew:sync")));
        Assert.Null(_cache.Get("cachew:sync"));
    }

    [Fact]
    public async Task Refresh_is_a_no_op()
    {
        await _cache.RefreshAsync("cachew:anything");
        await _cache.RefreshAsync("cachew:anything", CancellationToken.None);
    }

    [Fact]
    public async Task An_expiry_in_the_past_writes_nothing_synchronously()
    {
        _cache.Set("cachew:elapsed", [1], new DistributedCacheEntryOptions
        {
            AbsoluteExpiration = DateTimeOffset.UtcNow.AddMinutes(-1),
        });

        Assert.False(_database.KeyExists("cachew:elapsed"));
    }

    [Fact]
    public async Task A_connection_string_registration_connects_and_caches()
    {
        var services = new ServiceCollection();
        services.AddCachewRedis(_connectionString, configureConnection: options => options.AbortOnConnectFail = true);
        using var provider = services.BuildServiceProvider();
        var cache = provider.GetRequiredService<IDistributedCache>();

        await cache.SetAsync("cachew:di", [4], Expiring());

        await TestWait.Eventually(() => Task.FromResult(_database.KeyExists("cachew:di")));
        Assert.Equal(new byte[] { 4 }, await cache.GetAsync("cachew:di"));
    }

    [Fact]
    public async Task A_synchronous_write_without_an_expiry_leaves_the_key_persistent()
    {
        _cache.Set("cachew:sync-persistent", [5], new DistributedCacheEntryOptions());

        await TestWait.Eventually(() => Task.FromResult(_database.KeyExists("cachew:sync-persistent")));

        Assert.Equal(-1, (long)_database.Execute("PTTL", "cachew:sync-persistent"));
    }

    [Fact]
    public async Task A_connection_string_factory_registration_applies_both_callbacks()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:redis"] = _connectionString })
            .Build();
        services.AddSingleton<IConfiguration>(configuration);

        services.AddCachewRedis(
            config => config.GetConnectionString("redis"),
            options => options.KeyPrefix = "cachew:factory:",
            configureConnection: options => options.AllowAdmin = true);

        using var provider = services.BuildServiceProvider();
        var cache = provider.GetRequiredService<ICachew>();
        const string key = "factory";

        // CLIENT is admin only, and admin came from the connection callback, so this throws unless the
        // callback reached the parsed connection options.
        provider.GetRequiredService<IConnectionMultiplexer>().GetDatabase().Execute("CLIENT", "GETNAME");

        await cache.SetAsync(key, "value");

        Assert.Equal("value", await cache.GetAsync<string>(key));
        await TestWait.Eventually(() => Task.FromResult(_database.KeyExists("cachew:factory:" + key)));
    }

    [Fact]
    public async Task A_connection_string_registration_alone_connects_and_caches()
    {
        var services = new ServiceCollection();
        services.AddCachewRedis(_connectionString);
        using var provider = services.BuildServiceProvider();
        var cache = provider.GetRequiredService<IDistributedCache>();

        await cache.SetAsync("cachew:di-defaults", [6], Expiring());

        await TestWait.Eventually(() => Task.FromResult(_database.KeyExists("cachew:di-defaults")));
    }

    private static DistributedCacheEntryOptions Expiring()
        => new() { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(1) };

}
