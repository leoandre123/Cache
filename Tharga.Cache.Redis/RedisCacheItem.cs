using System.Text.Json.Serialization;

namespace Tharga.Cache.Redis;

/// <summary>
/// A cache item as read from Redis, with a fingerprint of the exact value that was stored. A touch compares the
/// fingerprint on the server instead of serializing the item again, so it does not depend on the serialization
/// round-tripping (a different shape of the type in another deployment, a converter that is not symmetric).
/// </summary>
internal record RedisCacheItem<T> : CacheItem<T>
{
    /// <summary>
    /// The lowercase hex SHA1 of the stored bytes, the same value Lua's <c>redis.sha1hex</c> gives. Used as a
    /// fingerprint, not for security.
    /// </summary>
    [JsonIgnore]
    public string StoredValueHash { get; init; }
}
