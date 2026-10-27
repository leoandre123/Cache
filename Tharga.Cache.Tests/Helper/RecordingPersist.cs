namespace Tharga.Cache.Tests.Helper;

/// <summary>
/// Wraps a persist and counts the calls the cache makes. A touch with the read item can be held open with
/// <see cref="TouchGate"/> or made to throw with <see cref="TouchException"/>.
/// </summary>
internal class RecordingPersist : IPersist
{
    private readonly IPersist _inner;
    private int _getCalls;
    private int _getManyCalls;
    private int _buyMoreTimeByKeyCalls;
    private int _buyMoreTimeWithItemCalls;

    public RecordingPersist(IPersist inner)
    {
        _inner = inner;
    }

    public int GetCalls => _getCalls;
    public int GetManyCalls => _getManyCalls;
    public int BuyMoreTimeByKeyCalls => _buyMoreTimeByKeyCalls;
    public int BuyMoreTimeWithItemCalls => _buyMoreTimeWithItemCalls;
    public TaskCompletionSource TouchGate { get; set; }
    public Exception TouchException { get; set; }
    public Exception GetManyException { get; set; }

    public Task<CacheItem<T>> GetAsync<T>(Key key)
    {
        Interlocked.Increment(ref _getCalls);
        return _inner.GetAsync<T>(key);
    }

    public Task<IReadOnlyList<(Key Key, CacheItem<T> CacheItem)>> GetManyAsync<T>(IEnumerable<Key> keys)
    {
        Interlocked.Increment(ref _getManyCalls);
        if (GetManyException != null) throw GetManyException;

        return _inner.GetManyAsync<T>(keys);
    }

    public IAsyncEnumerable<(Key Key, CacheItem<T> CacheItem)> FindAsync<T>(Key key) => _inner.FindAsync<T>(key);

    public Task SetAsync<T>(Key key, CacheItem<T> cacheItem, bool staleWhileRevalidate) => _inner.SetAsync(key, cacheItem, staleWhileRevalidate);

    public Task<bool> BuyMoreTime<T>(Key key)
    {
        Interlocked.Increment(ref _buyMoreTimeByKeyCalls);
        return _inner.BuyMoreTime<T>(key);
    }

    public async Task<bool> BuyMoreTime<T>(Key key, CacheItem<T> cacheItem, bool staleWhileRevalidate)
    {
        Interlocked.Increment(ref _buyMoreTimeWithItemCalls);
        if (TouchGate != null) await TouchGate.Task;
        if (TouchException != null) throw TouchException;

        return await _inner.BuyMoreTime(key, cacheItem, staleWhileRevalidate);
    }

    public Task<bool> Invalidate<T>(Key key) => _inner.Invalidate<T>(key);

    public Task<bool> DropAsync<T>(Key key) => _inner.DropAsync<T>(key);

    public Task<(bool Success, string Message)> CanConnectAsync() => _inner.CanConnectAsync();
}
