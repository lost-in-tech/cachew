using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cachew;

/// <summary>Options for <see cref="ICachew"/>.</summary>
public sealed class CachewOptions
{
    /// <summary>
    /// Whether the cache does anything. Defaults to <c>true</c>. Set it to <c>false</c> to turn the
    /// cache off from configuration, for example while an incident is in progress. A disabled cache
    /// stores nothing, returns nothing, and takes no part in cross instance invalidation, but
    /// <see cref="ICachew.GetOrCreateAsync{T}"/> still coalesces concurrent misses for a key so that a
    /// burst makes one call to the factory.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Default in-memory time to live. Defaults to one minute. Zero or negative disables the
    /// in-memory layer.
    /// </summary>
    public TimeSpan DefaultInMemoryTimeToLive { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Default distributed time to live. Defaults to ten minutes. Zero or negative disables the
    /// distributed layer.
    /// </summary>
    public TimeSpan DefaultDistributedTimeToLive { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>Prefix applied to every key in both layers. Defaults to <c>cachew:</c>.</summary>
    public string KeyPrefix { get; set; } = "cachew:";

    /// <summary>
    /// Serializer options used for distributed payloads. Defaults are the web defaults (camel case
    /// property names, case insensitive reads, numbers readable from strings) plus no nulls written
    /// and enums as strings. Changing this changes the payload format.
    /// </summary>
    public JsonSerializerOptions SerializerOptions { get; set; } = CreateDefaultSerializerOptions();

    private static JsonSerializerOptions CreateDefaultSerializerOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        // Keep payloads compact, and make an enum readable rather than a number, so reordering enum
        // members cannot silently reinterpret an entry that is already cached.
        options.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
        options.Converters.Add(new JsonStringEnumConverter());

        return options;
    }
}
