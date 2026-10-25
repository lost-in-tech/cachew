# cachew

Two layer caching for .NET: an in-memory layer in front of a distributed layer.

Reads hit memory first and fall back to the distributed layer, repopulating memory. Writes populate
both layers.

## Status

Early. The API is a first pass and may change.

## Install

```bash
dotnet add package LostInTech.Cachew
```

For the Redis provider:

```bash
dotnet add package LostInTech.Cachew.Redis
```

The package IDs carry the `LostInTech` prefix because the ID `Cachew` belongs to an unrelated
library on nuget.org. The namespaces are still `Cachew` and `Cachew.Redis`.

## Usage

```csharp
using Cachew;

builder.Services.AddStackExchangeRedisCache(o => o.Configuration = "localhost:6379");
builder.Services.AddCachew(o =>
{
    o.KeyPrefix = "myapp:";
    o.DefaultInMemoryTimeToLive = TimeSpan.FromMinutes(1);
    o.DefaultDistributedTimeToLive = TimeSpan.FromMinutes(10);
});
```

```csharp
public sealed class Products(ICachew cache)
{
    public Task<Product?> GetAsync(int id, CancellationToken ct)
        => cache.GetOrCreateAsync(
            $"product:{id}",
            async token => await LoadFromDatabaseAsync(id, token),
            new CacheEntryOptions<Product> { InMemoryTimeToLive = TimeSpan.FromSeconds(30) },
            ct);
}
```

`AddCachew` registers the memory layer and the cache itself. Register an `IDistributedCache`
(Redis, or `MemoryDistributedCache` for a single process) yourself.

`GetOrCreateAsync` coalesces concurrent misses for the same key within one process, so a burst of
callers runs the factory once. It does not coalesce across processes. The group shares the first
caller's `CacheEntryOptions<T>`, so concurrent calls for one key should agree on the policy. The
factory and the write are not run under any single caller's token: a caller that cancels stops
waiting, but the work it started carries on for the others and still populates the cache.

## Layers and lifetimes

Each layer is configured by a time to live. `null` (or unset) means "use the configured default",
and a time to live of zero or less means "do not use this layer".

```csharp
// Application defaults. Zero here turns a layer off for the whole app.
o.DefaultInMemoryTimeToLive = TimeSpan.FromMinutes(1);
o.DefaultDistributedTimeToLive = TimeSpan.Zero; // no distributed layer

// Per entry overrides.
new CacheEntryOptions<Product>
{
    InMemoryTimeToLive = TimeSpan.Zero,              // skip the in-memory layer for this entry
    DistributedTimeToLive = TimeSpan.FromMinutes(10),   // override the distributed lifetime
    ShouldCache = product => product is not null, // cache only when this returns true
}
```

`ShouldCache` runs on the value the factory produced. When it returns false the value is returned
but not cached. Pass the same `CacheEntryOptions<T>` to `GetAsync`, `SetAsync` and
`GetOrCreateAsync` to keep a key's policy consistent; a value promoted from the distributed layer
into memory expires after the effective in-memory time to live.

`AddCachew` registers an unbounded memory cache and sizes entries as one, so registering your own
`IMemoryCache` with a `SizeLimit` before `AddCachew` counts entries rather than bytes. A null
`KeyPrefix` or `SerializerOptions` fails at construction rather than silently misbehaving. Options
are read once at startup, so changing them at runtime has no effect.

## Turning the cache off

`CachewOptions.Enabled` defaults to `true`. Setting it to `false` turns the cache off: nothing is
stored, nothing is returned, and the instance takes no part in cross instance invalidation. Concurrent
misses for a key are still coalesced, so a burst makes one call to the factory, which is the case where
that matters most. It wins over per entry overrides, so an `InMemoryTimeToLive` on an entry cannot
re-enable caching that the configuration turned off.

Because the options bind from configuration, an operator can turn the cache off without a deployment:

```csharp
builder.Services.Configure<CachewOptions>(builder.Configuration.GetSection("Cachew"));
builder.Services.AddCachew();
```

