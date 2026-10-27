using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Cachew.Tests;

public class TwoLayerCacheTests
{
    private static readonly CachewOptions DefaultOptions = new();

    [Fact]
    public async Task A_never_cached_key_returns_nothing()
    {
        var cache = CreateCache(out _);

        var value = await cache.GetAsync<string>("missing");

        Assert.Null(value);
    }

    [Fact]
    public async Task A_stored_value_can_be_read_back()
    {
        var cache = CreateCache(out _);

        await cache.SetAsync("key", "value");

        Assert.Equal("value", await cache.GetAsync<string>("key"));
    }

    [Fact]
    public async Task A_value_written_by_another_instance_is_read_from_the_distributed_layer()
    {
        var distributed = CreateDistributedCache();
        var writer = CreateCache(distributed);
        var reader = CreateCache(distributed);

        await writer.SetAsync("key", new Payload(42, "answer"));
        var value = await reader.GetAsync<Payload>("key");

        Assert.Equal(new Payload(42, "answer"), value);
    }

    [Fact]
    public async Task A_distributed_hit_is_promoted_into_the_memory_layer()
    {
        var distributed = CreateDistributedCache();
        var writer = CreateCache(distributed);
        var reader = CreateCache(distributed);

        await writer.SetAsync("key", "value");
        Assert.Equal("value", await reader.GetAsync<string>("key"));

        // Remove from the distributed layer only. The value must now be served from memory.
        await distributed.RemoveAsync(DefaultOptions.KeyPrefix + "key");
        Assert.Equal("value", await reader.GetAsync<string>("key"));
    }

