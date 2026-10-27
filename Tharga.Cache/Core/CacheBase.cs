using Microsoft.Extensions.Logging;
using Tharga.Cache.Persist;

namespace Tharga.Cache.Core;

internal abstract class CacheBase : ICache
{
    private readonly IManagedCacheMonitor _cacheMonitor;
    private readonly IPersistLoader _persistLoader;
    private readonly IFetchQueue _fetchQueue;
    private readonly ILogger _logger;
    protected readonly CacheOptions _options;

    protected CacheBase(IManagedCacheMonitor cacheMonitor, IPersistLoader persistLoader, IFetchQueue fetchQueue, CacheOptions options, ILogger logger = null)
    {
        if (options.MaxConcurrentFetchCount <= 0) throw new InvalidOperationException($"Min value for {nameof(options.MaxConcurrentFetchCount)} is 1.");

        _cacheMonitor = cacheMonitor;
        _persistLoader = persistLoader;
        _fetchQueue = fetchQueue;
        _options = options;
        _logger = logger;
    }

    public event EventHandler<DataSetEventArgs> DataSetEvent;
    public event EventHandler<DataGetEventArgs> DataGetEvent;
    public event EventHandler<DataDropEventArgs> DataDropEvent;

    protected abstract TimeSpan GetDefaultFreshSpan<T>();

    public virtual async Task<T> GetAsync<T>(Key key, Func<Task<T>> fetch)
    {
        return (await GetCoreAsync(key, fetch, GetDefaultFreshSpan<T>())).Data;
    }

    protected async Task<(T Data, bool Fresh)> GetCoreAsync<T>(Key key, Func<Task<T>> fetch, TimeSpan freshSpan, Func<T, Task> callback = default)
    {
        var fs = freshSpan == TimeSpan.MaxValue ? (TimeSpan?)null : freshSpan;

        key = key.SetTypeKey<T>();

        var result = await TryGetPersistAsync<T>(key);

        if (result.IsValid(_options.UtcNow))
        {
            TrackIfNeeded(key, result);
            await OnGetAsync(key, result);
            return (result.GetData(), true);
        }

        var typeOptions = GetTypeOptions<T>();

        if (typeOptions.StaleWhileRevalidate && result != null)
        {
            TrackIfNeeded(key, result);
            var response = result.GetData();
            await OnGetAsync(key, result);
            BackgroundLoad(key, fetch, callback, fs);

            return (response, false);
        }

        if (typeOptions.ReturnDefaultOnFirstLoad && result == null)
        {
            BackgroundLoad(key, fetch, callback, fs);
            return (default, false);
        }

        var loadResponse = await _fetchQueue.LoadData(key, fetch, fs, FetchCallback);
        await OnGetAsync<T>(key, null);
        return (loadResponse, true);
    }

    private void BackgroundLoad<T>(Key key, Func<Task<T>> fetch, Func<T, Task> callback, TimeSpan? fs)
    {
        Task.Run(async () =>
        {
            var data = await _fetchQueue.LoadData(key, fetch, fs, FetchCallback);
            callback?.Invoke(data);
        });
    }

    private async Task FetchCallback<T>(Key key, CacheItem<T> item, bool staleWhileRevalidate)
    {
        if (await TrySetPersistAsync(key, item, staleWhileRevalidate))
        {
            await OnSetAsync(key, item, staleWhileRevalidate);
        }
    }

    protected CacheTypeOptions GetTypeOptions<T>()
    {
        var options = _options.Get<T>();
        return options;
    }

    public virtual async Task<T> PeekAsync<T>(Key key)
    {
        key = key.SetTypeKey<T>();

        return await ResolvePeekAsync(key, await TryGetPersistAsync<T>(key));
    }

