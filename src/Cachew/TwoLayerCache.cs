using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace Cachew;

internal sealed class TwoLayerCache : ICachew
{
    private readonly IMemoryCache _memoryCache;
    private readonly IDistributedCache _distributedCache;
    private readonly CachewOptions _options;
    private readonly ConcurrentDictionary<string, Task> _inFlight = new();

    public TwoLayerCache(IMemoryCache memoryCache, IDistributedCache distributedCache, IOptions<CachewOptions> options)
    {
        ArgumentNullException.ThrowIfNull(memoryCache);
        ArgumentNullException.ThrowIfNull(distributedCache);
        ArgumentNullException.ThrowIfNull(options);

        _memoryCache = memoryCache;
        _distributedCache = distributedCache;
        _options = options.Value;

        ArgumentNullException.ThrowIfNull(_options.KeyPrefix);
        ArgumentNullException.ThrowIfNull(_options.SerializerOptions);
    }

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

        if (_options.DefaultL1TimeToLive > TimeSpan.Zero)
        {
            _memoryCache.Remove(cacheKey);
        }

        if (_options.DefaultL2TimeToLive > TimeSpan.Zero)
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
        var l1Ttl = options?.L1TimeToLive ?? _options.DefaultL1TimeToLive;
        var l2Ttl = options?.L2TimeToLive ?? _options.DefaultL2TimeToLive;

        if (l2Ttl > TimeSpan.Zero)
        {
            var envelope = new CacheEnvelope<T>(value, DateTimeOffset.UtcNow.Add(l2Ttl));
            var payload = Serialize(envelope);

            try
            {
                await _distributedCache.SetAsync(
                    cacheKey,
                    payload,
                    new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = l2Ttl },
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // The cache is best effort. A distributed layer failure must not fail the caller, and
                // the value is still cached in memory below. Serialization is outside this guard, so a
                // value that cannot be serialized still surfaces.
            }

            if (l1Ttl > TimeSpan.Zero)
            {
                SetLocal(cacheKey, envelope, l1Ttl);
            }

            return;
        }

        if (l1Ttl > TimeSpan.Zero)
        {
            SetLocal(cacheKey, new CacheEnvelope<T>(value, null), l1Ttl);
        }
    }

    private async Task<(bool Found, T? Value)> TryGetAsync<T>(string cacheKey, CacheEntryOptions<T>? options, CancellationToken cancellationToken)
    {
        var l1Ttl = options?.L1TimeToLive ?? _options.DefaultL1TimeToLive;
        var l2Ttl = options?.L2TimeToLive ?? _options.DefaultL2TimeToLive;

        if (l1Ttl > TimeSpan.Zero && _memoryCache.TryGetValue(cacheKey, out CacheEnvelope<T>? local) && local is not null)
        {
            return (true, local.Value);
        }

        if (l2Ttl <= TimeSpan.Zero)
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

        // Every value written to L2 carries an expiry. One without it is not a usable entry
        // (an L1 only entry is never written to L2), so treat it as a miss.
        var envelope = Deserialize<T>(payload);
        if (envelope?.ExpiresAtUtc is not { } expiresAtUtc)
        {
            return (false, default);
        }

        var remaining = expiresAtUtc - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero)
        {
            return (false, default);
        }

        if (l1Ttl > TimeSpan.Zero)
        {
            SetLocal(cacheKey, envelope, Min(l1Ttl, remaining));
        }

        return (true, envelope.Value);
    }

    private string BuildKey(string key) => string.Concat(_options.KeyPrefix, key);

    private void SetLocal<T>(string cacheKey, CacheEnvelope<T> envelope, TimeSpan ttl)
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

    private CacheEnvelope<T>? Deserialize<T>(byte[] payload)
        => JsonSerializer.Deserialize<CacheEnvelope<T>>(payload, _options.SerializerOptions);

    private sealed record CacheEnvelope<T>(T Value, DateTimeOffset? ExpiresAtUtc);
}
