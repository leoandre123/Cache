using Microsoft.Extensions.Logging;
using Moq;
using Tharga.Cache.Core;
using Tharga.Cache.Persist;

namespace Tharga.Cache.Tests.Helper;

internal static class RecordingCacheFactory
{
    /// <summary>
    /// Builds a time cache over a <see cref="RecordingPersist"/>. The inner persist is in-memory unless
    /// <paramref name="innerPersist"/> provides another one. <typeparamref name="TData"/> is registered with
    /// <paramref name="typeOptions"/>.
    /// </summary>
    public static RecordingCache Create<TData>(bool timeToIdle = true, Action<CacheTypeOptions> typeOptions = null, bool failOpen = true, ILogger logger = null, Func<IManagedCacheMonitor, IPersist> innerPersist = null)
    {
        var options = new CacheOptions { FailOpenOnBackendError = failOpen };
        options.RegisterType<TData, IMemory>(o =>
        {
            o.DefaultFreshSpan = TimeSpan.FromMinutes(10);
            typeOptions?.Invoke(o);
        });

        var persistLoader = new Mock<IPersistLoader>(MockBehavior.Strict);
        var cacheMonitor = new CacheMonitor(persistLoader.Object, options);
        var persist = new RecordingPersist(innerPersist?.Invoke(cacheMonitor) ?? new Memory(cacheMonitor));
        persistLoader.Setup(x => x.GetPersist(It.IsAny<Type>())).Returns(persist);
        var fetchQueue = new FetchQueue(cacheMonitor, options, null);

        ITimeCache cache = timeToIdle
            ? new TimeToIdleCache(cacheMonitor, persistLoader.Object, fetchQueue, options, logger)
            : new TimeToLiveCache(cacheMonitor, persistLoader.Object, fetchQueue, options, logger);

        return new RecordingCache(cache, persist, cacheMonitor);
    }
}
