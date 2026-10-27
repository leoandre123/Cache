namespace Tharga.Cache;

public interface IPersist
{
    Task<CacheItem<T>> GetAsync<T>(Key key);

    /// <summary>
    /// Reads several keys, one entry per key in the requested order, with null for keys that are not cached.
    /// </summary>
    async Task<IReadOnlyList<(Key Key, CacheItem<T> CacheItem)>> GetManyAsync<T>(IEnumerable<Key> keys)
    {
        var result = new List<(Key Key, CacheItem<T> CacheItem)>();
        foreach (var key in keys) result.Add((key, await GetAsync<T>(key)));
        return result;
    }
    IAsyncEnumerable<(Key Key, CacheItem<T> CacheItem)> FindAsync<T>(Key key);
    Task SetAsync<T>(Key key, CacheItem<T> cacheItem, bool staleWhileRevalidate);
    Task<bool> BuyMoreTime<T>(Key key);

    /// <summary>
    /// Buys more time for an item the caller has just read, so the persist does not have to read it again.
    /// </summary>
    Task<bool> BuyMoreTime<T>(Key key, CacheItem<T> cacheItem, bool staleWhileRevalidate) => BuyMoreTime<T>(key);

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