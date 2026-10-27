namespace Tharga.Cache;

public record CacheTypeOptions
{
    /// <summary>
    /// If this is set to true, stale data will be returned directly and an update will be performed in the background.
    /// When the data is updated the event DataSetEvent will be fired and the method 'xx'.
    /// </summary>
    public bool StaleWhileRevalidate { get; set; }

    /// <summary>
    /// If this is set to true and there is no cached data at all (first load),
    /// default(T) will be returned immediately and the factory will run in the background.
    /// Works independently of <see cref="StaleWhileRevalidate"/>.
    /// </summary>
    public bool ReturnDefaultOnFirstLoad { get; set; }
    public long? MaxSize { get; set; }
    public int? MaxCount { get; set; }
    public EvictionPolicy EvictionPolicy { get; set; } = EvictionPolicy.FirstInFirstOut;
    public TimeSpan? DefaultFreshSpan { get; set; }

    /// <summary>
    /// If this is set to true, the time-to-idle touch after a hit (<see cref="ITimeToIdleCache"/>) runs in the
    /// background instead of before the read returns, so a hit costs a single read from the caller's point of view.
    /// Failures of the background touch are logged and never reach the caller.
    /// Events and monitor bookkeeping still happen before the read returns. At most one background touch per key is
    /// pending at a time; a hit while one is pending does not start another. No background touch is started once the
    /// cache has been disposed; touches already running are not awaited on shutdown. Default is false.
    /// </summary>
    public bool TouchInBackground { get; set; }

    /// <summary>
    /// If this is set, an <see cref="ITimeToIdleCache"/> hit does not touch an item that was written or touched less
    /// than this long ago, which saves a backend write on every hit of a hot key. The trade-off is precision: an item
    /// can then expire up to this much earlier than its idle time after the last hit. Applies to foreground and
    /// background touches. Default is null (every hit touches).
    /// </summary>
    public TimeSpan? MinTimeBetweenTouches { get; set; }

    internal Type PersistType { get; set; }
}