    public virtual async Task<IReadOnlyList<(Key Key, T Data)>> PeekManyAsync<T>(IEnumerable<Key> keys)
    {
        var requestedKeys = keys.ToArray();
        if (requestedKeys.Length == 0) return [];

        var typedKeys = requestedKeys.Select(x => x.SetTypeKey<T>()).ToArray();
        IReadOnlyList<(Key Key, CacheItem<T> CacheItem)> items;
        try
        {
            items = await GetPersist<T>().GetManyAsync<T>(typedKeys);
        }
        catch (Exception ex) when (_options.FailOpenOnBackendError)
        {
            LogFailOpen(ex, "read", typedKeys[0], typeof(T));
            items = typedKeys.Select(x => (x, (CacheItem<T>)null)).ToArray();
        }

        var data = await Task.WhenAll(typedKeys.Select((key, index) => ResolvePeekAsync(key, items[index].CacheItem)));
        return requestedKeys.Select((key, index) => (key, data[index])).ToArray();
    }

    private async Task<T> ResolvePeekAsync<T>(Key key, CacheItem<T> result)
    {
        if (result.IsValid(_options.UtcNow) || (GetTypeOptions<T>().StaleWhileRevalidate && result != null))
        {
            var response = result.GetData();
            await OnGetAsync(key, result);

            return response;
        }

        return default;
    }

    public virtual Task SetAsync<T>(Key key, T data)
    {
        return SetCoreAsync(key, data, GetDefaultFreshSpan<T>());
    }

    protected async Task SetCoreAsync<T>(Key key, T data, TimeSpan freshSpan)
    {
        key = key.SetTypeKey<T>();

        var staleWhileRevalidate = GetTypeOptions<T>().StaleWhileRevalidate;
        var item = CacheItemBuilder.BuildCacheItem(key.KeyParts, data, freshSpan, createTime: _options.UtcNow);
        if (await TrySetPersistAsync(key, item, staleWhileRevalidate))
        {
            await OnSetAsync(key, item, staleWhileRevalidate);
        }
    }

    public virtual async Task<int> DropAsync<T>(Key key)
    {
        if (string.IsNullOrEmpty(key?.Value) || key.KeyParts.Any())
        {
            var results = GetPersist<T>().FindAsync<T>(key);
            var count = 0;
            await foreach (var result in results)
            {
                var dropped = await GetPersist<T>().DropAsync<T>(result.Key);
                if (dropped)
                {
                    OnDrop<T>(result.Key);
                    count++;
                }
            }

            return count;
        }

        key = key.SetTypeKey<T>();

        var item = await GetPersist<T>().DropAsync<T>(key);
        if (item)
        {
            OnDrop<T>(key);
            return 1;
        }

        return 0;
    }

    public async Task<int> InvalidateAsync<T>(Key key)
    {
        if (!GetTypeOptions<T>().StaleWhileRevalidate) return await DropAsync<T>(key);

        if (string.IsNullOrEmpty(key?.Value) || key.KeyParts.Any())
        {
            var results = GetPersist<T>().FindAsync<T>(key);
            var count = 0;
            await foreach (var result in results)
            {
                if (await GetPersist<T>().Invalidate<T>(result.Key))
                {
                    count++;
                }
            }

            return count;
        }

        key = key.SetTypeKey<T>();

        return await GetPersist<T>().Invalidate<T>(key) ? 1 : 0;
    }

    private async Task OnSetAsync<T>(Key key, CacheItem<T> item, bool staleWhileRevalidate)
    {
        await EvictItems(item.Data);

        DataSetEvent?.Invoke(this, new DataSetEventArgs(key, item.Data));
        var returnDefaultOnFirstLoad = GetTypeOptions<T>().ReturnDefaultOnFirstLoad;
        _cacheMonitor.Set(typeof(T), key, item, staleWhileRevalidate, returnDefaultOnFirstLoad);
    }

    protected virtual Task OnGetAsync<T>(Key key, CacheItem<T> item)
    {
        return OnGetCoreAsync(key, item, false);
    }

