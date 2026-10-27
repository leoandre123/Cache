namespace Tharga.Cache;

public interface ICache
{
    event EventHandler<DataSetEventArgs> DataSetEvent;
    event EventHandler<DataGetEventArgs> DataGetEvent;
    event EventHandler<DataDropEventArgs> DataDropEvent;

    Task<T> GetAsync<T>(Key key, Func<Task<T>> fetch);
    Task<T> PeekAsync<T>(Key key);

    /// <summary>
    /// Peeks several keys with one backend read (one MGET on Redis). Each key gets the same result as <see cref="PeekAsync{T}"/>,
    /// in the requested order, with default(T) for keys that are not cached.
    /// </summary>
    async Task<IReadOnlyList<(Key Key, T Data)>> PeekManyAsync<T>(IEnumerable<Key> keys)
    {
        var result = new List<(Key Key, T Data)>();
        foreach (var key in keys) result.Add((key, await PeekAsync<T>(key)));
        return result;
    }
    Task SetAsync<T>(Key key, T data);
    Task<int> DropAsync<T>(Key key);
    Task<int> InvalidateAsync<T>(Key key);
}