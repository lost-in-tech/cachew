using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cachew;

internal sealed class TwoLayerCache : ICachew, IDisposable
{
    private readonly IMemoryCache _memoryCache;
    private readonly IDistributedCache _distributedCache;
    private readonly CachewOptions _options;
    private readonly ILogger<TwoLayerCache>? _logger;
    private readonly ICachewBackplane? _backplane;
    private readonly IDisposable? _subscription;
    private readonly ConcurrentDictionary<string, Task> _inFlight = new();

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
        _logger = logger;
        _backplane = backplane;

        ArgumentNullException.ThrowIfNull(_options.KeyPrefix);
        ArgumentNullException.ThrowIfNull(_options.SerializerOptions);

        _subscription = backplane?.Subscribe(EvictLocal);
    }

    public void Dispose() => _subscription?.Dispose();

    public async Task<T?> GetAsync<T>(string key, CacheEntryOptions<T>? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        var (_, value) = await TryGetAsync<T>(BuildKey(key), options, cancellationToken).ConfigureAwait(false);
        return value;
    }

    public async Task SetAsync<T>(string key, T value, CacheEntryOptions<T>? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        await SetCoreAsync(BuildKey(key), value, options, cancellationToken).ConfigureAwait(false);
    }

    public async Task<T> GetOrCreateAsync<T>(string key, Func<CancellationToken, Task<T>> factory, CacheEntryOptions<T>? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentNullException.ThrowIfNull(factory);

        var cacheKey = BuildKey(key);

        var (found, cached) = await TryGetAsync<T>(cacheKey, options, cancellationToken).ConfigureAwait(false);
        if (found)
        {
            return cached!;
        }

        while (true)
        {
            if (_inFlight.TryGetValue(cacheKey, out var inFlight))
            {
                return await ((Task<T>)inFlight).WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_inFlight.TryAdd(cacheKey, completion.Task))
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

    public async Task RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        var cacheKey = BuildKey(key);

        if (_options.DefaultInMemoryTimeToLive > TimeSpan.Zero)
        {
            _memoryCache.Remove(cacheKey);
        }

        if (_options.DefaultDistributedTimeToLive > TimeSpan.Zero)
        {
            try
            {
                await _distributedCache.RemoveAsync(cacheKey, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // The cache is best effort. A distributed layer failure is not the caller's problem,
                // and the in-memory layer has already been cleared.
            }
        }

        Publish(cacheKey);
    }

    private async Task RunFactoryAsync<T>(string cacheKey, CacheEntryOptions<T>? options, Func<CancellationToken, Task<T>> factory, TaskCompletionSource<T> completion)
    {
        try
        {
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
            _inFlight.TryRemove(cacheKey, out _);
        }
    }

    private async Task SetCoreAsync<T>(string cacheKey, T value, CacheEntryOptions<T>? options, CancellationToken cancellationToken)
    {
        var inMemoryTtl = options?.InMemoryTimeToLive ?? _options.DefaultInMemoryTimeToLive;
        var distributedTtl = options?.DistributedTimeToLive ?? _options.DefaultDistributedTimeToLive;

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
                // The cache is best effort. A distributed layer failure must not fail the caller, and
                // the value is still cached in memory below. Serialization is outside this guard, so a
                // value that cannot be serialized still surfaces.
            }

            if (inMemoryTtl > TimeSpan.Zero)
            {
                SetInMemory(cacheKey, envelope, inMemoryTtl);
            }

            Publish(cacheKey);
            return;
        }

        if (inMemoryTtl > TimeSpan.Zero)
        {
            SetInMemory(cacheKey, new CacheEnvelope<T>(value, null), inMemoryTtl);
        }

        Publish(cacheKey);
    }

    private async Task<(bool Found, T? Value)> TryGetAsync<T>(string cacheKey, CacheEntryOptions<T>? options, CancellationToken cancellationToken)
    {
        var inMemoryTtl = options?.InMemoryTimeToLive ?? _options.DefaultInMemoryTimeToLive;
        var distributedTtl = options?.DistributedTimeToLive ?? _options.DefaultDistributedTimeToLive;

        if (inMemoryTtl > TimeSpan.Zero && _memoryCache.TryGetValue(cacheKey, out CacheEnvelope<T>? local) && local is not null)
        {
            return (true, local.Value);
        }

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
            // The cache is best effort. Treat a distributed layer failure as a miss.
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

    private string BuildKey(string key) => string.Concat(_options.KeyPrefix, key);

    private void EvictLocal(string cacheKey) => _memoryCache.Remove(cacheKey);

    private void Publish(string cacheKey)
    {
        // Nothing to evict anywhere when there is no backplane, or when no instance keeps an
        // in-memory layer at all.
        if (_backplane is null || _options.DefaultInMemoryTimeToLive <= TimeSpan.Zero)
        {
            return;
        }

        try
        {
            _backplane.Publish(cacheKey);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Best effort, like the distributed layer: a missed notification only means another
            // instance keeps a stale copy until that entry expires.
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
            _logger?.LogWarning(exception, "Cache entry '{CacheKey}' could not be deserialized and is treated as a miss.", cacheKey);
            return null;
        }
    }

    private sealed record CacheEnvelope<T>(T Value, DateTimeOffset? ExpiresAtUtc);
}
