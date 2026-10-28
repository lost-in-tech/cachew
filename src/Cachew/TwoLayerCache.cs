using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Cachew;

internal sealed class TwoLayerCache : ICachew, IDisposable
{
    private readonly IMemoryCache _memoryCache;
    private readonly IDistributedCache _distributedCache;
    private readonly CachewOptions _options;
    private readonly ILogger<TwoLayerCache> _logger;
    private readonly ICachewBackplane _backplane;
    private readonly IDisposable _subscription;
    private readonly ConcurrentDictionary<(string Key, Type ValueType), Task> _inFlight = new();

    public TwoLayerCache(
        IMemoryCache memoryCache,
        IDistributedCache distributedCache,
        IOptions<CachewOptions> options,
        ILogger<TwoLayerCache>? logger = null,
        ICachewBackplane? backplane = null)
    {
        ArgumentNullException.ThrowIfNull(memoryCache);
        ArgumentNullException.ThrowIfNull(distributedCache);
        ArgumentNullException.ThrowIfNull(options);

        _memoryCache = memoryCache;
        _distributedCache = distributedCache;
        _options = options.Value;
        _logger = logger ?? NullLogger<TwoLayerCache>.Instance;
        _backplane = backplane ?? NullCacheBackplane.Instance;

        ArgumentNullException.ThrowIfNull(_options.KeyPrefix);
        ArgumentNullException.ThrowIfNull(_options.SerializerOptions);

        _subscription = _backplane.Subscribe(EvictLocal);
    }

    public void Dispose() => _subscription.Dispose();

    public async ValueTask<T?> GetAsync<T>(string key, CacheEntryOptions<T>? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        var (_, value) = await TryGetAsync<T>(BuildKey(key), options, cancellationToken).ConfigureAwait(false);
        return value;
    }

