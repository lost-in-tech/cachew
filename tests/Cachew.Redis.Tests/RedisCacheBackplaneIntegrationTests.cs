using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using StackExchange.Redis;

namespace Cachew.Redis.Tests;

[Collection(RedisCollection.Name)]
public sealed class RedisCacheBackplaneIntegrationTests
{
    private readonly IConnectionMultiplexer _connection;
    private readonly ISubscriber _subscriber;

    public RedisCacheBackplaneIntegrationTests(RedisFixture fixture)
    {
        _connection = fixture.Connection;
        _subscriber = fixture.Connection.GetSubscriber();
    }

    [Fact]
    public async Task Publish_IsReceivedByAnotherBackplane()
    {
        const string channel = "cachew:test-backplane-receive";
        using var publisher = new RedisCacheBackplane(_connection, channel);
        using var subscriber = new RedisCacheBackplane(_connection, channel);
        var received = new List<string>();
        using var subscription = subscriber.Subscribe(received.Add);

        await TestWait.Eventually(() =>
        {
            publisher.Publish("warmup");
            return Task.FromResult(received.Contains("warmup"));
        });

        publisher.Publish("cachew:key");

        await TestWait.Eventually(() => Task.FromResult(received.Contains("cachew:key")));
    }

    [Fact]
    public async Task Publish_IsNotEchoedBackToThePublisher_ButIsSeenByOthers()
    {
        const string channel = "cachew:test-backplane-echo";
        using var self = new RedisCacheBackplane(_connection, channel);
        using var other = new RedisCacheBackplane(_connection, channel);
        var selfReceived = new List<string>();
        var otherReceived = new List<string>();
        using var selfSubscription = self.Subscribe(selfReceived.Add);
        using var otherSubscription = other.Subscribe(otherReceived.Add);

        // `self` subscribing proves the channel is live, since it is the instance whose messages must
        // not come back to it.
        await TestWait.Eventually(() =>
        {
            other.Publish("warmup");
            return Task.FromResult(selfReceived.Contains("warmup"));
        });

        self.Publish("cachew:key");

        await TestWait.Eventually(() => Task.FromResult(otherReceived.Contains("cachew:key")));

        Assert.Contains("cachew:key", otherReceived);
        Assert.DoesNotContain("cachew:key", selfReceived);
    }

    [Fact]
    public async Task UnreadableMessages_AreIgnored()
    {
        const string channel = "cachew:test-backplane-unreadable";
        var redisChannel = RedisChannel.Literal(channel);
        using var backplane = new RedisCacheBackplane(_connection, channel);
        var received = new List<string>();
        using var subscription = backplane.Subscribe(received.Add);

        await TestWait.Eventually(async () =>
        {
            await _subscriber.PublishAsync(redisChannel, "{\"version\":1,\"origin\":\"other\",\"key\":\"warmup\"}");
            return received.Contains("warmup");
        });

        await _subscriber.PublishAsync(redisChannel, "not json");
        await _subscriber.PublishAsync(redisChannel, "{\"version\":99,\"origin\":\"other\",\"key\":\"future\"}");
        await _subscriber.PublishAsync(redisChannel, "{\"version\":1,\"origin\":\"other\",\"key\":\"cachew:key\"}");

        await TestWait.Eventually(() => Task.FromResult(received.Contains("cachew:key")));

        Assert.DoesNotContain("future", received);
    }

    [Fact]
    public async Task RemovingOnOneInstance_EvictsTheMemoryLayerOfTheOther()
    {
        const string channel = "cachew:test-backplane-evict";
        using var providerA = CreateProvider(channel);
        using var providerB = CreateProvider(channel);
        var cacheA = providerA.GetRequiredService<ICachew>();
        var cacheB = providerB.GetRequiredService<ICachew>();
        var memoryB = providerB.GetRequiredService<IMemoryCache>();
        const string key = "evict-me";
        var cacheKey = "cachew:" + key;

        await cacheA.SetAsync(key, "value");

        // B reads it, which promotes it into B's own memory layer.
        await TestWait.Eventually(async () => await cacheB.GetAsync<string>(key) is not null);
        Assert.True(memoryB.TryGetValue(cacheKey, out _));

        await cacheA.RemoveAsync(key);

        await TestWait.Eventually(() => Task.FromResult(!memoryB.TryGetValue(cacheKey, out _)));
        Assert.False(memoryB.TryGetValue(cacheKey, out _));
    }

    [Fact]
    public void Dispose_Twice_IsSafe_AndSubscribeAfterDisposeThrows()
    {
        var backplane = new RedisCacheBackplane(_connection, "cachew:test-backplane-dispose");

        backplane.Dispose();
        backplane.Dispose();

        Assert.Throws<ObjectDisposedException>(() => backplane.Subscribe(_ => { }));
    }

    [Fact]
    public async Task UnreadableMessage_IsLogged()
    {
        const string channel = "cachew:test-backplane-logs";
        var redisChannel = RedisChannel.Literal(channel);
        var logger = Substitute.For<ILogger<RedisCacheBackplane>>();
        using var backplane = new RedisCacheBackplane(_connection, channel, logger);
        var received = new List<string>();
        using var subscription = backplane.Subscribe(received.Add);

        await TestWait.Eventually(async () =>
        {
            await _subscriber.PublishAsync(redisChannel, "{\"version\":1,\"origin\":\"other\",\"key\":\"warmup\"}");
            return received.Contains("warmup");
        });

        await _subscriber.PublishAsync(redisChannel, "not json");

        await TestWait.Eventually(() => Task.FromResult(
            logger.ReceivedCalls().Any(call => call.GetMethodInfo().Name == "Log")));
    }

    private ServiceProvider CreateProvider(string channel)
    {
        var services = new ServiceCollection();
        services.AddSingleton(_connection);
        services.AddCachewRedis();
        services.AddCachew();
        services.WithBackplane(channel);
        return services.BuildServiceProvider();
    }
}
