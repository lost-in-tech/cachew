using System.Text.Json;

namespace Cachew;

/// <summary>Options for <see cref="ICachew"/>.</summary>
public sealed class CachewOptions
{
    /// <summary>
    /// Default L1 (in-memory) time to live. Defaults to one minute. Zero or negative disables the
    /// in-memory layer.
    /// </summary>
    public TimeSpan DefaultL1TimeToLive { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Default L2 (distributed) time to live. Defaults to ten minutes. Zero or negative disables the
    /// distributed layer.
    /// </summary>
    public TimeSpan DefaultL2TimeToLive { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>Prefix applied to every key in both layers. Defaults to <c>cachew:</c>.</summary>
    public string KeyPrefix { get; set; } = "cachew:";

    /// <summary>Serializer options used for L2 payloads.</summary>
    public JsonSerializerOptions SerializerOptions { get; set; } = new(JsonSerializerDefaults.Web);
}
