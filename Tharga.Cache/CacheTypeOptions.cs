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
    /// If this is set to true, the touch after an <see cref="ITimeToIdleCache"/> hit runs in the background, so the read
    /// returns after a single backend read. A failed background touch is logged and never reaches the caller.
    /// </summary>
    public bool TouchInBackground { get; set; }

    internal Type PersistType { get; set; }
}