    [Fact]
    public async Task The_factory_runs_only_on_a_miss()
    {
        var cache = CreateCache(out _);
        var calls = 0;

        var first = await cache.GetOrCreateAsync("key", _ => Task.FromResult(++calls));
        var second = await cache.GetOrCreateAsync("key", _ => Task.FromResult(++calls));

        Assert.Equal(1, first);
        Assert.Equal(1, second);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Removing_a_key_clears_both_layers()
    {
        var distributed = CreateDistributedCache();
        var cache = CreateCache(distributed);

        await cache.SetAsync("key", "value");
        await cache.RemoveAsync("key");

        Assert.Null(await cache.GetAsync<string>("key"));
        Assert.Null(await distributed.GetAsync(DefaultOptions.KeyPrefix + "key"));
    }

    [Fact]
    public async Task A_configured_prefix_namespaces_the_stored_keys()
    {
        var distributed = CreateDistributedCache();
        var cache = CreateCache(distributed);

        await cache.SetAsync("key", "value");

        Assert.NotNull(await distributed.GetAsync("cachew:key"));
        Assert.Null(await distributed.GetAsync("key"));
    }

    [Fact]
    public async Task Concurrent_misses_for_one_key_run_the_factory_once()
    {
        var cache = CreateCache(out _);
        var calls = 0;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<int> Factory(CancellationToken _)
        {
            Interlocked.Increment(ref calls);
            await gate.Task;
            return 42;
        }

        var callers = Enumerable.Range(0, 10)
            .Select(_ => cache.GetOrCreateAsync("key", Factory).AsTask())
            .ToArray();

        // Let every caller reach the coalescing point before the factory completes.
        await Task.Delay(100);
        Assert.Equal(1, calls);

        gate.SetResult();
        var results = await Task.WhenAll(callers);

        Assert.All(results, value => Assert.Equal(42, value));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task A_key_read_as_two_types_still_produces_a_value_for_each()
    {
        var cache = CreateCache(out _);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<string> TextFactory(CancellationToken _)
        {
            await gate.Task;
            return "text";
        }

        async Task<int> NumberFactory(CancellationToken _)
        {
            await gate.Task;
            return 42;
        }

        var text = cache.GetOrCreateAsync("key", TextFactory).AsTask();
        await Task.Delay(50);
        var number = cache.GetOrCreateAsync("key", NumberFactory).AsTask();

        gate.SetResult();

        // One key as two types is a misuse, but each caller still gets the value its own factory
        // produced rather than a cast failure.
        Assert.Equal("text", await text);
        Assert.Equal(42, await number);
    }

    [Fact]
    public async Task A_failing_factory_propagates_the_failure_and_the_next_call_retries()
    {
        var cache = CreateCache(out _);
        var calls = 0;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<int> Factory(CancellationToken _)
        {
            Interlocked.Increment(ref calls);
            await gate.Task;
            throw new InvalidOperationException("boom");
        }

        var first = cache.GetOrCreateAsync("key", Factory).AsTask();
        var second = cache.GetOrCreateAsync("key", Factory).AsTask();

        await Task.Delay(100);
        gate.SetResult();

        await Assert.ThrowsAsync<InvalidOperationException>(() => first);
        await Assert.ThrowsAsync<InvalidOperationException>(() => second);
        Assert.Equal(1, calls);

        // A failed attempt is not cached, so a later call retries.
        Assert.Equal(7, await cache.GetOrCreateAsync("key", _ => Task.FromResult(7)));
    }

    [Fact]
    public async Task A_zero_in_memory_lifetime_keeps_the_value_out_of_memory()
    {
        var cache = CreateCache(DefaultOptions, out var distributed, out var memory);

        await cache.SetAsync("key", "value", new CacheEntryOptions<string> { InMemoryTimeToLive = TimeSpan.Zero });

        Assert.False(memory.TryGetValue(DefaultOptions.KeyPrefix + "key", out _));
        Assert.NotNull(await distributed.GetAsync(DefaultOptions.KeyPrefix + "key"));
    }

    [Fact]
    public async Task A_zero_distributed_lifetime_keeps_the_value_out_of_the_distributed_layer()
    {
        var cache = CreateCache(DefaultOptions, out var distributed, out var memory);

        await cache.SetAsync("key", "value", new CacheEntryOptions<string> { DistributedTimeToLive = TimeSpan.Zero });

        Assert.Null(await distributed.GetAsync(DefaultOptions.KeyPrefix + "key"));
        Assert.True(memory.TryGetValue(DefaultOptions.KeyPrefix + "key", out _));
    }

    [Fact]
    public async Task Two_zero_lifetimes_cache_nothing()
    {
        var cache = CreateCache(DefaultOptions, out var distributed, out var memory);

        await cache.SetAsync("key", "value", new CacheEntryOptions<string>
        {
            InMemoryTimeToLive = TimeSpan.Zero,
            DistributedTimeToLive = TimeSpan.Zero,
        });

        Assert.Null(await distributed.GetAsync(DefaultOptions.KeyPrefix + "key"));
        Assert.False(memory.TryGetValue(DefaultOptions.KeyPrefix + "key", out _));
        Assert.Null(await cache.GetAsync<string>("key"));
    }

    [Fact]
    public async Task A_value_rejected_by_ShouldCache_is_not_cached()
    {
        var cache = CreateCache(out _);
        var calls = 0;
        var options = new CacheEntryOptions<int> { ShouldCache = value => value > 100 };

        var first = await cache.GetOrCreateAsync("key", _ => Task.FromResult(++calls), options);
        var second = await cache.GetOrCreateAsync("key", _ => Task.FromResult(++calls), options);

        Assert.Equal(1, first);
        Assert.Equal(2, second);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task A_value_accepted_by_ShouldCache_is_cached()
    {
        var cache = CreateCache(out _);
        var calls = 0;
        var options = new CacheEntryOptions<int> { ShouldCache = value => value > 0 };

        var first = await cache.GetOrCreateAsync("key", _ => Task.FromResult(++calls), options);
        var second = await cache.GetOrCreateAsync("key", _ => Task.FromResult(++calls), options);

        Assert.Equal(1, first);
        Assert.Equal(1, second);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task A_disabled_memory_layer_is_not_populated_by_a_distributed_read()
    {
        var options = new CachewOptions { DefaultInMemoryTimeToLive = TimeSpan.Zero };
        var distributed = CreateDistributedCache();
        var writer = CreateCache(distributed);
        var memory = new MemoryCache(new MemoryCacheOptions());
        var reader = new TwoLayerCache(memory, distributed, Options.Create(options));

        await writer.SetAsync("key", "value");

        Assert.Equal("value", await reader.GetAsync<string>("key"));
        Assert.False(memory.TryGetValue(options.KeyPrefix + "key", out _));
    }

    [Fact]
    public async Task A_disabled_distributed_layer_is_never_read()
    {
        var options = new CachewOptions { DefaultDistributedTimeToLive = TimeSpan.Zero };
        var distributed = CreateDistributedCache();
        var writer = CreateCache(distributed);
        var reader = new TwoLayerCache(new MemoryCache(new MemoryCacheOptions()), distributed, Options.Create(options));

        await writer.SetAsync("key", "value");

        Assert.Null(await reader.GetAsync<string>("key"));
    }

    [Fact]
    public async Task A_promoted_entry_expires_with_the_in_memory_lifetime()
    {
        var options = new CachewOptions { DefaultInMemoryTimeToLive = TimeSpan.FromMilliseconds(100) };
        var distributed = CreateDistributedCache();
        var writer = CreateCache(distributed);
        var memory = new MemoryCache(new MemoryCacheOptions());
        var reader = new TwoLayerCache(memory, distributed, Options.Create(options));

        await writer.SetAsync("key", "value");

        // First read promotes the value into memory for the configured in-memory time to live.
        Assert.Equal("value", await reader.GetAsync<string>("key"));
        Assert.True(memory.TryGetValue(options.KeyPrefix + "key", out _));

        await Task.Delay(500);

        Assert.False(memory.TryGetValue(options.KeyPrefix + "key", out _));
        Assert.Equal("value", await reader.GetAsync<string>("key"));
    }

    [Fact]
    public async Task A_disabled_distributed_layer_is_not_touched_by_a_remove()
    {
        var options = new CachewOptions { DefaultDistributedTimeToLive = TimeSpan.Zero };
        var distributed = CreateDistributedCache();
        await distributed.SetAsync(
            options.KeyPrefix + "key",
            [1, 2, 3],
            new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5) });
        var cache = new TwoLayerCache(new MemoryCache(new MemoryCacheOptions()), distributed, Options.Create(options));

        await cache.RemoveAsync("key");

        Assert.NotNull(await distributed.GetAsync(options.KeyPrefix + "key"));
    }

    [Fact]
    public async Task Cancelling_the_caller_that_started_the_work_does_not_abandon_it()
    {
        var cache = CreateCache(out _);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var leader = new CancellationTokenSource();

        async Task<int> Factory(CancellationToken ct)
        {
            await gate.Task.WaitAsync(ct);
            return 42;
        }

        var first = cache.GetOrCreateAsync("key", Factory, null, leader.Token).AsTask();
        await Task.Delay(50);
        var second = cache.GetOrCreateAsync("key", Factory, null, CancellationToken.None);

        leader.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);

        gate.SetResult();

        Assert.Equal(42, await second);
        Assert.Equal(42, await cache.GetAsync<int>("key"));
    }

    [Fact]
    public async Task A_payload_without_an_expiry_is_a_miss()
    {
        var distributed = CreateDistributedCache();
        await distributed.SetAsync(
            DefaultOptions.KeyPrefix + "key",
            Encoding.UTF8.GetBytes("{\"value\":\"value\",\"expiresAtUtc\":null}"),
            new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5) });
        var cache = CreateCache(distributed);

