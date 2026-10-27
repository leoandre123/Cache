using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Diagnostics;
using System.Text.Json;
using MongoDB.Driver;
using Tharga.Cache.Core;
using Tharga.MongoDB;

namespace Tharga.Cache.MongoDB;

internal class MongoDB : IMongoDB
{
    private readonly ILogger<MongoDB> _logger;
    private readonly ICollectionProvider _collectionProvider;
    private readonly MongoDBCacheOptions _options;

    public MongoDB(ICollectionProvider collectionProvider, IManagedCacheMonitor cacheMonitor, IOptions<MongoDBCacheOptions> options, ILogger<MongoDB> logger)
    {
        _collectionProvider = collectionProvider;
        _logger = logger;
        _options = options.Value;

        cacheMonitor.RequestEvictEvent += async (_, e) =>
        {
            var dropAsyncMethod = typeof(MongoDB)
                .GetMethod("DropAsync")!
                .MakeGenericMethod(e.Type);

            var task = (Task)dropAsyncMethod.Invoke(this, [e.Key])!;
            await task;

            cacheMonitor.Drop(e.Type, e.Key);
        };
    }

    public async Task<CacheItem<T>> GetAsync<T>(Key key)
    {
        var collection = GetCollection();

        var item = await collection.GetOneAsync(key.Value);
        return await ToCacheItemAsync<T>(collection, key, item);
    }

    /// <summary>
    /// Reads all keys with one find on the ids ($in).
    /// </summary>
    public async Task<IReadOnlyList<(Key Key, CacheItem<T> CacheItem)>> GetManyAsync<T>(IEnumerable<Key> keys)
    {
        var requestedKeys = keys.ToArray();
        if (requestedKeys.Length == 0) return [];

        var collection = GetCollection();
        var keyById = requestedKeys.DistinctBy(x => x.Value).ToDictionary(x => x.Value);
        var ids = keyById.Keys.ToArray();

        var entities = new List<CacheEntity>();
        await foreach (var entity in collection.GetAsync(new FilterDefinitionBuilder<CacheEntity>().In(x => x.Id, ids)))
        {
            entities.Add(entity);
        }

        var items = new Dictionary<string, CacheItem<T>>();
        foreach (var entity in entities)
        {
            if (!keyById.TryGetValue(entity.Id, out var key)) continue;

            items[entity.Id] = await ToCacheItemAsync<T>(collection, key, entity);
        }

        return requestedKeys.Select(x => (x, items.GetValueOrDefault(x.Value))).ToArray();
    }

    private static async Task<CacheItem<T>> ToCacheItemAsync<T>(ICacheRepositoryCollection collection, Key key, CacheEntity item)
    {
        if (item == null) return null;

        if (!item.StaleWhileRevalidate && item.FreshSpan.HasValue && item.FreshSpan.Value != TimeSpan.MaxValue && item.CreateTime.Add(item.FreshSpan.Value) < DateTime.UtcNow)
        {
            await collection.DeleteOneAsync(ById(item.Id));
            return null;
        }

        return new CacheItem<T>()
        {
            KeyParts = key.KeyParts,
            CreateTime = item.CreateTime,
            Data = JsonSerializer.Deserialize<T>(item.Data),
            FreshSpan = item.FreshSpan,
            UpdateTime = item.UpdateTime,
            LoadDuration = item.LoadDuration,
        };
    }

    public IAsyncEnumerable<(Key Key, CacheItem<T> CacheItem)> FindAsync<T>(Key key)
    {
        throw new NotImplementedException();
    }

