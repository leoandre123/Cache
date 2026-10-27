using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly;
using Polly.CircuitBreaker;
using StackExchange.Redis;
using Tharga.Cache.Core;

namespace Tharga.Cache.Redis;

internal class Redis : IRedis
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IHostEnvironment _hostEnvironment;
    private readonly RedisCacheOptions _options;
    private readonly ILogger<Redis> _logger;
    private readonly IAsyncPolicy _resiliencePolicy;
    private ConnectionMultiplexer _redisConnection;

    public Redis(IServiceProvider serviceProvider, IHostEnvironment hostEnvironment, IManagedCacheMonitor cacheMonitor, IOptions<RedisCacheOptions> options, ILogger<Redis> logger)
    {
        _serviceProvider = serviceProvider;
        _hostEnvironment = hostEnvironment;
        _options = options.Value;
        _logger = logger;
        _resiliencePolicy = RedisResiliencePolicy.Create(_options, logger);

        cacheMonitor.RequestEvictEvent += async (_, e) =>
        {
            var dropAsyncMethod = typeof(Redis)
                .GetMethod("DropAsync")!
                .MakeGenericMethod(e.Type);

            var task = (Task)dropAsyncMethod.Invoke(this, [e.Key])!;
            await task;

            cacheMonitor.Drop(e.Type, e.Key);
        };
    }

    public async Task<CacheItem<T>> GetAsync<T>(Key key)
    {
        return await _resiliencePolicy.ExecuteAsync(async () =>
        {
            var redisConnection = await GetConnection();
            if (redisConnection.Multiplexer == null) return null;

            var db = redisConnection.Multiplexer.GetDatabase();
            var data = (await db.StringGetAsync((string)key)).ToString();
            if (!string.IsNullOrEmpty(data))
            {
                var cacheItem = JsonSerializer.Deserialize<CacheItem<T>>(data);
                return cacheItem;
            }

            return null;
        });
    }

    public async Task<IReadOnlyList<(Key Key, CacheItem<T> CacheItem)>> GetManyAsync<T>(IEnumerable<Key> keys)
    {
        var requestedKeys = keys.ToArray();
        var items = new CacheItem<T>[requestedKeys.Length];
        await _resiliencePolicy.ExecuteAsync(async () =>
        {
            var redisConnection = await GetConnection();
            if (redisConnection.Multiplexer == null) return;

            var db = redisConnection.Multiplexer.GetDatabase();
            foreach (var slot in requestedKeys.Select((key, index) => (Key: (RedisKey)(string)key, Index: index)).GroupBy(x => redisConnection.Multiplexer.HashSlot(x.Key)))
            {
                var entries = slot.ToArray();
                var values = await db.StringGetAsync(entries.Select(x => x.Key).ToArray());
                for (var i = 0; i < entries.Length; i++)
                {
                    items[entries[i].Index] = values[i].IsNullOrEmpty ? null : JsonSerializer.Deserialize<CacheItem<T>>(values[i].ToString());
                }
            }
        });

        return requestedKeys.Select((key, index) => (key, items[index])).ToArray();
    }

    public IAsyncEnumerable<(Key Key, CacheItem<T> CacheItem)> FindAsync<T>(Key key)
    {
        throw new NotImplementedException();
    }

    public async Task SetAsync<T>(Key key, CacheItem<T> cacheItem, bool staleWhileRevalidate)
    {
        await _resiliencePolicy.ExecuteAsync(async () =>
        {
            var item = JsonSerializer.Serialize(cacheItem);
            if (Debugger.IsAttached)
            {
                var convertedBack = JsonSerializer.Deserialize<CacheItem<T>>(item);
                var itemAgain = JsonSerializer.Serialize(convertedBack);
                if (itemAgain != item) throw new InvalidOperationException("Failed to serialize/deserialize back to same result.");
            }

            var redisConnection = await GetConnection();
            if (redisConnection.Multiplexer == null) return;

            var db = redisConnection.Multiplexer.GetDatabase();
            await db.StringSetAsync((string)key, item, GetExpiry(cacheItem, staleWhileRevalidate), false);
        });
    }

    public async Task<bool> BuyMoreTime<T>(Key key)
    {
        return await _resiliencePolicy.ExecuteAsync(async () => await SetUpdateTime<T>(key, DateTime.UtcNow));
    }

    public async Task<bool> BuyMoreTime<T>(Key key, CacheItem<T> cacheItem, bool staleWhileRevalidate)
    {
        return await _resiliencePolicy.ExecuteAsync(async () =>
        {
            var redisConnection = await GetConnection();
            if (redisConnection.Multiplexer == null) return false;

            var item = JsonSerializer.Serialize(cacheItem with { UpdateTime = DateTime.UtcNow });
            return await redisConnection.Multiplexer.GetDatabase().StringSetAsync((string)key, item, GetExpiry(cacheItem, staleWhileRevalidate), When.Exists);
        });
    }

    public async Task<bool> Invalidate<T>(Key key)
    {
        return await _resiliencePolicy.ExecuteAsync(async () => await SetUpdateTime<T>(key, DateTime.MinValue));
    }

    public async Task<bool> DropAsync<T>(Key key)
    {
        return await _resiliencePolicy.ExecuteAsync(async () =>
        {
            var redisConnection = await GetConnection();
            if (redisConnection.Multiplexer == null) return false;

            var db = redisConnection.Multiplexer.GetDatabase();
            var result = await db.KeyDeleteAsync((string)key);
            return result;
        });
    }

    public async Task<(bool Success, string Message)> CanConnectAsync()
    {
        try
        {
            return await _resiliencePolicy.ExecuteAsync(async () =>
            {
                var redisConnection = await GetConnection();
                if (redisConnection.Multiplexer == null) return (false, redisConnection.Message);

                return (redisConnection.Multiplexer.IsConnected, redisConnection.Message);
            });
        }
        catch (BrokenCircuitException e)
        {
            return (false, $"Redis circuit is open: {e.Message}");
        }
    }

    private async Task<bool> SetUpdateTime<T>(Key key, DateTime updateTime)
    {
        var redisConnection = await GetConnection();
        if (redisConnection.Multiplexer == null) return false;

        var db = redisConnection.Multiplexer.GetDatabase();
        var dataTask = db.StringGetAsync((string)key);
        var timeToLive = await db.KeyTimeToLiveAsync((string)key);
        var data = (await dataTask).ToString();
        if (!string.IsNullOrEmpty(data))
        {
            var cacheItem = JsonSerializer.Deserialize<CacheItem<T>>(data);
            var updatedCacheItem = cacheItem with { UpdateTime = updateTime };
            var item = JsonSerializer.Serialize(updatedCacheItem);
            if (Debugger.IsAttached)
            {
                var convertedBack = JsonSerializer.Deserialize<CacheItem<T>>(item);
                var itemAgain = JsonSerializer.Serialize(convertedBack);
                if (itemAgain != item) throw new InvalidOperationException("Failed to serialize/deserialize back to same result.");
            }

            var expiry = timeToLive == null || updateTime == DateTime.MinValue ? timeToLive : cacheItem.FreshSpan ?? timeToLive;
            return await db.StringSetAsync((string)key, item, expiry, When.Exists);
        }

        return false;
    }

    private static TimeSpan? GetExpiry(CacheItem cacheItem, bool staleWhileRevalidate)
    {
        return cacheItem.FreshSpan == TimeSpan.MaxValue || staleWhileRevalidate ? null : cacheItem.FreshSpan;
    }

    private async Task<(ConnectionMultiplexer Multiplexer, string Message)> GetConnection()
    {
        if (_redisConnection?.IsConnected ?? false) return (_redisConnection, "Connected (Cached).");

        var connectionString = _options.ConnectionStringLoader?.Invoke(_serviceProvider);
        if (string.IsNullOrEmpty(connectionString))
        {
            if (!_hostEnvironment.IsDevelopment()) _logger?.LogWarning("No connection string set for distributed cache.");
            return (null, "No connection string.");
        }

        if (string.Equals(connectionString, "DISABLED", StringComparison.InvariantCultureIgnoreCase)) return (null, "Cache is Disabled.");

        try
        {
            if (_options.CommandTimeout is { } commandTimeout)
            {
                var config = ConfigurationOptions.Parse(connectionString);
                var milliseconds = (int)commandTimeout.TotalMilliseconds;
                config.AsyncTimeout = milliseconds;
                config.SyncTimeout = milliseconds;
                config.ConnectTimeout = milliseconds;
                _redisConnection = await ConnectionMultiplexer.ConnectAsync(config);
            }
            else
            {
                _redisConnection = await ConnectionMultiplexer.ConnectAsync(connectionString);
            }

            return (_redisConnection, "Connected to Redis.");
        }
        catch (Exception e)
        {
            _logger?.LogError(e, e.Message);
            if (_redisConnection != null) await _redisConnection.DisposeAsync();
            _redisConnection = null;
            return (null, e.Message);
        }
    }

    public void Dispose()
    {
        _redisConnection?.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (_redisConnection != null) await _redisConnection.DisposeAsync();
    }
}