        Assert.Null(await cache.GetAsync<string>("key"));
    }

    [Fact]
    public async Task A_failing_distributed_layer_turns_a_read_into_a_miss()
    {
        var cache = CreateCache(new FailingDistributedCache(new CacheUnavailableException()));

        Assert.Null(await cache.GetAsync<string>("key"));
    }

    [Fact]
    public async Task A_failing_distributed_layer_still_uses_the_memory_layer()
    {
        var cache = CreateCache(new FailingDistributedCache(new CacheUnavailableException()));
        var calls = 0;

        var first = await cache.GetOrCreateAsync("key", _ => Task.FromResult(++calls));
        var second = await cache.GetOrCreateAsync("key", _ => Task.FromResult(++calls));

        Assert.Equal(1, first);
        Assert.Equal(1, second);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task A_failing_distributed_layer_does_not_stop_an_in_memory_write()
    {
        var cache = CreateCache(new FailingDistributedCache(new CacheUnavailableException()));

        await cache.SetAsync("key", "value");

        Assert.Equal("value", await cache.GetAsync<string>("key"));
    }

    [Fact]
    public async Task A_failing_distributed_layer_does_not_stop_a_memory_remove()
    {
        var cache = CreateCache(new FailingDistributedCache(new CacheUnavailableException()));

        await cache.SetAsync("key", "value");
        Assert.Equal("value", await cache.GetAsync<string>("key"));

        await cache.RemoveAsync("key");

        Assert.Null(await cache.GetAsync<string>("key"));
    }

    [Fact]
    public async Task Cancellation_from_the_distributed_layer_is_propagated()
    {
        var cache = CreateCache(new FailingDistributedCache(new OperationCanceledException()));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cache.GetAsync<string>("key").AsTask());
    }

    [Fact]
    public async Task A_size_limited_memory_cache_still_serves_values()
    {
        var memory = new MemoryCache(new MemoryCacheOptions { SizeLimit = 10 });
        var cache = new TwoLayerCache(memory, CreateDistributedCache(), Options.Create(DefaultOptions));

        await cache.SetAsync("key", "value");

        Assert.Equal("value", await cache.GetAsync<string>("key"));
    }

    [Fact]
    public async Task An_expired_payload_is_a_miss()
    {
        var expiredAt = DateTimeOffset.UtcNow.AddMinutes(-1).ToString("O");
        var distributed = CreateDistributedCache();
        await distributed.SetAsync(
            DefaultOptions.KeyPrefix + "key",
            Encoding.UTF8.GetBytes($"{{\"value\":\"value\",\"expiresAtUtc\":\"{expiredAt}\"}}"),
            new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5) });
        var cache = CreateCache(distributed);

        Assert.Null(await cache.GetAsync<string>("key"));
    }

    [Fact]
    public async Task A_cancelled_factory_reports_cancellation()
    {
        var cache = CreateCache(out _);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // Pins the contract that a cancelled factory surfaces as cancellation rather than as some
        // other failure. It cannot distinguish the internal cancellation state, because the async
        // state machine normalises an OperationCanceledException to a cancelled task either way.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => cache.GetOrCreateAsync("key", _ => Task.FromCanceled<int>(cts.Token)).AsTask());
    }

    [Fact]
    public async Task A_literal_json_null_payload_is_a_miss()
    {
        var distributed = CreateDistributedCache();
        await distributed.SetAsync(
            DefaultOptions.KeyPrefix + "key",
            Encoding.UTF8.GetBytes("null"),
            new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5) });
        var cache = CreateCache(distributed);

        Assert.Null(await cache.GetAsync<string>("key"));
    }

    [Fact]
    public async Task A_promoted_entry_cannot_outlive_the_distributed_entry_it_came_from()
    {
        var options = new CachewOptions { DefaultInMemoryTimeToLive = TimeSpan.FromSeconds(30) };
        var distributed = CreateDistributedCache();
        var writer = CreateCache(distributed);
        var memory = new MemoryCache(new MemoryCacheOptions());
        var reader = new TwoLayerCache(memory, distributed, Options.Create(options));

        await writer.SetAsync("key", "value", new CacheEntryOptions<string>
        {
            DistributedTimeToLive = TimeSpan.FromMilliseconds(500),
        });

        // The reader's memory layer is empty, so this read promotes from the distributed layer. The
        // distributed entry expires well before the 30 second in-memory time to live, so the promoted
        // entry must be capped at the shorter of the two.
        Assert.Equal("value", await reader.GetAsync<string>("key"));
        Assert.True(memory.TryGetValue(options.KeyPrefix + "key", out _));

        await Task.Delay(1000);

        Assert.False(memory.TryGetValue(options.KeyPrefix + "key", out _));
    }

    [Fact]
    public async Task A_cached_null_is_a_hit_that_skips_the_factory()
    {
        var distributed = CreateDistributedCache();
        var writer = CreateCache(distributed);

        // A separate reader so its memory layer is empty and the null has to come back from the
        // distributed layer.
        var reader = CreateCache(distributed);
        await writer.SetAsync<string?>("key", null);

        var calls = 0;

        Task<string?> Factory(CancellationToken _)
        {
            calls++;
            return Task.FromResult<string?>("produced");
        }

        var value = await reader.GetOrCreateAsync("key", Factory);

        // A cached null is a hit, so this is negative caching and the factory does not run again.
        Assert.Null(value);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task A_cached_null_is_served_from_the_memory_layer()
    {
        var cache = CreateCache(out _);
        await cache.SetAsync<string?>("key", null);

        var calls = 0;

        Task<string?> Factory(CancellationToken _)
        {
            calls++;
            return Task.FromResult<string?>("produced");
        }

        var value = await cache.GetOrCreateAsync("key", Factory);

        // A null value is a valid cached value, so the memory layer serves it as a hit.
        Assert.Null(value);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task A_payload_that_does_not_fit_the_type_is_a_miss_and_is_logged()
    {
        var distributed = CreateDistributedCache();
        await distributed.SetAsync(
            DefaultOptions.KeyPrefix + "key",
            Encoding.UTF8.GetBytes("{\"value\":123,\"expiresAtUtc\":\"2099-01-01T00:00:00+00:00\"}"),
            new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5) });
        var logger = Substitute.For<ILogger<TwoLayerCache>>();
        var cache = new TwoLayerCache(
            new MemoryCache(new MemoryCacheOptions()), distributed, Options.Create(DefaultOptions), logger);

        Assert.Null(await cache.GetAsync<string>("key"));

        var arguments = logger.ReceivedCalls().Single(call => call.GetMethodInfo().Name == "Log").GetArguments();
        Assert.Equal(LogLevel.Warning, arguments[0]);
        Assert.IsType<JsonException>(arguments[3]);
        Assert.Contains(DefaultOptions.KeyPrefix + "key", Format(arguments));
    }

    private static string Format(object?[] arguments)
        => (string)((Delegate)arguments[4]!).DynamicInvoke(arguments[2], arguments[3])!;

    [Fact]
    public async Task A_payload_that_does_not_fit_the_type_is_reloaded_and_replaced()
    {
        var distributed = CreateDistributedCache();
        await distributed.SetAsync(
            DefaultOptions.KeyPrefix + "key",
            Encoding.UTF8.GetBytes("{\"value\":123,\"expiresAtUtc\":\"2099-01-01T00:00:00+00:00\"}"),
            new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5) });
        var cache = CreateCache(distributed);
        var calls = 0;

        Task<string> Factory(CancellationToken _)
        {
            calls++;
            return Task.FromResult("produced");
        }

        var first = await cache.GetOrCreateAsync("key", Factory);
        var second = await cache.GetOrCreateAsync("key", Factory);

        // An unreadable entry is a miss, so the factory runs and the entry is rewritten with the
        // current shape, which the next call can then read.
        Assert.Equal("produced", first);
        Assert.Equal("produced", second);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task The_default_serializer_writes_camel_case_ignores_nulls_and_names_enums()
    {
        var distributed = CreateDistributedCache();
        var writer = CreateCache(distributed);

        // A separate reader, so the value comes back through the payload rather than the memory layer.
        var reader = CreateCache(distributed);
        var payload = new SerializedPayload(1, null, SerializedStatus.Inactive);

        await writer.SetAsync("key", payload);

        var raw = await distributed.GetAsync(DefaultOptions.KeyPrefix + "key");
        var json = Encoding.UTF8.GetString(raw!);
        Assert.Contains("\"id\":1", json);
        Assert.DoesNotContain("\"name\"", json);
        Assert.Contains("\"status\":\"Inactive\"", json);

        Assert.Equal(payload, await reader.GetAsync<SerializedPayload>("key"));
    }

    [Fact]
    public async Task A_numeric_enum_payload_still_reads()
    {
        var distributed = CreateDistributedCache();
        await distributed.SetAsync(
            DefaultOptions.KeyPrefix + "key",
            Encoding.UTF8.GetBytes("{\"value\":1,\"expiresAtUtc\":\"2099-01-01T00:00:00+00:00\"}"),
            new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5) });
        var cache = CreateCache(distributed);

        Assert.Equal(SerializedStatus.Inactive, await cache.GetAsync<SerializedStatus>("key"));
    }

    [Fact]
    public async Task A_write_publishes_the_key_for_other_instances()
    {
        var backplane = new FakeBackplane();
        var cache = CreateCache(CreateDistributedCache(), backplane, out _);

        await cache.SetAsync("key", "value");

        Assert.Equal(DefaultOptions.KeyPrefix + "key", Assert.Single(backplane.Published));
    }

    [Fact]
    public async Task A_filled_key_is_published_for_other_instances()
    {
        var backplane = new FakeBackplane();
        var cache = CreateCache(CreateDistributedCache(), backplane, out _);
        var calls = 0;

        await cache.GetOrCreateAsync("key", _ => Task.FromResult(++calls));

        Assert.Equal(DefaultOptions.KeyPrefix + "key", Assert.Single(backplane.Published));
    }

    [Fact]
    public async Task A_remove_publishes_the_key_for_other_instances()
    {
        var backplane = new FakeBackplane();
        var cache = CreateCache(CreateDistributedCache(), backplane, out _);

        await cache.RemoveAsync("key");

        Assert.Equal(DefaultOptions.KeyPrefix + "key", Assert.Single(backplane.Published));
    }

    [Fact]
    public async Task An_invalidation_from_another_instance_evicts_the_memory_layer()
    {
        var backplane = new FakeBackplane();
        var cache = CreateCache(CreateDistributedCache(), backplane, out var memory);
        var cacheKey = DefaultOptions.KeyPrefix + "key";

        await cache.SetAsync("key", "value");
        Assert.True(memory.TryGetValue(cacheKey, out _));

        backplane.Raise(cacheKey);

        Assert.False(memory.TryGetValue(cacheKey, out _));
    }

    [Fact]
    public async Task An_invalidation_from_another_instance_leaves_the_distributed_layer_alone()
    {
        var backplane = new FakeBackplane();
        var distributed = CreateDistributedCache();
        var cache = CreateCache(distributed, backplane, out _);
        var cacheKey = DefaultOptions.KeyPrefix + "key";

        await cache.SetAsync("key", "value");

        backplane.Raise(cacheKey);

        Assert.NotNull(await distributed.GetAsync(cacheKey));
    }

    [Fact]
    public async Task A_disabled_memory_layer_publishes_nothing()
    {
        var backplane = new FakeBackplane();
        var options = new CachewOptions { DefaultInMemoryTimeToLive = TimeSpan.Zero };
        var cache = CreateCache(CreateDistributedCache(), backplane, out _, options);

        await cache.SetAsync("key", "value");
        await cache.RemoveAsync("key");

        Assert.Empty(backplane.Published);
    }

    [Fact]
    public void Disposing_the_cache_unsubscribes_from_the_backplane()
    {
        var backplane = new FakeBackplane();
        var cache = CreateCache(CreateDistributedCache(), backplane, out _);
        Assert.True(backplane.IsSubscribed);

        ((IDisposable)cache).Dispose();

        Assert.False(backplane.IsSubscribed);
    }

    [Fact]
    public async Task A_failing_backplane_does_not_stop_a_write_or_a_remove()
    {
        var backplane = new FakeBackplane(new InvalidOperationException("the backplane is unavailable."));
        var logger = Substitute.For<ILogger<TwoLayerCache>>();
        var cache = new TwoLayerCache(
            new MemoryCache(new MemoryCacheOptions()),
            CreateDistributedCache(),
            Options.Create(DefaultOptions),
            logger: logger,
            backplane: backplane);

        await cache.SetAsync("key", "value");
        Assert.Equal("value", await cache.GetAsync<string>("key"));

        await cache.RemoveAsync("key");
        Assert.Null(await cache.GetAsync<string>("key"));

        // Both the write and the remove report that the invalidation could not be published.
        Assert.Equal(2, logger.ReceivedCalls().Count(call => call.GetMethodInfo().Name == "Log"));
    }

    [Fact]
    public async Task A_received_invalidation_is_not_published_again()
    {
        var backplane = new FakeBackplane();
        var cache = CreateCache(CreateDistributedCache(), backplane, out _);

        await cache.SetAsync("key", "value");
        Assert.Single(backplane.Published);
        backplane.Published.Clear();

        backplane.Raise(DefaultOptions.KeyPrefix + "key");

        // A received invalidation evicts the local copy and nothing else: it does not re-publish, so
        // one change cannot fan out and one instance cannot start echoing another.
        Assert.Empty(backplane.Published);
    }

    [Fact]
    public async Task A_disabled_cache_stores_and_returns_nothing()
    {
        var options = new CachewOptions { Enabled = false };
        var distributed = CreateDistributedCache();
        var cache = CreateCache(distributed, options, out var memory);
        var cacheKey = DefaultOptions.KeyPrefix + "key";

        await cache.SetAsync("key", "value");

        Assert.Null(await cache.GetAsync<string>("key"));
        Assert.Null(await distributed.GetAsync(cacheKey));
        Assert.False(memory.TryGetValue(cacheKey, out _));
    }

    [Fact]
    public async Task A_disabled_cache_ignores_a_per_entry_override()
    {
        var options = new CachewOptions { Enabled = false };
        var distributed = CreateDistributedCache();
        var cache = CreateCache(distributed, options, out _);

        await cache.SetAsync("key", "value", new CacheEntryOptions<string>
        {
            InMemoryTimeToLive = TimeSpan.FromMinutes(1),
            DistributedTimeToLive = TimeSpan.FromMinutes(1),
        });

        Assert.Null(await cache.GetAsync<string>("key"));
    }

    [Fact]
    public async Task A_disabled_cache_runs_the_factory_every_time()
    {
        var options = new CachewOptions { Enabled = false };
        var cache = CreateCache(CreateDistributedCache(), options, out _);
        var calls = 0;

        await cache.GetOrCreateAsync("key", _ => Task.FromResult(++calls));
        await cache.GetOrCreateAsync("key", _ => Task.FromResult(++calls));

        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task A_disabled_cache_publishes_nothing()
    {
        var backplane = new FakeBackplane();
        var options = new CachewOptions { Enabled = false };
        var cache = new TwoLayerCache(
            new MemoryCache(new MemoryCacheOptions()),
            CreateDistributedCache(),
            Options.Create(options),
            backplane: backplane);

        await cache.SetAsync("key", "value");
        await cache.RemoveAsync("key");

        Assert.Empty(backplane.Published);
    }

    [Fact]
    public async Task A_failing_distributed_layer_is_logged_for_a_read_a_write_and_a_remove()
    {
        var logger = Substitute.For<ILogger<TwoLayerCache>>();
        var cache = new TwoLayerCache(
            new MemoryCache(new MemoryCacheOptions()),
            new FailingDistributedCache(new CacheUnavailableException()),
            Options.Create(DefaultOptions),
            logger);

        await cache.GetAsync<string>("key");
        await cache.SetAsync("key", "value");
        await cache.RemoveAsync("key");

        // The read, the write and the remove each report the failure rather than swallowing it
        // silently, which is what makes an awaited provider worth configuring.
        Assert.Equal(3, logger.ReceivedCalls().Count(call => call.GetMethodInfo().Name == "Log"));
    }

    [Fact]
    public void Caching_is_on_by_default()
    {
        Assert.True(new CachewOptions().Enabled);
    }

    [Fact]
    public async Task A_per_entry_lifetime_cannot_re_enable_a_memory_layer_that_is_off_by_default()
    {
        var options = new CachewOptions
        {
            DefaultInMemoryTimeToLive = TimeSpan.Zero,
            DefaultDistributedTimeToLive = TimeSpan.FromMinutes(10),
        };
        var distributed = CreateDistributedCache();
        var memory = new MemoryCache(new MemoryCacheOptions());
        var backplane = new FakeBackplane();
        var cache = new TwoLayerCache(memory, distributed, Options.Create(options), backplane: backplane);
        var entry = new CacheEntryOptions<string> { InMemoryTimeToLive = TimeSpan.FromMinutes(5) };

        await cache.SetAsync("key", "value", entry);

        // The layer is off for the whole app, so the value lands in the distributed layer only and
        // there is nothing in memory to invalidate.
        Assert.False(memory.TryGetValue(options.KeyPrefix + "key", out _));
        Assert.Empty(backplane.Published);
        Assert.NotNull(await distributed.GetAsync(options.KeyPrefix + "key"));

        // Reading back through the same override must not resurrect a value that was removed.
        await cache.RemoveAsync("key");
        Assert.Null(await cache.GetAsync<string>("key", entry));
    }

    [Fact]
    public async Task A_per_entry_lifetime_cannot_re_enable_a_distributed_layer_that_is_off_by_default()
    {
        var options = new CachewOptions
        {
            DefaultInMemoryTimeToLive = TimeSpan.FromMinutes(1),
            DefaultDistributedTimeToLive = TimeSpan.Zero,
        };
        var distributed = CreateDistributedCache();
        var memory = new MemoryCache(new MemoryCacheOptions());
        var cache = new TwoLayerCache(memory, distributed, Options.Create(options));
        var entry = new CacheEntryOptions<string> { DistributedTimeToLive = TimeSpan.FromMinutes(5) };

        await cache.SetAsync("key", "value", entry);

        Assert.Null(await distributed.GetAsync(options.KeyPrefix + "key"));
        Assert.True(memory.TryGetValue(options.KeyPrefix + "key", out _));
    }

    private static ICachew CreateCache(IDistributedCache distributed, CachewOptions options, out IMemoryCache memory)
    {
        memory = new MemoryCache(new MemoryCacheOptions());
        return new TwoLayerCache(memory, distributed, Options.Create(options));
    }

    private static ICachew CreateCache(IDistributedCache distributed, ICachewBackplane backplane, out IMemoryCache memory, CachewOptions? options = null)
    {
        memory = new MemoryCache(new MemoryCacheOptions());
        return new TwoLayerCache(memory, distributed, Options.Create(options ?? DefaultOptions), backplane: backplane);
    }

    [Fact]
    public async Task A_size_limit_evicts_entries_once_it_is_reached()
    {
        var memory = new MemoryCache(new MemoryCacheOptions { SizeLimit = 2 });
        var cache = new TwoLayerCache(memory, CreateDistributedCache(), Options.Create(DefaultOptions));

        for (var i = 0; i < 5; i++)
        {
            await cache.SetAsync($"key{i}", i);
        }

        var remaining = Enumerable.Range(0, 5).Count(i => memory.TryGetValue(DefaultOptions.KeyPrefix + "key" + i, out _));

        // The limit is enforced, which it can be because the cache sizes every entry as one.
        Assert.True(remaining <= 2, $"expected at most 2 of 5 entries to remain, found {remaining}");
    }

    private static ICachew CreateCache(IDistributedCache distributed)
        => new TwoLayerCache(new MemoryCache(new MemoryCacheOptions()), distributed, Options.Create(DefaultOptions));

    private static ICachew CreateCache(CachewOptions options, out IDistributedCache distributed, out IMemoryCache memory)
    {
        distributed = CreateDistributedCache();
        memory = new MemoryCache(new MemoryCacheOptions());
        return new TwoLayerCache(memory, distributed, Options.Create(options));
    }

    private static ICachew CreateCache(out IDistributedCache distributed)
    {
        distributed = CreateDistributedCache();
        return CreateCache(distributed);
    }

    private static MemoryDistributedCache CreateDistributedCache()
        => new(Options.Create(new MemoryDistributedCacheOptions()));

    public sealed record Payload(int Id, string Name);

    public sealed record SerializedPayload(int Id, string? Name, SerializedStatus Status);

    public enum SerializedStatus
    {
        Active,
        Inactive,
    }
}