    public async Task SetAsync<T>(Key key, CacheItem<T> cacheItem, bool staleWhileRevalidate)
    {
        var item = JsonSerializer.Serialize(cacheItem);
        if (Debugger.IsAttached)
        {
            var convertedBack = JsonSerializer.Deserialize<CacheItem<T>>(item);
            var itemAgain = JsonSerializer.Serialize(convertedBack);
            if (itemAgain != item) throw new InvalidOperationException("Failed to serialize/deserialize back to same result.");
        }

        var entity = new CacheEntity
        {
            Id = key,
            Type = typeof(T).AssemblyQualifiedName,
            Data = JsonSerializer.Serialize(cacheItem.Data),
            CreateTime = cacheItem.CreateTime,
            FreshSpan = cacheItem.FreshSpan,
            UpdateTime = cacheItem.UpdateTime,
            StaleWhileRevalidate = staleWhileRevalidate,
            LoadDuration = cacheItem.LoadDuration
        };

        var collection = GetCollection();
        await collection.AddOrReplaceAsync(entity);
    }

    public Task<bool> BuyMoreTime<T>(Key key)
    {
        return SetUpdateTime(key, DateTime.UtcNow);
    }

    /// <summary>
    /// Sets only the update time, and only on the entry that was read (same create time and update time), so a
    /// concurrent replace or invalidate is left alone. One round trip, no read.
    /// </summary>
    public async Task<bool> BuyMoreTime<T>(Key key, CacheItem<T> cacheItem, bool staleWhileRevalidate)
    {
        var filterBuilder = new FilterDefinitionBuilder<CacheEntity>();
        var filter = filterBuilder.And(ById(key.Value), filterBuilder.Eq(x => x.CreateTime, cacheItem.CreateTime), filterBuilder.Eq(x => x.UpdateTime, cacheItem.UpdateTime));
        var update = new UpdateDefinitionBuilder<CacheEntity>().Set(x => x.UpdateTime, DateTime.UtcNow);

        var collection = GetCollection();
        var result = await collection.UpdateOneAsync(filter, update, OneOption<CacheEntity>.SingleOrDefault);
        return result.Before != null;
    }

    public Task<bool> Invalidate<T>(Key key)
    {
        return SetUpdateTime(key, DateTime.MinValue);
    }

    public async Task<bool> DropAsync<T>(Key key)
    {
        var collection = GetCollection();
        var item = await collection.DeleteOneAsync(ById(key.Value));
        return item != null;
    }

    /// <summary>
    /// The id of a cache entity as an explicit filter.
    /// </summary>
    /// <remarks>
    /// There is no DeleteOneAsync(TKey) overload, so a bare string is picked up by
    /// DeleteOneAsync(FilterDefinition&lt;CacheEntity&gt;) through the driver's implicit
    /// string -&gt; JsonFilterDefinition conversion. That compiles, and then Mongo parses the
    /// cache key as a JSON filter document: "JSON reader was expecting a value but found 'MyType'".
    /// Building the filter here means the key can only ever be read as an id.
    /// </remarks>
    private static FilterDefinition<CacheEntity> ById(string id)
    {
        return new FilterDefinitionBuilder<CacheEntity>().Eq(x => x.Id, id);
    }

    private async Task<bool> SetUpdateTime(Key key, DateTime updateTime)
    {
        var collection = GetCollection();
        var update = new UpdateDefinitionBuilder<CacheEntity>().Set(x => x.UpdateTime, updateTime);
        var result = await collection.UpdateOneAsync(ById(key.Value), update, OneOption<CacheEntity>.SingleOrDefault);
        return result.Before != null;
    }

    public async Task<(bool Success, string Message)> CanConnectAsync()
    {
        try
        {
            var collection = GetCollection();
            var cnt = await collection.CountAsync(x => true);
            return (true, $"There {(cnt == 1 ? "is" : "are")} {cnt} record{(cnt == 1 ? "" : "s")} cached.");
        }
        catch (Exception e)
        {
            _logger?.LogError(e, e.Message);
            return (false, e.Message);
        }
    }

    private ICacheRepositoryCollection GetCollection()
    {
        var databaseContext = new DatabaseContext
        {
            CollectionName = _options.CollectionName,
            ConfigurationName = _options.ConfigurationName
        };
        var collection = _collectionProvider.GetCollection<ICacheRepositoryCollection, CacheEntity, string>(databaseContext);
        return collection;
    }

    public void Dispose()
    {
    }

    public ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }
}

