using Microsoft.Extensions.Caching.Distributed;
using NSubstitute;
using StackExchange.Redis;

namespace Cachew.Redis.Tests;

public class RedisDistributedCacheTests
{
    private static readonly byte[] Payload = [1, 2, 3];

    [Fact]
    public async Task A_write_with_an_expiry_is_sent_fire_and_forget()
    {
        var (cache, database) = CreateCache();

        await cache.SetAsync("key", Payload, new DistributedCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(60),
        });

        await database.Received(1).StringSetAsync(
            "key", Payload, TimeSpan.FromSeconds(60), When.Always, CommandFlags.FireAndForget);
    }

    [Fact]
    public async Task A_write_without_an_expiry_is_sent_fire_and_forget()
    {
        var (cache, database) = CreateCache();

        await cache.SetAsync("key", Payload, new DistributedCacheEntryOptions());

        await database.Received(1).StringSetAsync(
            "key", Payload, null, When.Always, CommandFlags.FireAndForget);
    }

    [Fact]
    public async Task Sliding_expiration_is_not_supported()
    {
        var (cache, _) = CreateCache();

        await Assert.ThrowsAsync<NotSupportedException>(() => cache.SetAsync("key", Payload,
            new DistributedCacheEntryOptions { SlidingExpiration = TimeSpan.FromSeconds(30) }));
    }

    [Fact]
    public async Task An_expiry_in_the_past_writes_nothing()
    {
        var (cache, database) = CreateCache();

        await cache.SetAsync("key", Payload, new DistributedCacheEntryOptions
        {
            AbsoluteExpiration = DateTimeOffset.UtcNow.AddSeconds(-1),
        });

        await database.DidNotReceive().StringSetAsync(
            Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<TimeSpan?>(), Arg.Any<When>(), Arg.Any<CommandFlags>());
    }

    [Fact]
    public async Task A_stored_value_can_be_read_back_from_redis()
    {
        var (cache, database) = CreateCache();
        database.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns((RedisValue)Payload);

        Assert.Equal(Payload, await cache.GetAsync("key"));
    }

    [Fact]
    public async Task A_missing_key_reads_as_nothing()
    {
        var (cache, database) = CreateCache();
        database.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(RedisValue.Null);

        Assert.Null(await cache.GetAsync("key"));
    }

    [Fact]
    public async Task A_remove_deletes_the_key_fire_and_forget()
    {
        var (cache, database) = CreateCache();

        await cache.RemoveAsync("key");

        await database.Received(1).KeyDeleteAsync("key", CommandFlags.FireAndForget);
    }

    private static (RedisDistributedCache Cache, IDatabase Database) CreateCache()
    {
        var database = Substitute.For<IDatabase>();
        var connection = Substitute.For<IConnectionMultiplexer>();
        connection.GetDatabase().Returns(database);
        return (new RedisDistributedCache(connection), database);
    }
}
