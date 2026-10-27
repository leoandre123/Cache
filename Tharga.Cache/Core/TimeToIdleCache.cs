using Microsoft.Extensions.Logging;

namespace Tharga.Cache.Core;

internal class TimeToIdleCache : TimeCacheBase, ITimeToIdleCache
{
    public TimeToIdleCache(IManagedCacheMonitor cacheMonitor, IPersistLoader persistLoader, IFetchQueue fetchQueue, CacheOptions options, ILogger logger = null)
        : base(cacheMonitor, persistLoader, fetchQueue, options, logger)
    {
    }

    protected override Task OnGetAsync<T>(Key key, CacheItem<T> item)
    {
        return OnGetCoreAsync(key, item, true);
    }
}