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
    private TimeSpan? _l1TimeToLive;
    private TimeSpan? _l2TimeToLive;

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
    /// <param name="l1TimeToLive">In-memory layer time to live, or null for the default.</param>
    /// <param name="l2TimeToLive">Distributed layer time to live, or null for the default.</param>
    public CacheBuilder<T> Duration(TimeSpan? l1TimeToLive, TimeSpan? l2TimeToLive)
    {
        _l1TimeToLive = l1TimeToLive;
        _l2TimeToLive = l2TimeToLive;
        return this;
    }

    /// <summary>
    /// Sets the time to live for both layers in seconds. Null uses the configured default, and zero
    /// or less disables the layer.
    /// </summary>
    /// <param name="l1Seconds">In-memory layer time to live in seconds, or null for the default.</param>
    /// <param name="l2Seconds">Distributed layer time to live in seconds, or null for the default.</param>
    public CacheBuilder<T> Duration(int? l1Seconds, int? l2Seconds)
    {
        _l1TimeToLive = l1Seconds is null ? null : TimeSpan.FromSeconds(l1Seconds.Value);
        _l2TimeToLive = l2Seconds is null ? null : TimeSpan.FromSeconds(l2Seconds.Value);
        return this;
    }

    /// <summary>Gets the value, producing and caching it on a miss.</summary>
    /// <param name="key">The cache key.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    public Task<T> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        var options = _shouldCache is null && _l1TimeToLive is null && _l2TimeToLive is null
            ? null
            : new CacheEntryOptions<T>
            {
                L1TimeToLive = _l1TimeToLive,
                L2TimeToLive = _l2TimeToLive,
                ShouldCache = _shouldCache,
            };

        return _cache.GetOrCreateAsync(key, _factory, options, cancellationToken);
    }
}
