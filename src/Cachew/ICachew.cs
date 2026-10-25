namespace Cachew;

/// <summary>
/// A two layer cache that reads from an in-memory layer first and falls back to a
/// distributed layer. Writes populate both layers.
/// </summary>
public interface ICachew
{
    /// <summary>Gets a value, checking the in-memory layer before the distributed layer.</summary>
    /// <param name="key">The cache key.</param>
    /// <param name="options">Per entry overrides. Unset members fall back to the configured defaults.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    Task<T?> GetAsync<T>(string key, CacheEntryOptions<T>? options = null, CancellationToken cancellationToken = default);

    /// <summary>Writes a value to both layers.</summary>
    /// <param name="key">The cache key.</param>
    /// <param name="value">The value to cache.</param>
    /// <param name="options">Per entry overrides. Unset members fall back to the configured defaults.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    Task SetAsync<T>(string key, T value, CacheEntryOptions<T>? options = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a value, invoking <paramref name="factory"/> to produce and cache it on a miss.
    /// Concurrent misses for the same key within one process share a single <paramref name="factory"/>
    /// invocation; this does not coalesce across processes. The coalesced callers share the first
    /// caller's <paramref name="options"/>. The factory and the write are not run under any caller's
    /// token, so a caller that cancels stops waiting without abandoning the work the others are
    /// waiting on. The produced value is cached only when
    /// <see cref="CacheEntryOptions{T}.ShouldCache"/> is unset or returns true.
    /// </summary>
    /// <param name="key">The cache key.</param>
    /// <param name="factory">Produces the value on a miss.</param>
    /// <param name="options">Per entry overrides. Unset members fall back to the configured defaults.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    Task<T> GetOrCreateAsync<T>(string key, Func<CancellationToken, Task<T>> factory, CacheEntryOptions<T>? options = null, CancellationToken cancellationToken = default);

    /// <summary>Removes a value from both layers.</summary>
    /// <param name="key">The cache key.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    Task RemoveAsync(string key, CancellationToken cancellationToken = default);
}
