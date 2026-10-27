using System.Collections.Concurrent;
using System.Text.Json;
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
    private readonly IConnectionMultiplexer _secondConnection;
    private readonly IDatabase _database;
    private readonly ISubscriber _subscriber;

    public RedisCacheBackplaneIntegrationTests(RedisFixture fixture)
    {
        _connection = fixture.Connection;
        _secondConnection = fixture.SecondConnection;
        _database = fixture.Connection.GetDatabase();
        _subscriber = fixture.Connection.GetSubscriber();
    }

    [Fact]
    public async Task A_published_invalidation_reaches_another_instance()
    {
        const string channel = "cachew:test-backplane-receive";
        using var publisher = new RedisCacheBackplane(_connection, channel);
        using var subscriber = new RedisCacheBackplane(_connection, channel);
        var received = new ConcurrentBag<string>();
        using var subscription = subscriber.Subscribe(received.Add);

        await TestWait.Eventually(async () =>
        {
            await publisher.PublishAsync("warmup");
            return received.Contains("warmup");
        });

        // Published in the loop rather than once: delivery is best effort, so a single
        // fire-and-forget publish is not guaranteed to arrive.
        await TestWait.Eventually(async () =>
        {
            await publisher.PublishAsync("cachew:key");
            return received.Contains("cachew:key");
        });
    }

    [Fact]
    public async Task An_invalidation_is_not_echoed_to_its_publisher_but_is_seen_by_others()
    {
        const string channel = "cachew:test-backplane-echo";
        using var self = new RedisCacheBackplane(_connection, channel);
        using var other = new RedisCacheBackplane(_connection, channel);
        var selfReceived = new ConcurrentBag<string>();
        var otherReceived = new ConcurrentBag<string>();
        using var selfSubscription = self.Subscribe(selfReceived.Add);
        using var otherSubscription = other.Subscribe(otherReceived.Add);

        // `self` subscribing proves the channel is live, since it is the instance whose messages must
        // not come back to it.
        await TestWait.Eventually(async () =>
        {
            await other.PublishAsync("warmup");
            return selfReceived.Contains("warmup");
        });

        await self.PublishAsync("cachew:key");

        await TestWait.Eventually(async () =>
        {
            await self.PublishAsync("cachew:key");
            return otherReceived.Contains("cachew:key");
        });

        Assert.Contains("cachew:key", otherReceived);
        Assert.DoesNotContain("cachew:key", selfReceived);
    }

    [Fact]
    public async Task An_unreadable_message_is_ignored()
    {
        const string channel = "cachew:test-backplane-unreadable";
        var redisChannel = RedisChannel.Literal(channel);
        using var backplane = new RedisCacheBackplane(_connection, channel);
        var received = new ConcurrentBag<string>();
        using var subscription = backplane.Subscribe(received.Add);

        await TestWait.Eventually(async () =>
        {
            await _subscriber.PublishAsync(redisChannel, "{\"version\":1,\"origin\":\"other\",\"key\":\"warmup\"}");
            return received.Contains("warmup");
        });

        await TestWait.Eventually(async () =>
        {
            await _subscriber.PublishAsync(redisChannel, "not json");
            await _subscriber.PublishAsync(redisChannel, "{\"version\":99,\"origin\":\"other\",\"key\":\"future\"}");
            await _subscriber.PublishAsync(redisChannel, "{\"version\":1,\"origin\":\"other\",\"key\":\"cachew:key\"}");
            return received.Contains("cachew:key");
        });

        Assert.DoesNotContain("future", received);
    }

    [Fact]
    public async Task Removing_a_key_on_one_instance_evicts_it_on_another()
    {
        const string channel = "cachew:test-backplane-evict";
        using var providerA = CreateProvider(_connection, channel);
        using var providerB = CreateProvider(_secondConnection, channel);
        var cacheA = providerA.GetRequiredService<ICachew>();
        var cacheB = providerB.GetRequiredService<ICachew>();
        var memoryB = providerB.GetRequiredService<IMemoryCache>();
        const string key = "evict-me";
        var cacheKey = "cachew:" + key;

        // Seeded directly in Redis so that no notification is in flight while B promotes it: a
        // notification from an earlier write would evict the copy B has just taken.
        await _database.StringSetAsync(cacheKey, Payload("value"), TimeSpan.FromMinutes(5));

        // B reads it, which promotes it into B's own memory layer.
        await TestWait.Eventually(async () => await cacheB.GetAsync<string>(key) is not null);
        Assert.True(memoryB.TryGetValue(cacheKey, out _));

        // Removed in the loop rather than once: the notification is best effort, so a single
        // fire-and-forget publish is not guaranteed to arrive.
        await TestWait.Eventually(async () =>
        {
            await cacheA.RemoveAsync(key);
            return !memoryB.TryGetValue(cacheKey, out _);
        });

        Assert.False(memoryB.TryGetValue(cacheKey, out _));
    }

    [Fact]
    public void Disposing_twice_is_safe_and_subscribing_afterwards_throws()
    {
        var backplane = new RedisCacheBackplane(_connection, "cachew:test-backplane-dispose");

        backplane.Dispose();
        backplane.Dispose();

        Assert.Throws<ObjectDisposedException>(() => backplane.Subscribe(_ => { }));
    }

    [Fact]
    public async Task An_unreadable_message_is_logged()
    {
        const string channel = "cachew:test-backplane-logs";
        var redisChannel = RedisChannel.Literal(channel);
        var logger = Substitute.For<ILogger<RedisCacheBackplane>>();
        using var backplane = new RedisCacheBackplane(_connection, channel, logger);
        var received = new ConcurrentBag<string>();
        using var subscription = backplane.Subscribe(received.Add);

        await TestWait.Eventually(async () =>
        {
            await _subscriber.PublishAsync(redisChannel, "{\"version\":1,\"origin\":\"other\",\"key\":\"warmup\"}");
            return received.Contains("warmup");
        });

        await TestWait.Eventually(async () =>
        {
            await _subscriber.PublishAsync(redisChannel, "not json");
            return logger.ReceivedCalls().Any(call => call.GetMethodInfo().Name == "Log");
        });
    }

    [Fact]
    public void No_backplane_is_registered_unless_it_is_asked_for()
    {
        using var provider = CreateProvider(_connection, channel: null);

        Assert.Null(provider.GetService<ICachewBackplane>());
    }

    [Fact]
    public void The_backplane_registration_registers_the_redis_backplane()
    {
        using var provider = CreateProvider(_connection, "cachew:test-backplane-registration");

        Assert.IsType<RedisCacheBackplane>(provider.GetRequiredService<ICachewBackplane>());
    }

    [Fact]
    public async Task A_write_on_one_instance_evicts_the_other_memory_layer_but_leaves_redis_alone()
    {
        const string channel = "cachew:test-backplane-set";
        using var providerA = CreateProvider(_connection, channel);
        using var providerB = CreateProvider(_secondConnection, channel);
        var cacheA = providerA.GetRequiredService<ICachew>();
        var cacheB = providerB.GetRequiredService<ICachew>();
        var memoryB = providerB.GetRequiredService<IMemoryCache>();
        const string key = "set-me";
        var cacheKey = "cachew:" + key;

        // Seeded directly in Redis so that no notification is in flight while B promotes it.
        await _database.StringSetAsync(cacheKey, Payload("v1"), TimeSpan.FromMinutes(5));
        await TestWait.Eventually(async () => await cacheB.GetAsync<string>(key) is "v1");
        Assert.True(memoryB.TryGetValue(cacheKey, out _));

        // A stores a second version. B has to drop its own copy, and the stored value has to survive:
        // an invalidation evicts the in-memory copy of the instance that receives it, nothing else.
        // Stored in the loop rather than once, because the notification is best effort.
        await TestWait.Eventually(async () =>
        {
            await cacheA.SetAsync(key, "v2");
            return !memoryB.TryGetValue(cacheKey, out _);
        });

        await TestWait.Eventually(async () => await cacheB.GetAsync<string>(key) is "v2");
        Assert.True(_connection.GetDatabase().KeyExists(cacheKey));
    }

    private static string Payload(string value) =>
        $"{{\"value\":{JsonSerializer.Serialize(value)},\"expiresAtUtc\":\"{DateTimeOffset.UtcNow.AddMinutes(5):O}\"}}";

    private static ServiceProvider CreateProvider(IConnectionMultiplexer connection, string? channel)
    {
        var services = new ServiceCollection();
        services.AddSingleton(connection);
        services.AddCachewRedis();
        services.AddCachew();
        if (channel is not null)
        {
            services.AddCachewBackplane(channel);
        }

        return services.BuildServiceProvider();
    }
}
