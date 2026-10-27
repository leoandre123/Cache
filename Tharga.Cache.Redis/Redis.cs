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
    private IConnectionMultiplexer _redisConnection;

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

    internal Redis(IServiceProvider serviceProvider, IHostEnvironment hostEnvironment, IManagedCacheMonitor cacheMonitor, IOptions<RedisCacheOptions> options, ILogger<Redis> logger, IConnectionMultiplexer redisConnection)
        : this(serviceProvider, hostEnvironment, cacheMonitor, options, logger)
    {
        _redisConnection = redisConnection;
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

    public IAsyncEnumerable<(Key Key, CacheItem<T> CacheItem)> FindAsync<T>(Key key)
    {
        throw new NotImplementedException();
    }

    public async Task SetAsync<T>(Key key, CacheItem<T> cacheItem, bool staleWhileRevalidate)
    {
        await _resiliencePolicy.ExecuteAsync(async () =>
        {
            var item = Serialize(cacheItem);

            var redisConnection = await GetConnection();
            if (redisConnection.Multiplexer == null) return;

            var db = redisConnection.Multiplexer.GetDatabase();
            var expiry = GetExpiry(cacheItem, staleWhileRevalidate);
            if (expiry == null)
            {
                await db.StringSetAsync((string)key, item);
            }
            else
            {
                await db.StringSetAsync((string)key, item, expiry, false);
            }
        });
    }

    public async Task<bool> BuyMoreTime<T>(Key key)
    {
        return await _resiliencePolicy.ExecuteAsync(async () => await SetUpdateTime<T>(key, DateTime.UtcNow, renewExpiry: true));
    }

    public async Task<bool> Invalidate<T>(Key key)
    {
        return await _resiliencePolicy.ExecuteAsync(async () => await SetUpdateTime<T>(key, DateTime.MinValue, renewExpiry: false));
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

    /// <summary>
    /// Read-and-write of the update time. The persist does not know the type's stale-while-revalidate flag here, so the
    /// expiry follows the key's current state: a key without a TTL keeps having none, and a key with a TTL gets its
    /// fresh span again when <paramref name="renewExpiry"/> is set (a touch) or keeps what is left otherwise (an
    /// invalidate, which must not extend the entry's life).
    /// </summary>
    private async Task<bool> SetUpdateTime<T>(Key key, DateTime updateTime, bool renewExpiry)
    {
        var redisConnection = await GetConnection();
        if (redisConnection.Multiplexer == null) return false;

        var db = redisConnection.Multiplexer.GetDatabase();

        // GET and PTTL are two pipelined commands in one round trip, not one atomic read.
        var dataTask = db.StringGetAsync((string)key);
        var timeToLiveTask = db.KeyTimeToLiveAsync((string)key);
        var data = (await dataTask).ToString();
        var timeToLive = await timeToLiveTask;
        if (string.IsNullOrEmpty(data)) return false;

        var cacheItem = JsonSerializer.Deserialize<CacheItem<T>>(data);
        var updatedCacheItem = cacheItem with { UpdateTime = updateTime };
        var expiry = GetUpdateExpiry(cacheItem, timeToLive, renewExpiry);
        return await db.StringSetAsync((string)key, Serialize(updatedCacheItem), expiry, false, When.Exists);
    }

    private static TimeSpan? GetUpdateExpiry(CacheItem cacheItem, TimeSpan? timeToLive, bool renewExpiry)
    {
        if (timeToLive == null) return null;
        if (!renewExpiry) return timeToLive;

        return GetExpiry(cacheItem, false) ?? timeToLive;
    }

    /// <summary>
    /// The expiry rule for a write: none for items without a fresh span, for eternal items and with
    /// stale-while-revalidate (the stale value must stay readable); otherwise the fresh span from now.
    /// </summary>
    private static TimeSpan? GetExpiry(CacheItem cacheItem, bool staleWhileRevalidate)
    {
        if (cacheItem.FreshSpan == null || cacheItem.FreshSpan == TimeSpan.MaxValue || staleWhileRevalidate) return null;

        return cacheItem.FreshSpan;
    }

    private static string Serialize<T>(CacheItem<T> cacheItem)
    {
        var item = JsonSerializer.Serialize(cacheItem);
        if (Debugger.IsAttached)
        {
            var convertedBack = JsonSerializer.Deserialize<CacheItem<T>>(item);
            var itemAgain = JsonSerializer.Serialize(convertedBack);
            if (itemAgain != item) throw new InvalidOperationException("Failed to serialize/deserialize back to same result.");
        }

        return item;
    }

    private async Task<(IConnectionMultiplexer Multiplexer, string Message)> GetConnection()
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