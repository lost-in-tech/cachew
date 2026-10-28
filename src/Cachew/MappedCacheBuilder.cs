namespace Cachew;

/// <summary>
/// Entry points for a get-or-create operation whose factory result differs from the value that is
/// cached and returned, for example an HTTP response that is inspected before its payload is cached.
/// </summary>
public static class CachewMappedExtensions
{
    /// <summary>
    /// Starts a fluent get-or-create operation where the factory result differs from the cached and
    /// returned value. The factory is invoked on a cache miss.
    /// </summary>
    /// <typeparam name="TResult">The type the factory produces and the cache predicate inspects.</typeparam>
    /// <typeparam name="T">The cached and returned value type.</typeparam>
    /// <param name="cache">The cache.</param>
    /// <param name="factory">Produces the value on a miss.</param>
    /// <param name="select">Maps the produced value to what is cached and returned.</param>
    public static CacheBuilder<TResult, T> OnCacheMiss<TResult, T>(
        this ICachew cache,
        Func<CancellationToken, Task<TResult>> factory,
        Func<TResult, T> select)
    {
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(select);

        return new CacheBuilder<TResult, T>(cache, factory, select);
    }

    /// <summary>
    /// Gets a value, invoking <paramref name="factory"/> on a miss and caching
    /// <paramref name="select"/> of its result. Concurrent misses for the same key within one process
    /// share a single <paramref name="factory"/> invocation, and the coalesced callers share the first
    /// caller's <paramref name="select"/>, <paramref name="shouldCache"/> and expiry overrides.
    /// </summary>
    /// <typeparam name="TResult">The type the factory produces and <paramref name="shouldCache"/> inspects.</typeparam>
    /// <typeparam name="T">The cached and returned value type.</typeparam>
    /// <param name="cache">The cache.</param>
    /// <param name="key">The cache key.</param>
    /// <param name="factory">Produces the value on a miss.</param>
    /// <param name="select">Maps the produced value to what is cached and returned. Always invoked on a
    /// miss, including when the value is not cached, so it has to be defined for every result.</param>
    /// <param name="shouldCache">
    /// Decides from the factory result whether the mapped value is cached. When null, every mapped
    /// value is cached.
    /// </param>
    /// <param name="inMemoryTimeToLive">In-memory layer time to live, or null for the default.</param>
    /// <param name="distributedTimeToLive">Distributed layer time to live, or null for the default.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    public static async ValueTask<T> GetOrCreateAsync<TResult, T>(
        this ICachew cache,
        string key,
        Func<CancellationToken, Task<TResult>> factory,
        Func<TResult, T> select,
        Func<TResult, bool>? shouldCache = null,
        TimeSpan? inMemoryTimeToLive = null,
        TimeSpan? distributedTimeToLive = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(select);

        // The core decides caching from the mapped value, because that is what it stores. The decision
        // belongs to the factory result, so it is captured here and replayed for that value. This runs
        // on the winning caller only, which is the caller whose options the others already share.
        var cacheable = true;
        CacheEntryOptions<T>? options = shouldCache is null && inMemoryTimeToLive is null && distributedTimeToLive is null
            ? null
            : new CacheEntryOptions<T>
            {
                InMemoryTimeToLive = inMemoryTimeToLive,
                DistributedTimeToLive = distributedTimeToLive,
                ShouldCache = shouldCache is null ? null : _ => cacheable,
            };

        return await cache.GetOrCreateAsync<T>(
            key,
            async token =>
            {
                var result = await factory(token).ConfigureAwait(false);
                cacheable = shouldCache?.Invoke(result) ?? true;
                return select(result);
            },
            options,
            cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// Builds a get-or-create operation where the factory result and the cache predicate differ from the
/// cached and returned value. A builder can be reused across keys. It is not safe to use a builder
/// from multiple threads while configuring it.
/// </summary>
/// <typeparam name="TResult">The type the factory produces and the cache predicate inspects.</typeparam>
/// <typeparam name="T">The cached and returned value type.</typeparam>
public sealed class CacheBuilder<TResult, T>
{
    private readonly ICachew _cache;
    private readonly Func<CancellationToken, Task<TResult>> _factory;
    private readonly Func<TResult, T> _select;

    private Func<TResult, bool>? _shouldCache;
    private TimeSpan? _inMemoryTimeToLive;
    private TimeSpan? _distributedTimeToLive;

    internal CacheBuilder(ICachew cache, Func<CancellationToken, Task<TResult>> factory, Func<TResult, T> select)
    {
        _cache = cache;
        _factory = factory;
        _select = select;
    }

    /// <summary>
    /// Caches the mapped value only when <paramref name="predicate"/> returns true for the factory
    /// result. When unset, every mapped value is cached. The mapped value is returned either way.
    /// </summary>
    /// <param name="predicate">Decides from the factory result whether the mapped value is cached.</param>
    public CacheBuilder<TResult, T> CacheIf(Func<TResult, bool> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);

        _shouldCache = predicate;
        return this;
    }

    /// <summary>
    /// Sets the time to live for both layers of the cached value. Null uses the configured default,
    /// and zero or less disables the layer.
    /// </summary>
    /// <param name="inMemoryTimeToLive">In-memory layer time to live, or null for the default.</param>
    /// <param name="distributedTimeToLive">Distributed layer time to live, or null for the default.</param>
    public CacheBuilder<TResult, T> ExpireIn(TimeSpan? inMemoryTimeToLive, TimeSpan? distributedTimeToLive)
    {
        _inMemoryTimeToLive = inMemoryTimeToLive;
        _distributedTimeToLive = distributedTimeToLive;
        return this;
    }

    /// <summary>
    /// Sets the time to live for both layers of the cached value in seconds. Null uses the configured
    /// default, and zero or less disables the layer.
    /// </summary>
    /// <param name="inMemorySeconds">In-memory layer time to live in seconds, or null for the default.</param>
    /// <param name="distributedSeconds">Distributed layer time to live in seconds, or null for the default.</param>
    public CacheBuilder<TResult, T> ExpireIn(int? inMemorySeconds, int? distributedSeconds)
    {
        _inMemoryTimeToLive = inMemorySeconds is null ? null : TimeSpan.FromSeconds(inMemorySeconds.Value);
        _distributedTimeToLive = distributedSeconds is null ? null : TimeSpan.FromSeconds(distributedSeconds.Value);
        return this;
    }

    /// <summary>Gets the value, producing, mapping and caching it on a miss.</summary>
    /// <param name="key">The cache key.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    public ValueTask<T> GetAsync(string key, CancellationToken cancellationToken = default)
        => _cache.GetOrCreateAsync(key, _factory, _select, _shouldCache, _inMemoryTimeToLive, _distributedTimeToLive, cancellationToken);
}
