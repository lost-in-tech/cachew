using System.Text.Json;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Cachew.Redis;

/// <summary>
/// An <see cref="ICachewBackplane"/> over Redis pub/sub, so that invalidating a key on one instance
/// also drops the in-memory copy held by every other instance. It shares the connection multiplexer
/// used by <see cref="RedisDistributedCache"/>.
/// </summary>
/// <remarks>
/// Delivery is best effort. A message that is lost, or published while an instance is
/// reconnecting, leaves that instance with a stale in-memory entry until the entry expires, which is
/// the behaviour without a backplane. Messages published before an instance subscribes are not
/// replayed.
/// </remarks>
public sealed class RedisCacheBackplane : ICachewBackplane, IDisposable
{
    /// <summary>The channel used when none is supplied.</summary>
    public const string DefaultChannel = "cachew:invalidate";

    private const int CurrentVersion = 1;

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly ISubscriber _subscriber;
    private readonly RedisChannel _channel;
    private readonly ILogger<RedisCacheBackplane>? _logger;
    private readonly string _origin = Guid.NewGuid().ToString("N");
    private readonly List<IDisposable> _subscriptions = [];
    private bool _disposed;

    /// <summary>Creates the backplane over an existing connection multiplexer.</summary>
    /// <param name="connection">The Redis connection multiplexer to use.</param>
    /// <param name="channel">
    /// The pub/sub channel. Instances that must see each other's invalidations have to agree on this
    /// value. Sharing a channel with an unrelated application is harmless: evicting a key that is not
    /// there does nothing.
    /// </param>
    /// <param name="logger">Optional logger for unreadable messages.</param>
    public RedisCacheBackplane(
        IConnectionMultiplexer connection,
        string channel = DefaultChannel,
        ILogger<RedisCacheBackplane>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrEmpty(channel);

        _subscriber = connection.GetSubscriber();
        _channel = RedisChannel.Literal(channel);
        _logger = logger;
    }

    /// <inheritdoc />
    public void Publish(string key)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        var message = new Message(CurrentVersion, _origin, key);

        _subscriber.Publish(_channel, JsonSerializer.Serialize(message, SerializerOptions), CommandFlags.FireAndForget);
    }

    /// <inheritdoc />
    public IDisposable Subscribe(Action<string> onInvalidate)
    {
        ArgumentNullException.ThrowIfNull(onInvalidate);
        ObjectDisposedException.ThrowIf(_disposed, this);

        void Handler(RedisChannel _, RedisValue value)
        {
            if (Read(value) is { } key)
            {
                onInvalidate(key);
            }
        }

        _subscriber.Subscribe(_channel, Handler);

        var subscription = new Subscription(() => _subscriber.Unsubscribe(_channel, Handler));
        _subscriptions.Add(subscription);
        return subscription;
    }

    /// <summary>Unsubscribes from the channel.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (var subscription in _subscriptions)
        {
            subscription.Dispose();
        }

        _subscriptions.Clear();
    }

    private string? Read(RedisValue value)
    {
        try
        {
            var message = JsonSerializer.Deserialize<Message>(value.ToString(), SerializerOptions);
            if (message is null || message.Version != CurrentVersion)
            {
                return null;
            }

            // Redis delivers a message back to the publisher's own subscription, so ignore our own.
            return message.Origin == _origin ? null : message.Key;
        }
        catch (JsonException exception)
        {
            _logger?.LogWarning(
                exception,
                "Ignoring an unreadable cache invalidation message on channel '{Channel}'.",
                _channel);
            return null;
        }
    }

    private sealed record Message(int Version, string Origin, string Key);

    private sealed class Subscription(Action unsubscribe) : IDisposable
    {
        public void Dispose() => unsubscribe();
    }
}
