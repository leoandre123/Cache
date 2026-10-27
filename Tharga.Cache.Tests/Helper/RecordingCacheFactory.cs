using Microsoft.Extensions.Logging;
using Moq;
using Tharga.Cache.Core;
using Tharga.Cache.Persist;

namespace Tharga.Cache.Tests.Helper;

internal static class RecordingCacheFactory
{
    /// <summary>
    /// Builds a time-to-idle cache over an in-memory persist wrapped in a <see cref="RecordingPersist"/>.
    /// </summary>
    public static (ITimeToIdleCache Cache, RecordingPersist Persist) Create<TData>(Action<CacheTypeOptions> typeOptions = null, ILogger logger = null)
    {
        var options = new CacheOptions();
        options.RegisterType<TData, IMemory>(o =>
        {
            o.DefaultFreshSpan = TimeSpan.FromMinutes(10);
            typeOptions?.Invoke(o);
        });

        var persistLoader = new Mock<IPersistLoader>();
        var cacheMonitor = new CacheMonitor(persistLoader.Object, options);
        var persist = new RecordingPersist(new Memory(cacheMonitor));
        persistLoader.Setup(x => x.GetPersist(It.IsAny<Type>())).Returns(persist);

        return (new TimeToIdleCache(cacheMonitor, persistLoader.Object, new FetchQueue(cacheMonitor, options, null), options, logger), persist);
    }
}
