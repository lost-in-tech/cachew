using Microsoft.Extensions.Caching.Distributed;

namespace Cachew.Tests;

/// <summary>
/// An <see cref="IDistributedCache"/> that fails every operation, to exercise how the cache behaves
/// when the distributed layer is unavailable.
/// </summary>
internal sealed class FailingDistributedCache(Exception failure) : IDistributedCache
{
    public byte[]? Get(string key) => throw failure;

    public Task<byte[]?> GetAsync(string key, CancellationToken token = default) => throw failure;

    public void Set(string key, byte[] value, DistributedCacheEntryOptions options) => throw failure;

    public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default) => throw failure;

    public void Refresh(string key) => throw failure;

    public Task RefreshAsync(string key, CancellationToken token = default) => throw failure;

    public void Remove(string key) => throw failure;

    public Task RemoveAsync(string key, CancellationToken token = default) => throw failure;
}

internal sealed class CacheUnavailableException : Exception
{
    public CacheUnavailableException()
        : base("The distributed cache is unavailable.")
    {
    }
}