```json
{
  "Cachew": {
    "Enabled": false,
    "KeyPrefix": "myapp:",
    "DefaultInMemoryTimeToLive": "00:01:00",
    "DefaultDistributedTimeToLive": "00:10:00"
  }
}
```

It is read at startup, so it needs a restart to take effect.

## Distributed layer failures

The distributed layer is best effort. If it throws, a read behaves as a miss and a write or a
remove becomes a no-op, so an outage does not become an application error. The in-memory layer is
still populated and still cleared. Cancellation is the exception, since an
`OperationCanceledException` always propagates. Serialization happens outside that guard, so a value
that cannot be serialized still throws.

Because remove degrades as well, invalidation is best effort: if the remove fails, other processes
can serve a stale value until it expires.

## Invalidation across instances

Each instance has its own in-memory layer, so by default a change on one instance is invisible to the
others until their copies expire, which is up to their in-memory time to live, one minute by default.
`Cachew.Redis` closes that gap with a backplane: invalidating a key publishes it over Redis pub/sub and
every other instance drops its own copy straight away.

```csharp
builder.Services.AddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect("localhost:6379"));
builder.Services.AddCachewRedis();   // uses the registered multiplexer
builder.Services.AddCachew();
builder.Services.WithBackplane();    // optional: enable cross instance invalidation
```

Notifications are published when a value is cached and when a key is removed, because both change what
the other instances are holding. They are best effort, on the same terms as everything else here:

- An instance that misses a notification, or that was restarting at the time, keeps its stale copy
  until that copy expires. The worst case is therefore the behaviour without a backplane at all; the
  normal case is that the other instances are correct immediately. Messages are not replayed, so an
  instance only sees what is published after it subscribes.
- A notification can arrive before the write that caused it has reached Redis, because writes are
  fire-and-forget. The instance that receives it may then read the previous value and cache that
  again, so an invalidation can extend staleness rather than end it. Same bound: the in-memory time to
  live.
- Evicting a key that is not there does nothing, so sharing a channel with an unrelated application is
  harmless. Instances that must see each other have to pass the same `channel` to `WithBackplane`.

If none of that is good enough for particular data, keep `DefaultInMemoryTimeToLive` short for values
that change often, or bypass the in-memory layer for a key by passing
`InMemoryTimeToLive = TimeSpan.Zero` on both the write and the read, so every read goes to the
distributed layer.

## Evolving cached values

The library does not version your payloads, you do, in the key:

```csharp
var product = await cache.GetAsync<Product>($"product:{id}:v1", ct);
```

Bump the suffix when you change the shape of what you cache. Nothing then needs invalidating, the
old keyspace ages out on its own, and rolling back just means reading `v1` again.
`CachewOptions.KeyPrefix` is the same idea for the whole application, so a global change is a
configuration edit rather than a code change. One key means one type: if a value's type changes,
change the key with it.

The payload is JSON written with `CachewOptions.SerializerOptions`. It starts from the web defaults
(camel case property names, case insensitive reads, numbers readable from strings) plus two choices:
nulls are not written, and enums are written as their names rather than numbers, so reordering enum
members cannot silently reinterpret an entry that is already cached. Reads remain compatible with
payloads written before those two choices, because a missing property deserializes to its default
and enum numbers are still accepted. Changing the serializer changes the format, so treat that like
any other payload change and bump the key.

If a payload does not fit the type being read, deserialization fails. That entry is treated as a
cache miss and a warning is logged through `ILogger<TwoLayerCache>` when logging is registered, so
the caller still gets a value from the factory and the next write replaces the entry with the
current shape. The cause is still worth chasing: the key holds either a forgotten version bump or
the wrong type. A payload with no expiry, or a literal JSON null, is a miss as well, which is the
hook the library will use if the envelope format itself ever changes. Worth being explicit about the
residual gap: a conflicting type whose JSON shape happens to match cannot be detected at all,
because the bytes are valid for both types. That is why one key means one type is a convention and
not a runtime guarantee.

## Fluent style

The same policy can be built fluently:

```csharp
var product = await cache
    .OnCacheMiss(ct => LoadFromDatabaseAsync(id, ct))
    .CacheIf(product => product is not null)
    .Duration(inMemorySeconds: 30, distributedSeconds: 300)
    .GetAsync($"product:{id}", ct);
```

