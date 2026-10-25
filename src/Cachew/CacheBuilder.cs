namespace Cachew;

/// <summary>Fluent entry points for <see cref="ICachew"/>.</summary>
public static class CachewExtensions
{
    /// <summary>
    /// Starts a fluent get-or-create operation. The factory is invoked on a cache miss.
    /// </summary>
    /// <typeparam name="T">The cached value type.</typeparam>
    /// <param name="cache">The cache.</param>
    /// <param name="factory">Produces the value on a miss.</param>
    public static CacheBuilder<T> OnCacheMiss<T>(this ICachew cache, Func<CancellationToken, Task<T>> factory)
    {
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(factory);

        return new CacheBuilder<T>(cache, factory);
    }
}

/// <summary>
/// Builds a get-or-create operation with a per call caching policy. A builder can be reused across
/// keys. It is not safe to use a builder from multiple threads while configuring it.
/// </summary>
/// <typeparam name="T">The cached value type.</typeparam>
public sealed class CacheBuilder<T>
{
    private readonly ICachew _cache;
    private readonly Func<CancellationToken, Task<T>> _factory;

    private Func<T, bool>? _shouldCache;
    private TimeSpan? _inMemoryTimeToLive;
    private TimeSpan? _distributedTimeToLive;

    internal CacheBuilder(ICachew cache, Func<CancellationToken, Task<T>> factory)
    {
        _cache = cache;
        _factory = factory;
    }

    /// <summary>
    /// Caches the produced value only when <paramref name="predicate"/> returns true. When unset,
    /// every value is cached.
    /// </summary>
    /// <param name="predicate">Decides whether the produced value is cached.</param>
    public CacheBuilder<T> CacheIf(Func<T, bool> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);

        _shouldCache = predicate;
        return this;
    }

    /// <summary>
    /// Sets the time to live for both layers. Null uses the configured default, and zero or less
    /// disables the layer.
    /// </summary>
    /// <param name="inMemoryTimeToLive">In-memory layer time to live, or null for the default.</param>
    /// <param name="distributedTimeToLive">Distributed layer time to live, or null for the default.</param>
    public CacheBuilder<T> ExpireIn(TimeSpan? inMemoryTimeToLive, TimeSpan? distributedTimeToLive)
    {
        _inMemoryTimeToLive = inMemoryTimeToLive;
        _distributedTimeToLive = distributedTimeToLive;
        return this;
    }

    /// <summary>
    /// Sets the time to live for both layers in seconds. Null uses the configured default, and zero
    /// or less disables the layer.
    /// </summary>
    /// <param name="inMemorySeconds">In-memory layer time to live in seconds, or null for the default.</param>
    /// <param name="distributedSeconds">Distributed layer time to live in seconds, or null for the default.</param>
    public CacheBuilder<T> ExpireIn(int? inMemorySeconds, int? distributedSeconds)
    {
        _inMemoryTimeToLive = inMemorySeconds is null ? null : TimeSpan.FromSeconds(inMemorySeconds.Value);
        _distributedTimeToLive = distributedSeconds is null ? null : TimeSpan.FromSeconds(distributedSeconds.Value);
        return this;
    }

    /// <summary>Gets the value, producing and caching it on a miss.</summary>
    /// <param name="key">The cache key.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    public ValueTask<T> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        var options = _shouldCache is null && _inMemoryTimeToLive is null && _distributedTimeToLive is null
            ? null
            : new CacheEntryOptions<T>
            {
                InMemoryTimeToLive = _inMemoryTimeToLive,
                DistributedTimeToLive = _distributedTimeToLive,
                ShouldCache = _shouldCache,
            };

        return _cache.GetOrCreateAsync(key, _factory, options, cancellationToken);
    }
}
