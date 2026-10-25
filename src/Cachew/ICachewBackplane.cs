namespace Cachew;

/// <summary>
/// Notifies the other instances of an application that a key changed, and reports what they
/// published. A provider package implements this, for example <c>Cachew.Redis</c>, which carries the
/// notifications over Redis pub/sub.
/// </summary>
/// <remarks>
/// Notifications are best effort. Losing one costs nothing more than the in-memory layer of the
/// instance that missed it staying stale until that entry expires, which is the behaviour without a
/// backplane at all.
/// </remarks>
public interface ICachewBackplane
{
    /// <summary>
    /// Publishes that <paramref name="key"/> changed, so that other instances drop their in-memory
    /// copy of it. Must not throw: a failure only means an instance keeps a stale copy until it
    /// expires, and the caller is not expected to handle it. A provider that sends fire-and-forget
    /// can return a completed value.
    /// </summary>
    /// <param name="key">The cache key, already prefixed.</param>
    ValueTask PublishAsync(string key);

    /// <summary>
    /// Registers <paramref name="onInvalidate"/>, invoked with a key that another instance
    /// invalidated. The returned value unregisters it.
    /// </summary>
    /// <param name="onInvalidate">Receives an invalidated cache key, already prefixed.</param>
    IDisposable Subscribe(Action<string> onInvalidate);
}