    protected async Task OnGetCoreAsync<T>(Key key, CacheItem<T> item, bool buyMoreTime)
    {
        DataGetEvent?.Invoke(this, new DataGetEventArgs(key));

        if (buyMoreTime && GetTypeOptions<T>().TouchInBackground)
        {
            _cacheMonitor.Accessed(typeof(T), key, false);
            _ = Task.Run(async () =>
            {
                try
                {
                    await BuyMoreTimeAsync(key, item);
                }
                catch (Exception ex)
                {
                    LogFailOpen(ex, "buy-more-time", key, typeof(T));
                }
            });
            return;
        }

        var moreTimeBought = false;
        if (buyMoreTime)
        {
            try
            {
                moreTimeBought = await BuyMoreTimeAsync(key, item);
            }
            catch (Exception ex) when (_options.FailOpenOnBackendError)
            {
                LogFailOpen(ex, "buy-more-time", key, typeof(T));
            }
        }

        _cacheMonitor.Accessed(typeof(T), key, moreTimeBought);
    }

    private Task<bool> BuyMoreTimeAsync<T>(Key key, CacheItem<T> item)
    {
        var persist = GetPersist<T>();
        if (item == null || !item.IsValid(_options.UtcNow)) return persist.BuyMoreTime<T>(key);

        return persist.BuyMoreTime(key, item, GetTypeOptions<T>().StaleWhileRevalidate);
    }

    private void OnDrop<T>(Key key)
    {
        DataDropEvent?.Invoke(this, new DataDropEventArgs(key));
        _cacheMonitor.Drop(typeof(T), key);
    }

    private void TrackIfNeeded<T>(Key key, CacheItem<T> item)
    {
        var typeOptions = GetTypeOptions<T>();
        _cacheMonitor.Track(typeof(T), key, item, typeOptions.StaleWhileRevalidate, typeOptions.ReturnDefaultOnFirstLoad);
    }

    private IPersist GetPersist<T>()
    {
        var persist = _persistLoader.GetPersist(_options.Get<T>().PersistType);
        return persist;
    }

    /// <summary>
    /// Reads from the persist backend. When <see cref="CacheOptions.FailOpenOnBackendError"/> is enabled, a
    /// backend exception is logged and treated as a miss (returns null) so the caller falls back to the source loader.
    /// </summary>
    private async Task<CacheItem<T>> TryGetPersistAsync<T>(Key key)
    {
        try
        {
            return await GetPersist<T>().GetAsync<T>(key);
        }
        catch (Exception ex) when (_options.FailOpenOnBackendError)
        {
            LogFailOpen(ex, "read", key, typeof(T));
            return null;
        }
    }

    /// <summary>
    /// Writes to the persist backend. When <see cref="CacheOptions.FailOpenOnBackendError"/> is enabled, a backend
    /// exception is logged and swallowed (returns false) so a failed cache write never faults the caller.
    /// </summary>
    private async Task<bool> TrySetPersistAsync<T>(Key key, CacheItem<T> item, bool staleWhileRevalidate)
    {
        try
        {
            await GetPersist<T>().SetAsync(key, item, staleWhileRevalidate);
            return true;
        }
        catch (Exception ex) when (_options.FailOpenOnBackendError)
        {
            LogFailOpen(ex, "write", key, typeof(T));
            return false;
        }
    }

    private void LogFailOpen(Exception ex, string operation, Key key, Type type)
    {
        _logger?.LogWarning(ex, "Cache persist {Operation} failed for key '{Key}' on type '{Type}'; failing open ({Message}).", operation, key.Value, type.Name, ex.Message);
    }

    private async Task EvictItems<T>(T data)
    {
        var maxCount = GetTypeOptions<T>().MaxCount;
        var maxSize = GetTypeOptions<T>().MaxSize;

        if (maxCount != null || maxSize != null)
        {
            var result = _cacheMonitor.GetInfos().FirstOrDefault(x => x.Type == typeof(T));
            if (maxCount <= result?.Items.Count || maxSize <= result?.Items.Sum(x => x.Value.Size) + data.ToSize())
            {
                var keyToDrop = _cacheMonitor.Get<T>(GetTypeOptions<T>().EvictionPolicy);
                await GetPersist<T>().DropAsync<T>(keyToDrop);
                OnDrop<T>(keyToDrop);
            }
        }
    }
}