    public async ValueTask SetAsync<T>(string key, T value, CacheEntryOptions<T>? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        await SetCoreAsync(BuildKey(key), value, options, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<T> GetOrCreateAsync<T>(string key, 
        Func<CancellationToken, Task<T>> factory, 
        CacheEntryOptions<T>? options = null, 
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentNullException.ThrowIfNull(factory);

        var cacheKey = BuildKey(key);

        // Only the memory layer is checked here. The distributed read happens inside the single flight,
        // so a burst of misses makes one round trip rather than one per caller.
        if (TryGetInMemory(cacheKey, options, out var cached))
        {
            return cached!;
        }

        // The value type is part of the single flight key because the entry is a Task<T>: two callers
        // using one key as two types have to get their own slot, or the cast below would fault instead
        // of letting each run its own factory.
        var flightKey = (Key: cacheKey, ValueType: typeof(T));

        while (true)
        {
            if (_inFlight.TryGetValue(flightKey, out var inFlight))
            {
                return await ((Task<T>)inFlight).WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_inFlight.TryAdd(flightKey, completion.Task))
            {
                continue;
            }

            // The factory and the write are shared by every caller of this key, so they are not run
            // under any one caller's token. A caller that cancels stops waiting, it does not abandon
            // the work for the others.
            _ = RunFactoryAsync(cacheKey, options, factory, completion);
            return await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async ValueTask RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        var cacheKey = BuildKey(key);

        if (InMemoryEnabled)
        {
            _memoryCache.Remove(cacheKey);
        }

        if (DistributedEnabled)
        {
            try
            {
                await _distributedCache.RemoveAsync(cacheKey, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // Best effort: not the caller's problem, and the in-memory layer is already cleared.
                // Logged because an awaited provider surfaces failures here rather than swallowing them.
                _logger.LogWarning(exception, "Could not remove '{CacheKey}' from the distributed layer.", cacheKey);
            }
        }

        await PublishAsync(cacheKey).ConfigureAwait(false);
    }

    private async Task RunFactoryAsync<T>(string cacheKey, CacheEntryOptions<T>? options, Func<CancellationToken, Task<T>> factory, TaskCompletionSource<T> completion)
    {
        try
        {
            // Also catches a flight for this key that completed between the caller's memory miss and
            // this one taking the slot, which would otherwise run the factory a second time.
            var (found, cached) = await TryGetAsync(cacheKey, options, CancellationToken.None).ConfigureAwait(false);
            if (found)
            {
                completion.TrySetResult(cached!);
                return;
            }

            var created = await factory(CancellationToken.None).ConfigureAwait(false);

            if (options?.ShouldCache is null || options.ShouldCache(created))
            {
                await SetCoreAsync(cacheKey, created, options, CancellationToken.None).ConfigureAwait(false);
            }

            completion.TrySetResult(created);
        }
        catch (OperationCanceledException exception)
        {
            completion.TrySetCanceled(exception.CancellationToken);
        }
        catch (Exception exception)
        {
            completion.TrySetException(exception);
        }
        finally
        {
            _inFlight.TryRemove((cacheKey, typeof(T)), out _);
        }
    }

    private async Task SetCoreAsync<T>(string cacheKey, T value, CacheEntryOptions<T>? options, CancellationToken cancellationToken)
    {
        var inMemoryTtl = InMemoryTimeToLive(options);
        var distributedTtl = DistributedTimeToLive(options);

        if (distributedTtl > TimeSpan.Zero)
        {
            var envelope = new CacheEnvelope<T>(value, DateTimeOffset.UtcNow.Add(distributedTtl));
            var payload = Serialize(envelope);

            try
            {
                await _distributedCache.SetAsync(
                    cacheKey,
                    payload,
                    new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = distributedTtl },
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // Best effort: not the caller's problem, and the value is still cached in memory below.
                // Serialization is outside this guard, so a value that cannot be serialized still
                // surfaces. Logged because an awaited provider surfaces failures here rather than
                // swallowing them.
                _logger.LogWarning(exception, "Could not write '{CacheKey}' to the distributed layer.", cacheKey);
            }

            if (inMemoryTtl > TimeSpan.Zero)
            {
                SetInMemory(cacheKey, envelope, inMemoryTtl);
            }

            await PublishAsync(cacheKey).ConfigureAwait(false);
            return;
        }

        if (inMemoryTtl > TimeSpan.Zero)
        {
            SetInMemory(cacheKey, new CacheEnvelope<T>(value, null), inMemoryTtl);
        }

        await PublishAsync(cacheKey).ConfigureAwait(false);
    }

    private async ValueTask<(bool Found, T? Value)> TryGetAsync<T>(string cacheKey, CacheEntryOptions<T>? options, CancellationToken cancellationToken)
    {
        if (TryGetInMemory(cacheKey, options, out var local))
        {
            return (true, local);
        }

        var inMemoryTtl = InMemoryTimeToLive(options);
        var distributedTtl = DistributedTimeToLive(options);

        if (distributedTtl <= TimeSpan.Zero)
        {
            return (false, default);
        }

        byte[]? payload;
        try
        {
            payload = await _distributedCache.GetAsync(cacheKey, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Best effort: treat a distributed layer failure as a miss. Logged because an awaited
            // provider surfaces failures here rather than swallowing them.
            _logger.LogWarning(exception, "Could not read '{CacheKey}' from the distributed layer.", cacheKey);
            return (false, default);
        }

        if (payload is null)
        {
            return (false, default);
        }

        // Every value written to the distributed layer carries an expiry. One without it is not a
        // usable entry (an in-memory only entry is never written there), so treat it as a miss.
        var envelope = Deserialize<T>(payload, cacheKey);
        if (envelope?.ExpiresAtUtc is not { } expiresAtUtc)
        {
            return (false, default);
        }

        var remaining = expiresAtUtc - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero)
        {
            return (false, default);
        }

        if (inMemoryTtl > TimeSpan.Zero)
        {
            SetInMemory(cacheKey, envelope, Min(inMemoryTtl, remaining));
        }

        return (true, envelope.Value);
    }

    private bool TryGetInMemory<T>(string cacheKey, CacheEntryOptions<T>? options, out T? value)
    {
        if (InMemoryTimeToLive(options) > TimeSpan.Zero && _memoryCache.TryGetValue(cacheKey, out CacheEnvelope<T>? local) && local is not null)
        {
            value = local.Value;
            return true;
        }

        value = default;
        return false;
    }

    // A disabled cache wins over per entry overrides: turning it off from configuration has to turn
    // it off everywhere. The same goes for a layer that is off through a zero default, so a per entry
    // time to live can adjust an enabled layer but cannot switch one on. Keeping that rule here is what
    // lets remove and publish decide from the default alone and still agree with what a write did.
    private TimeSpan InMemoryTimeToLive<T>(CacheEntryOptions<T>? options)
        => InMemoryEnabled ? options?.InMemoryTimeToLive ?? _options.DefaultInMemoryTimeToLive : TimeSpan.Zero;

    private TimeSpan DistributedTimeToLive<T>(CacheEntryOptions<T>? options)
        => DistributedEnabled ? options?.DistributedTimeToLive ?? _options.DefaultDistributedTimeToLive : TimeSpan.Zero;

    private bool InMemoryEnabled => _options.Enabled && _options.DefaultInMemoryTimeToLive > TimeSpan.Zero;

    private bool DistributedEnabled => _options.Enabled && _options.DefaultDistributedTimeToLive > TimeSpan.Zero;

    private string BuildKey(string key) => string.Concat(_options.KeyPrefix, key);

    private void EvictLocal(string cacheKey) => _memoryCache.Remove(cacheKey);

    private async Task PublishAsync(string cacheKey)
    {
        // Nothing to evict anywhere when no instance keeps an in-memory layer at all.
        if (!InMemoryEnabled)
        {
            return;
        }

        try
        {
            // Costs nothing with a provider that sends fire-and-forget, and waits when it does not.
            await _backplane.PublishAsync(cacheKey).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(exception, "Could not publish an invalidation for '{CacheKey}'.", cacheKey);
        }
    }

    private void SetInMemory<T>(string cacheKey, CacheEnvelope<T> envelope, TimeSpan ttl)
    {
        _memoryCache.Set(cacheKey, envelope, new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = ttl,
            Size = 1,
        });
    }

    private static TimeSpan Min(TimeSpan left, TimeSpan right) => left < right ? left : right;

    private byte[] Serialize<T>(CacheEnvelope<T> envelope)
        => JsonSerializer.SerializeToUtf8Bytes(envelope, _options.SerializerOptions);

    private CacheEnvelope<T>? Deserialize<T>(byte[] payload, string cacheKey)
    {
        try
        {
            return JsonSerializer.Deserialize<CacheEnvelope<T>>(payload, _options.SerializerOptions);
        }
        catch (JsonException exception)
        {
            // The payload does not fit the type being read, which means a stale entry from an older
            // shape or one key used with two types. Serve a miss and leave a trail rather than fail
            // the caller; the next write replaces the entry.
            _logger.LogWarning(exception, "Cache entry '{CacheKey}' could not be deserialized and is treated as a miss.", cacheKey);
            return null;
        }
    }

    private sealed record CacheEnvelope<T>(T Value, DateTimeOffset? ExpiresAtUtc);
}
