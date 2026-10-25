using Microsoft.Extensions.Caching.Distributed;
using StackExchange.Redis;

namespace Cachew.Redis;

/// <summary>
/// An <see cref="IDistributedCache"/> backed by Redis where every mutation is issued with
/// <see cref="CommandFlags.FireAndForget"/>. The command is handed to the connection as soon as
/// the call returns, but the caller does not wait for the server reply and a write failure on the
/// server is not surfaced. Reads are always awaited.
/// </summary>
/// <remarks>
/// Sliding expiration is not supported. A <see cref="DistributedCacheEntryOptions"/> with a
/// sliding expiration passed to <see cref="Set"/> or <see cref="SetAsync"/> throws
/// <see cref="NotSupportedException"/>, and <see cref="Refresh"/> is a no-op.
/// Cancellation tokens are not honoured because StackExchange.Redis does not accept them.
/// </remarks>
public sealed class RedisDistributedCache : IDistributedCache
{
    private readonly IDatabase _database;

    /// <summary>Creates the cache over an existing connection multiplexer.</summary>
    /// <param name="connection">The Redis connection multiplexer to use.</param>
    public RedisDistributedCache(IConnectionMultiplexer connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        _database = connection.GetDatabase();
    }

    /// <inheritdoc />
    public byte[]? Get(string key)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        var value = _database.StringGet(key);
        return value.IsNull ? null : (byte[]?)value;
    }

    /// <inheritdoc />
    public async Task<byte[]?> GetAsync(string key, CancellationToken token = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        var value = await _database.StringGetAsync(key).ConfigureAwait(false);
        return value.IsNull ? null : (byte[]?)value;
    }

    /// <inheritdoc />
    public void Set(string key, byte[] value, DistributedCacheEntryOptions options)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(options);

        var expiry = GetExpiry(options);
        if (expiry <= TimeSpan.Zero)
        {
            return;
        }

        _database.StringSet(key, value, expiry, when: When.Always, flags: CommandFlags.FireAndForget);
    }

    /// <inheritdoc />
    public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(options);

        var expiry = GetExpiry(options);
        if (expiry <= TimeSpan.Zero)
        {
            return Task.CompletedTask;
        }

        _database.StringSetAsync(key, value, expiry, when: When.Always, flags: CommandFlags.FireAndForget);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void Remove(string key)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        _database.KeyDelete(key, CommandFlags.FireAndForget);
    }

    /// <inheritdoc />
    public Task RemoveAsync(string key, CancellationToken token = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        _database.KeyDeleteAsync(key, CommandFlags.FireAndForget);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void Refresh(string key)
    {
    }

    /// <inheritdoc />
    public Task RefreshAsync(string key, CancellationToken token = default) => Task.CompletedTask;

    private static TimeSpan? GetExpiry(DistributedCacheEntryOptions options)
    {
        if (options.SlidingExpiration is not null)
        {
            throw new NotSupportedException($"{nameof(RedisDistributedCache)} does not support sliding expiration.");
        }

        if (options.AbsoluteExpirationRelativeToNow is { } relative)
        {
            return relative;
        }

        if (options.AbsoluteExpiration is { } absolute)
        {
            return absolute - DateTimeOffset.UtcNow;
        }

        return null;
    }
}
