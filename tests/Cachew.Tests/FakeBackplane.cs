namespace Cachew.Tests;

/// <summary>
/// A backplane that records what was published and lets a test raise an invalidation as if another
/// instance had published it.
/// </summary>
internal sealed class FakeBackplane : ICachewBackplane
{
    private readonly List<Action<string>> _handlers = [];
    private readonly Exception? _publishFailure;

    public FakeBackplane(Exception? publishFailure = null) => _publishFailure = publishFailure;

    public List<string> Published { get; } = [];

    public bool IsSubscribed => _handlers.Count > 0;

    public void Publish(string key)
    {
        if (_publishFailure is not null)
        {
            throw _publishFailure;
        }

        Published.Add(key);
    }

    public IDisposable Subscribe(Action<string> onInvalidate)
    {
        _handlers.Add(onInvalidate);
        return new Subscription(() => _handlers.Remove(onInvalidate));
    }

    public void Raise(string key)
    {
        foreach (var handler in _handlers.ToArray())
        {
            handler(key);
        }
    }

    private sealed class Subscription(Action unsubscribe) : IDisposable
    {
        public void Dispose() => unsubscribe();
    }
}
