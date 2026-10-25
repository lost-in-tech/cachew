namespace Cachew;

/// <summary>
/// A backplane that does nothing, used when none is registered, so that the cache itself needs no
/// null checks.
/// </summary>
/// <remarks>
/// This is deliberately not registered in the container. Registrations here use <c>TryAdd</c>, so a
/// no-op added by <c>AddCachew</c> would win over a later <c>WithBackplane</c> call and silently
/// disable cross instance invalidation. Falling back to it from the constructor has the same effect
/// on the cache code with none of that risk.
/// </remarks>
internal sealed class NullCacheBackplane : ICachewBackplane
{
    public static NullCacheBackplane Instance { get; } = new();

    private NullCacheBackplane()
    {
    }

    public void Publish(string key)
    {
    }

    public IDisposable Subscribe(Action<string> onInvalidate)
    {
        ArgumentNullException.ThrowIfNull(onInvalidate);
        return NoSubscription.Instance;
    }

    private sealed class NoSubscription : IDisposable
    {
        public static NoSubscription Instance { get; } = new();

        public void Dispose()
        {
        }
    }
}
