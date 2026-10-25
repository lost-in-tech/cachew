using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;
using Testcontainers.Redis;
using Xunit.Sdk;

namespace Cachew.Redis.Tests;

public sealed class RedisFixture : IAsyncLifetime
{
    private readonly RedisContainer _container = new RedisBuilder("redis:7.4").Build();

    public IConnectionMultiplexer Connection { get; private set; } = null!;

    public string ConnectionString { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        ConnectionString = _container.GetConnectionString();
        Connection = await ConnectionMultiplexer.ConnectAsync(ConnectionString);
    }

    public async Task DisposeAsync()
    {
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
    public async Task SetAsync_ThenGetAsync_RoundTrips()
    {
        var payload = new byte[] { 1, 2, 3 };

        await _cache.SetAsync("cachew:round-trip", payload, Expiring());

        await Eventually(() => Task.FromResult(_database.KeyExists("cachew:round-trip")));
        Assert.Equal(payload, await _cache.GetAsync("cachew:round-trip"));
    }

    [Fact]
    public async Task SetAsync_AppliesTheExpiry()
    {
        await _cache.SetAsync("cachew:expiry", [9], Expiring());

        await Eventually(() => Task.FromResult(_database.KeyTimeToLive("cachew:expiry") is not null));

        var timeToLive = _database.KeyTimeToLive("cachew:expiry");
        Assert.NotNull(timeToLive);
        Assert.True(timeToLive > TimeSpan.Zero);
    }

    [Fact]
    public async Task SetAsync_WithoutExpiry_LeavesTheKeyPersistent()
    {
        await _cache.SetAsync("cachew:persistent", [5], new DistributedCacheEntryOptions());

        await Eventually(() => Task.FromResult(_database.KeyExists("cachew:persistent")));

        // PTTL is -1 for a key with no expiry. StackExchange.Redis maps that to null, the same as a
        // missing key, so ask Redis directly.
        Assert.Equal(-1, (long)_database.Execute("PTTL", "cachew:persistent"));
    }

    [Fact]
    public async Task RemoveAsync_DeletesTheKey()
    {
        await _cache.SetAsync("cachew:remove", [7], Expiring());
        await Eventually(() => Task.FromResult(_database.KeyExists("cachew:remove")));

        await _cache.RemoveAsync("cachew:remove");

        await Eventually(() => Task.FromResult(!_database.KeyExists("cachew:remove")));
        Assert.Null(await _cache.GetAsync("cachew:remove"));
    }

    [Fact]
    public async Task GetAsync_ReturnsNull_WhenTheKeyIsMissing()
    {
        Assert.Null(await _cache.GetAsync("cachew:missing"));
    }

    [Fact]
    public async Task GetAsync_ReturnsNull_AfterTheEntryExpires()
    {
        await _cache.SetAsync(
            "cachew:short",
            [1],
            new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromMilliseconds(200) });

        await Eventually(() => Task.FromResult(!_database.KeyExists("cachew:short")));
        Assert.Null(await _cache.GetAsync("cachew:short"));
    }

    [Fact]
    public async Task Cache_RoundTripsThroughRealRedis()
    {
        var services = new ServiceCollection();
        services.AddSingleton(_connection);
        services.AddCachewRedis();
        services.AddCachew(options => options.DefaultInMemoryTimeToLive = TimeSpan.Zero);
        using var provider = services.BuildServiceProvider();
        var cache = provider.GetRequiredService<ICachew>();

        await cache.SetAsync("order:1", "value");

        await Eventually(async () => (await cache.GetAsync<string>("order:1")) is not null);
        Assert.Equal("value", await cache.GetAsync<string>("order:1"));
    }

    [Fact]
    public async Task SyncMembers_RoundTripAgainstRedis()
    {
        _cache.Set("cachew:sync", [1, 2, 3], Expiring());
        await Eventually(() => Task.FromResult(_database.KeyExists("cachew:sync")));

        _cache.Refresh("cachew:sync");
        Assert.Equal(new byte[] { 1, 2, 3 }, _cache.Get("cachew:sync"));
        Assert.Null(_cache.Get("cachew:not-there"));

        _cache.Remove("cachew:sync");
        await Eventually(() => Task.FromResult(!_database.KeyExists("cachew:sync")));
        Assert.Null(_cache.Get("cachew:sync"));
    }

    [Fact]
    public async Task RefreshAsync_IsANoOp()
    {
        await _cache.RefreshAsync("cachew:anything");
        await _cache.RefreshAsync("cachew:anything", CancellationToken.None);
    }

    [Fact]
    public async Task Set_WithAnElapsedExpiry_DoesNotWrite()
    {
        _cache.Set("cachew:elapsed", [1], new DistributedCacheEntryOptions
        {
            AbsoluteExpiration = DateTimeOffset.UtcNow.AddMinutes(-1),
        });

        Assert.False(_database.KeyExists("cachew:elapsed"));
    }

    [Fact]
    public async Task AddCachewRedis_WithConnectionString_ConnectsAndCaches()
    {
        var services = new ServiceCollection();
        services.AddCachewRedis(_connectionString, options => options.AbortOnConnectFail = true);
        using var provider = services.BuildServiceProvider();
        var cache = provider.GetRequiredService<IDistributedCache>();

        await cache.SetAsync("cachew:di", [4], Expiring());

        await Eventually(() => Task.FromResult(_database.KeyExists("cachew:di")));
        Assert.Equal(new byte[] { 4 }, await cache.GetAsync("cachew:di"));
    }

    [Fact]
    public async Task Set_WithoutExpiry_LeavesTheKeyPersistent()
    {
        _cache.Set("cachew:sync-persistent", [5], new DistributedCacheEntryOptions());

        await Eventually(() => Task.FromResult(_database.KeyExists("cachew:sync-persistent")));

        Assert.Equal(-1, (long)_database.Execute("PTTL", "cachew:sync-persistent"));
    }

    [Fact]
    public async Task AddCachewRedis_WithConnectionStringOnly_ConnectsAndCaches()
    {
        var services = new ServiceCollection();
        services.AddCachewRedis(_connectionString);
        using var provider = services.BuildServiceProvider();
        var cache = provider.GetRequiredService<IDistributedCache>();

        await cache.SetAsync("cachew:di-defaults", [6], Expiring());

        await Eventually(() => Task.FromResult(_database.KeyExists("cachew:di-defaults")));
    }

    private static DistributedCacheEntryOptions Expiring()
        => new() { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(1) };

    private static async Task Eventually(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            if (await condition())
            {
                return;
            }

            await Task.Delay(50);
        }

        throw new XunitException("The condition was not satisfied in time.");
    }
}