`OnCacheMiss` starts the operation, `CacheIf` is optional, and `Duration` takes `TimeSpan` or
`int` seconds with the same null-means-default and zero-means-disabled rules as
`CacheEntryOptions<T>`. The terminal `GetAsync` runs the same coalesced get-or-create. A builder can
be reused across keys, since configuring it does not consume it.

## Redis

`Cachew.Redis` provides a Redis backed `IDistributedCache` whose writes are sent with
`CommandFlags.FireAndForget`: the command is handed to the connection as soon as the method
returns, and the caller does not wait for the write round trip.

```csharp
builder.Services.AddCachewRedis("localhost:6379");
builder.Services.AddCachew(o => { /* ... */ });
```

`AddCachewRedis` registers the `IDistributedCache` with `TryAdd`, so a cache you register yourself
(for example `AddStackExchangeRedisCache`) wins if registered first. The connection can be supplied
in four ways:

```csharp
builder.Services.AddCachewRedis();                                        // ConnectionStrings:Cachew
builder.Services.AddCachewRedis("localhost:6379");                        // connection string
builder.Services.AddCachewRedis(cfg => cfg.GetConnectionString("redis")); // from configuration
builder.Services.AddCachewRedis(sp => myConnection);                      // share a connection
```

The parameterless overload reads `ConnectionStrings:Cachew` (see
`ServiceCollectionExtensions.DefaultConnectionStringName`); an `IConnectionMultiplexer` that is
already registered is used instead. The configuration overload is given the `IConfiguration` from
the container, so the connection string can be read from any section. `WithBackplane` adds cross
instance invalidation over the same connection; see
[Invalidation across instances](#invalidation-across-instances). The multiplexer is registered
as a singleton with `TryAdd`, so the first registration wins.

Write semantics:

- Writes (`Set`, `SetAsync`, `Remove`, `RemoveAsync`) and invalidation notifications are
  fire-and-forget by default. They are enqueued when the method returns, but the server reply is not
  awaited and a write error is not surfaced. Reads are always awaited.
- A caller that writes and immediately reads the same key from another process may briefly see the
  previous value or a miss, because the write may not have reached Redis yet.
- Sliding expiration is not supported and throws `NotSupportedException`. `Refresh` is a no-op.
- Cancellation tokens are not honoured, because StackExchange.Redis does not accept them.

### Waiting for Redis instead

`RedisCachewOptions.FireAndForget` defaults to `true`. On a cache miss the sequence is read, run the
factory, then write, so fire-and-forget removes the wait on that last step. Setting it to `false`
makes writes, removes and invalidations wait for Redis instead. That costs a round trip each time and
buys observability: failures surface through the cache's logger rather than only meaning that another
instance stays stale until an entry expires.

```csharp
builder.Services.AddCachewRedis("localhost:6379");
builder.Services.Configure<RedisCachewOptions>(o => o.FireAndForget = false);
```

Because it binds from configuration, it can be turned on without a release:

```csharp
builder.Services.Configure<RedisCachewOptions>(builder.Configuration.GetSection("Cachew:Redis"));
```

```json
{ "Cachew": { "Redis": { "FireAndForget": false } } }
```

## Build

```bash
dotnet build cachew.slnx --configuration Release
dotnet test cachew.slnx --configuration Release
dotnet pack cachew.slnx --configuration Release
```

## Releasing

Push a `v*` tag. The `release` workflow builds, tests, packs, publishes to NuGet.org and creates a
GitHub release with the packages attached.

Publishing uses NuGet trusted publishing, so no long lived NuGet API key is stored. The workflow
exchanges a GitHub OIDC token for an API key that lives for one hour. Two things are needed:
a `NUGET_USER` secret holding your nuget.org profile name (not your email), and a trusted publishing
policy on nuget.org matching repository owner `lost-in-tech`, repository `cachew` and workflow file
`release.yml`. The workflow fails before publishing if `NUGET_USER` is missing rather than going green
with nothing published.

## License

MIT
