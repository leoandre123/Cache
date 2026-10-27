namespace Tharga.Cache;

public interface ICache
{
    event EventHandler<DataSetEventArgs> DataSetEvent;
    event EventHandler<DataGetEventArgs> DataGetEvent;
    event EventHandler<DataDropEventArgs> DataDropEvent;

    Task<T> GetAsync<T>(Key key, Func<Task<T>> fetch);
    Task<T> PeekAsync<T>(Key key);

    /// <summary>
    /// Peeks several keys at once. Each key gets exactly the result of <see cref="PeekAsync{T}"/>, including
    /// validity, stale-while-revalidate, the time-to-idle touch, events and monitoring, but the built-in caches read
    /// the persist backend in one call (a single MGET on Redis). The result has one entry per requested key, in the
    /// requested order, with default(T) for keys that are not cached.
    /// The default implementation calls <see cref="PeekAsync{T}"/> once per key.
    /// </summary>
    async Task<IReadOnlyList<(Key Key, T Data)>> PeekManyAsync<T>(IEnumerable<Key> keys)
    {
        var result = new List<(Key Key, T Data)>();
        foreach (var key in keys)
        {
            result.Add((key, await PeekAsync<T>(key)));
        }

        return result;
    }

    Task SetAsync<T>(Key key, T data);
    Task<int> DropAsync<T>(Key key);
    Task<int> InvalidateAsync<T>(Key key);
}