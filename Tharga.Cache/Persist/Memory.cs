using System.Collections.Concurrent;
using Tharga.Cache.Core;

namespace Tharga.Cache.Persist;

internal class Memory : IMemory
{
    private readonly ConcurrentDictionary<string, CacheItem> _datas = new();
    private readonly TimeProvider _timeProvider;

    public Memory(IManagedCacheMonitor cacheMonitor)
        : this(cacheMonitor, TimeProvider.System)
    {
    }

    /// <summary>
    /// Lets tests control the clock that extends an item's life on access. Not public, so dependency
    /// injection always uses the constructor above.
    /// </summary>
    internal Memory(IManagedCacheMonitor cacheMonitor, TimeProvider timeProvider)
    {
        _timeProvider = timeProvider;

        cacheMonitor.RequestEvictEvent += async (_, e) =>
        {
            var dropAsyncMethod = typeof(Memory)
                .GetMethod("DropAsync")!
                .MakeGenericMethod(e.Type);

            var task = (Task)dropAsyncMethod.Invoke(this, [e.Key])!;
            await task;

            cacheMonitor.Drop(e.Type, e.Key);
        };
    }

    public Task<CacheItem<T>> GetAsync<T>(Key key)
    {
        return Task.FromResult((CacheItem<T>)_datas.GetValueOrDefault(key));
    }

    public async IAsyncEnumerable<(Key Key, CacheItem<T> CacheItem)> FindAsync<T>(Key key)
    {
        var datas = _datas
            .Where(x => x.Value.GetType() == typeof(CacheItem<T>))
            .Where(x =>
                key.KeyParts
                    .All(kvp =>
                        x.Value.KeyParts.TryGetValue(kvp.Key, out var value) &&
                        value == kvp.Value
                    )
            );

        foreach (var data in datas)
        {
            yield return (data.Key, (CacheItem<T>)data.Value);
        }
    }

    public Task SetAsync<T>(Key key, CacheItem<T> item, bool staleWhileRevalidate)
    {
        _datas.AddOrUpdate(key, item, (_, _) => item);
        return Task.CompletedTask;
    }

    public Task<bool> BuyMoreTime<T>(Key key)
    {
        return SetUpdateTimeAsync(key, _timeProvider.GetUtcNow().UtcDateTime);
    }

    public Task<bool> BuyMoreTime<T>(Key key, CacheItem<T> cacheItem, bool staleWhileRevalidate)
    {
        var updatedItem = cacheItem with { UpdateTime = _timeProvider.GetUtcNow().UtcDateTime };
        return Task.FromResult(_datas.TryUpdate(key, updatedItem, cacheItem));
    }

    public Task<bool> Invalidate<T>(Key key)
    {
        return SetUpdateTimeAsync(key, DateTime.MinValue);
    }

    public Task<bool> DropAsync<T>(Key key)
    {
        var result = _datas.TryRemove(key, out _);
        return Task.FromResult(result);
    }

    public Task<(bool Success, string Message)> CanConnectAsync()
    {
        var cnt = _datas.Count;
        return Task.FromResult((true, $"There {(cnt == 1 ? "is" : "are")} {cnt} record{(cnt == 1 ? "" : "s")} cached."));
    }

    private Task<bool> SetUpdateTimeAsync(Key key, DateTime updateTime)
    {
        if (_datas.TryGetValue(key, out var item))
        {
            var updatedItem = item with { UpdateTime = updateTime };
            var r = _datas.TryUpdate(key, updatedItem, item);
            return Task.FromResult(r);
        }

        return Task.FromResult(false);
    }
}