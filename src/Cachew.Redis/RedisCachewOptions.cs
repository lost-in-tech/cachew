namespace Cachew.Redis;

/// <summary>Options for the Redis backed cache and its backplane.</summary>
public sealed class RedisCachewOptions
{
    /// <summary>
    /// Whether Redis mutations are sent without waiting for the reply. Defaults to <c>true</c>.
    /// </summary>
    /// <remarks>
    /// On a cache miss the sequence is read, run the factory, then write, so this removes the wait on
    /// that last step. Writes, removes and invalidation notifications are all covered. Turning it off
    /// costs a round trip each time and makes a failure visible instead of silent. Reads always wait,
    /// because their value is the point of the call.
    /// </remarks>
    public bool FireAndForget { get; set; } = true;
}
