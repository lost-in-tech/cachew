using System.Collections.Concurrent;
using DotNet.Testcontainers.Containers;
using Testcontainers.Redis;
using StackExchange.Redis;

namespace Cachew.Redis.Tests;

/// <summary>
/// A dedicated server, so that killing the pub/sub connections can only hit this test's own
/// subscription. That is what makes the verification below unambiguous: on a shared server the kill
/// could land on another test's connection and the test would pass while proving nothing.
/// </summary>
public sealed class BackplaneRedisFixture : IAsyncLifetime
{
    private readonly RedisContainer _container = new RedisBuilder("redis:7.4").Build();

    public IConnectionMultiplexer Connection { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();

        var options = ConfigurationOptions.Parse(_container.GetConnectionString());
        options.AllowAdmin = true;
        options.AbortOnConnectFail = false;
        Connection = await ConnectionMultiplexer.ConnectAsync(options);
    }

    public async Task DisposeAsync()
    {
        await Connection.DisposeAsync();
        await _container.DisposeAsync();
    }
}

[CollectionDefinition(Name)]
public sealed class BackplaneRedisCollection : ICollectionFixture<BackplaneRedisFixture>
{
    public const string Name = "redis-backplane";
}

[Collection(BackplaneRedisCollection.Name)]
public sealed class RedisBackplaneRecoveryTests
{
    private readonly IConnectionMultiplexer _connection;

    public RedisBackplaneRecoveryTests(BackplaneRedisFixture fixture) => _connection = fixture.Connection;

    [Fact]
    public async Task A_subscription_comes_back_after_its_connection_is_killed()
    {
        var database = _connection.GetDatabase();
        var channel = "cachew:test-backplane-recovery";
        using var subscriber = new RedisCacheBackplane(_connection, channel);

        // A second instance, because a publisher ignores its own message.
        using var publisher = new RedisCacheBackplane(_connection, channel);
        var received = new ConcurrentBag<string>();
        using var subscription = subscriber.Subscribe(received.Add);

        await TestWait.Eventually(async () =>
        {
            await publisher.PublishAsync("before");
            return received.Contains("before");
        });

        // The server agrees there is a subscriber for this channel, so a later absence is meaningful.
        Assert.Equal(1, Subscribers(database, channel));

        var killed = (long)await database.ExecuteAsync("CLIENT", "KILL", "TYPE", "pubsub");

        // Nothing else on this server is subscribed, so the kill hit this test's connection. If it had
        // not, the assertion below would pass without the subscription ever having dropped.
        Assert.Equal(1, killed);

        await TestWait.Eventually(async () =>
        {
            await publisher.PublishAsync("after");
            return received.Contains("after");
        });

        Assert.Contains("after", received);
        Assert.Equal(1, Subscribers(database, channel));
    }

    private static long Subscribers(IDatabase database, string channel)
        => (long)database.Execute("PUBSUB", "NUMSUB", channel)[1];
}
