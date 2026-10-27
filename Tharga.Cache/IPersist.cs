namespace Tharga.Cache;

public interface IPersist
{
    Task<CacheItem<T>> GetAsync<T>(Key key);
    IAsyncEnumerable<(Key Key, CacheItem<T> CacheItem)> FindAsync<T>(Key key);
    Task SetAsync<T>(Key key, CacheItem<T> cacheItem, bool staleWhileRevalidate);
    Task<bool> BuyMoreTime<T>(Key key);

    /// <summary>
    /// Buys more time for an item the caller has already read, so the persist does not have to read it again.
    /// Implementations should only apply the touch if the stored entry is still the one that was read, so a concurrent
    /// replace or invalidate is not overwritten with older data; the built-in Memory, MongoDB and Redis persists do,
    /// and File does within one process. Returns true if the entry was touched.
    /// The default implementation falls back to <see cref="BuyMoreTime{T}(Key)"/>, which touches whatever is stored
    /// and does not give that guarantee.
    /// </summary>
    /// <param name="key">The key of the item.</param>
    /// <param name="cacheItem">The item as it was read from this persist.</param>
    /// <param name="staleWhileRevalidate">The same flag that is passed to <see cref="SetAsync{T}"/>, for backends whose expiry depends on it.</param>
    Task<bool> BuyMoreTime<T>(Key key, CacheItem<T> cacheItem, bool staleWhileRevalidate)
    {
        return BuyMoreTime<T>(key);
    }

    /// <summary>
    /// Change the time for the record so that it becomes invalid, but keeps the value in cache.
    /// This is needed when using StaleWhileRevalidate.
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <param name="key"></param>
    /// <returns></returns>
    Task<bool> Invalidate<T>(Key key);

    /// <summary>
    /// Returns true if something was removed.
    /// </summary>
    /// <param name="key"></param>
    /// <returns></returns>
    Task<bool> DropAsync<T>(Key key);

    Task<(bool Success, string Message)> CanConnectAsync();
}