using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Options;
using NSubstitute;
using StackExchange.Redis;

namespace Cachew.Redis.Tests;

/// <summary>
/// Fire-and-forget is the default because it removes the wait on the write that follows a miss. These
/// pin what it means in both modes: the flag sent to Redis, and whether the call waits.
/// </summary>
public class RedisFireAndForgetTests
{
    [Fact]
    public void FireAndForgetIsOnByDefault()
    {
        Assert.True(new RedisCachewOptions().FireAndForget);
    }

    [Fact]
    public async Task ByDefault_AWriteIsSentFireAndForget()
    {
        var (cache, database) = CreateCache();

        await cache.SetAsync("key", [1], Expiring());

        await database.Received(1).StringSetAsync(
            "key", Arg.Any<RedisValue>(), Arg.Any<TimeSpan?>(), When.Always, CommandFlags.FireAndForget);
    }

    [Fact]
    public async Task ByDefault_ARemoveIsSentFireAndForget()
    {
        var (cache, database) = CreateCache();

        await cache.RemoveAsync("key");

        await database.Received(1).KeyDeleteAsync("key", CommandFlags.FireAndForget);
    }

    [Fact]
    public async Task WhenFireAndForgetIsOff_AWriteWaitsForRedis()
    {
        var database = Substitute.For<IDatabase>();
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        database.StringSetAsync("key", Arg.Any<RedisValue>(), Arg.Any<TimeSpan?>(), When.Always, CommandFlags.None)
            .Returns(gate.Task);
        var cache = CreateCache(database, fireAndForget: false);

        var write = cache.SetAsync("key", [1], Expiring());

        await Task.Delay(50);
        Assert.False(write.IsCompleted);

        gate.SetResult(true);
        await write;
    }

    [Fact]
    public async Task WhenFireAndForgetIsOff_ARemoveWaitsForRedis()
    {
        var database = Substitute.For<IDatabase>();
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        database.KeyDeleteAsync("key", CommandFlags.None).Returns(gate.Task);
        var cache = CreateCache(database, fireAndForget: false);

        var remove = cache.RemoveAsync("key");

        await Task.Delay(50);
        Assert.False(remove.IsCompleted);

        gate.SetResult(true);
        await remove;
    }

    [Fact]
    public async Task ByDefault_AnInvalidationIsPublishedFireAndForget()
    {
        var subscriber = Substitute.For<ISubscriber>();
        var backplane = new RedisCacheBackplane(Connection(subscriber));

        await backplane.PublishAsync("key");

        subscriber.Received(1).Publish(
            Arg.Any<RedisChannel>(), Arg.Any<RedisValue>(), CommandFlags.FireAndForget);
    }

    [Fact]
    public async Task WhenFireAndForgetIsOff_AnInvalidationWaitsForRedis()
    {
        var subscriber = Substitute.For<ISubscriber>();
        var gate = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        subscriber.PublishAsync(Arg.Any<RedisChannel>(), Arg.Any<RedisValue>(), CommandFlags.None).Returns(gate.Task);
        var connection = Connection(subscriber);
        var backplane = new RedisCacheBackplane(
            connection, RedisCacheBackplane.DefaultChannel, options: Options.Create(new RedisCachewOptions { FireAndForget = false }));

        var publish = backplane.PublishAsync("key");

        await Task.Delay(50);
        Assert.False(publish.IsCompleted);

        gate.SetResult(1);
        await publish;
    }

    private static DistributedCacheEntryOptions Expiring()
        => new() { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(1) };

    private static IConnectionMultiplexer Connection(ISubscriber subscriber)
    {
        var connection = Substitute.For<IConnectionMultiplexer>();
        connection.GetSubscriber().Returns(subscriber);
        return connection;
    }

    private static (RedisDistributedCache Cache, IDatabase Database) CreateCache(bool fireAndForget = true)
    {
        var database = Substitute.For<IDatabase>();
        return (CreateCache(database, fireAndForget), database);
    }

    private static RedisDistributedCache CreateCache(IDatabase database, bool fireAndForget = true)
        => new(ConnectionFor(database), Options.Create(new RedisCachewOptions { FireAndForget = fireAndForget }));

    private static IConnectionMultiplexer ConnectionFor(IDatabase database)
    {
        var connection = Substitute.For<IConnectionMultiplexer>();
        connection.GetDatabase().Returns(database);
        return connection;
    }
}
