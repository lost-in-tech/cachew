namespace Cachew;

/// <summary>
/// Per entry overrides. Unset members fall back to <see cref="CachewOptions"/> defaults.
/// </summary>
/// <typeparam name="T">The cached value type.</typeparam>
public sealed class CacheEntryOptions<T>
{
    /// <summary>
    /// Time to live in the in-memory layer (L1). Unset falls back to
    /// <see cref="CachewOptions.DefaultL1TimeToLive"/>. Zero or negative means the in-memory layer
    /// is not used for this entry.
    /// </summary>
    public TimeSpan? L1TimeToLive { get; set; }

    /// <summary>
    /// Time to live in the distributed layer (L2). Unset falls back to
    /// <see cref="CachewOptions.DefaultL2TimeToLive"/>. Zero or negative means the distributed layer
    /// is not used for this entry.
    /// </summary>
    public TimeSpan? L2TimeToLive { get; set; }

    /// <summary>
    /// Optional predicate deciding whether a value produced by
    /// <see cref="ICachew.GetOrCreateAsync{T}"/> is cached. When null, every value is cached.
    /// </summary>
    public Func<T, bool>? ShouldCache { get; set; }
}
