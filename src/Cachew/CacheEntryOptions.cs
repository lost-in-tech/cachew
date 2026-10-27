namespace Cachew;

/// <summary>
/// Per entry overrides. Unset members fall back to <see cref="CachewOptions"/> defaults. An override
/// can change the lifetime within an enabled layer, but it cannot switch on a layer that the configured
/// defaults turn off.
/// </summary>
/// <typeparam name="T">The cached value type.</typeparam>
public sealed class CacheEntryOptions<T>
{
    /// <summary>
    /// Time to live in the in-memory layer. Unset falls back to
    /// <see cref="CachewOptions.DefaultInMemoryTimeToLive"/>. Zero or negative means the in-memory layer
    /// is not used for this entry.
    /// </summary>
    public TimeSpan? InMemoryTimeToLive { get; set; }

    /// <summary>
    /// Time to live in the distributed layer. Unset falls back to
    /// <see cref="CachewOptions.DefaultDistributedTimeToLive"/>. Zero or negative means the distributed layer
    /// is not used for this entry.
    /// </summary>
    public TimeSpan? DistributedTimeToLive { get; set; }

    /// <summary>
    /// Optional predicate deciding whether a value produced by
    /// <see cref="ICachew.GetOrCreateAsync{T}"/> is cached. When null, every value is cached.
    /// </summary>
    public Func<T, bool>? ShouldCache { get; set; }
}
