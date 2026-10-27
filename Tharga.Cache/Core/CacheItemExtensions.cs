namespace Tharga.Cache.Core;

internal static class CacheItemExtensions
{
    public static bool IsValid(this CacheItem item, DateTime utcNow)
    {
        if (item == null) return false;

        var time = utcNow - (item.UpdateTime ?? item.CreateTime);
        if (time > item.FreshSpan) return false;

        return true;
    }

    /// <summary>
    /// True when both items are the same write of an entry: the same create time and the same update time.
    /// A replace gets a new create time, and a touch or an invalidate gets a new update time.
    /// The identity is only as fine as the stored clock: two writes stamped with the same tick (or the same
    /// millisecond on a backend that stores milliseconds) are taken for the same entry.
    /// </summary>
    public static bool IsSameEntryAs(this CacheItem item, CacheItem other)
    {
        if (item == null || other == null) return false;

        return item.CreateTime == other.CreateTime && item.UpdateTime == other.UpdateTime;
    }

    public static T GetData<T>(this CacheItem<T> item)
    {
        if (item == null) return default;
        return item.Data;
    }
}