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
    /// <summary>
    /// Writes ARGV[2] only if the key still holds the value whose SHA1 is ARGV[1], the value the caller read.
    /// ARGV[3] is the expiry in milliseconds, or 0 for no expiry. Returns 1 if written, otherwise 0.
    /// </summary>
    private const string CompareAndSetScript = """
        local current = redis.call('GET', KEYS[1])
        if not current or redis.sha1hex(current) ~= ARGV[1] then
            return 0
        end
        local expiry = tonumber(ARGV[3])
        if expiry > 0 then
            redis.call('SET', KEYS[1], ARGV[2], 'PX', expiry)
        else
            redis.call('SET', KEYS[1], ARGV[2])
        end
        return 1
        """;

    private readonly IServiceProvider _serviceProvider;
    private readonly IHostEnvironment _hostEnvironment;
    private readonly RedisCacheOptions _options;
    private readonly ILogger<Redis> _logger;
    private readonly IAsyncPolicy _resiliencePolicy;
    private readonly RedisScriptingAvailability _scripting;
    private IConnectionMultiplexer _redisConnection;

    public Redis(IServiceProvider serviceProvider, IHostEnvironment hostEnvironment, IManagedCacheMonitor cacheMonitor, IOptions<RedisCacheOptions> options, ILogger<Redis> logger)
        : this(serviceProvider, hostEnvironment, cacheMonitor, options, logger, null, TimeProvider.System)
    {
    }

    internal Redis(IServiceProvider serviceProvider, IHostEnvironment hostEnvironment, IManagedCacheMonitor cacheMonitor, IOptions<RedisCacheOptions> options, ILogger<Redis> logger, IConnectionMultiplexer redisConnection)
        : this(serviceProvider, hostEnvironment, cacheMonitor, options, logger, redisConnection, TimeProvider.System)
    {
    }

    /// <summary>
    /// Lets tests pass a connection and control the clock that times the scripting re-probe. Not public, so
    /// dependency injection always uses the first constructor.
    /// </summary>
    internal Redis(IServiceProvider serviceProvider, IHostEnvironment hostEnvironment, IManagedCacheMonitor cacheMonitor, IOptions<RedisCacheOptions> options, ILogger<Redis> logger, IConnectionMultiplexer redisConnection, TimeProvider timeProvider)
    {
        _serviceProvider = serviceProvider;
        _hostEnvironment = hostEnvironment;
        _options = options.Value;
        _logger = logger;
        _resiliencePolicy = RedisResiliencePolicy.Create(_options, logger);
        _scripting = new RedisScriptingAvailability(timeProvider);
        _redisConnection = redisConnection;

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
            return Deserialize<T>(await db.StringGetAsync((string)key));
        });
    }

    /// <summary>
    /// Reads all keys with one MGET. On a cluster the keys are grouped by hash slot, one MGET per slot,
    /// because a multi-key command cannot span slots.
    /// </summary>
    public async Task<IReadOnlyList<(Key Key, CacheItem<T> CacheItem)>> GetManyAsync<T>(IEnumerable<Key> keys)
    {
        var requestedKeys = keys.ToArray();
        if (requestedKeys.Length == 0) return [];

        return await _resiliencePolicy.ExecuteAsync<IReadOnlyList<(Key Key, CacheItem<T> CacheItem)>>(async () =>
        {
            var redisConnection = await GetConnection();
            if (redisConnection.Multiplexer == null) return requestedKeys.Select(x => (x, (CacheItem<T>)null)).ToArray();

            var db = redisConnection.Multiplexer.GetDatabase();
            var values = new RedisValue[requestedKeys.Length];
            var slots = requestedKeys
                .Select((key, index) => (Key: (RedisKey)key.Value, Index: index))
                .GroupBy(x => redisConnection.Multiplexer.HashSlot(x.Key))
                .Select(x => x.ToArray());

            await Task.WhenAll(slots.Select(async slot =>
            {
                var read = await db.StringGetAsync(slot.Select(x => x.Key).ToArray());
                for (var i = 0; i < slot.Length; i++)
                {
                    values[slot[i].Index] = read[i];
                }
            }));

            return requestedKeys.Select((key, index) => (key, (CacheItem<T>)Deserialize<T>(values[index]))).ToArray();
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

    /// <summary>
    /// Touches without reading again: one script call that writes the item with a new update time, only if the key
    /// still holds exactly the value that was read, and with the same expiry rule as <see cref="SetAsync{T}"/>.
    /// An item that was not read through this persist does not carry the stored value to fingerprint; it is read once
    /// more and only touched if it is still the same entry. Without scripting the compare-and-set falls back to
    /// <see cref="CompareAndSetWithoutScriptAsync{T}"/>.
    /// </summary>
    public async Task<bool> BuyMoreTime<T>(Key key, CacheItem<T> cacheItem, bool staleWhileRevalidate)
    {
        return await _resiliencePolicy.ExecuteAsync(async () =>
        {
            var redisConnection = await GetConnection();
            if (redisConnection.Multiplexer == null) return false;

            var db = redisConnection.Multiplexer.GetDatabase();
            var readInThisCall = false;
            if (cacheItem is not RedisCacheItem<T> { StoredValue: not null } read)
            {
                read = Deserialize<T>(await db.StringGetAsync((string)key));
                if (!read.IsSameEntryAs(cacheItem))
                {
                    LogNotTouched(key);
                    return false;
                }

                readInThisCall = true;
            }

            var touched = read with { UpdateTime = DateTime.UtcNow };
            return await CompareAndSetAsync(redisConnection.Multiplexer, db, key, read, readInThisCall, Serialize<T>(touched), GetExpiry(read, staleWhileRevalidate));
        });
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

        // GET and PTTL are two pipelined commands in one round trip, not one atomic read; the compare-and-set below
        // only writes if the value is still the one that was read.
        var dataTask = db.StringGetAsync((string)key);
        var timeToLiveTask = db.KeyTimeToLiveAsync((string)key);
        var cacheItem = Deserialize<T>(await dataTask);
        var timeToLive = await timeToLiveTask;
        if (cacheItem == null) return false;

        var updatedCacheItem = cacheItem with { UpdateTime = updateTime };
        var expiry = GetUpdateExpiry(cacheItem, timeToLive, renewExpiry);
        return await CompareAndSetAsync(redisConnection.Multiplexer, db, key, cacheItem, true, Serialize<T>(updatedCacheItem), expiry);
    }

    /// <summary>
    /// Writes <paramref name="value"/> only if the key still holds the stored value of <paramref name="read"/>. Runs
    /// the compare-and-set script, unless the server rejects scripting: then the same call continues with the fallback,
    /// so the rejection never reaches the resilience policy (it is neither retried nor counted by the circuit breaker),
    /// and later calls on the connection go straight to the fallback.
    /// </summary>
    private async Task<bool> CompareAndSetAsync<T>(IConnectionMultiplexer connection, IDatabase db, Key key, RedisCacheItem<T> read, bool readInThisCall, string value, TimeSpan? expiry)
    {
        var expiryMilliseconds = expiry.HasValue ? Math.Max(1, (long)Math.Ceiling(expiry.Value.TotalMilliseconds)) : 0;
        bool written;
        if (_scripting.ShouldTryScript(connection))
        {
            try
            {
                var result = await db.ScriptEvaluateAsync(CompareAndSetScript, [(string)key], [read.ComputeStoredValueHash(), value, expiryMilliseconds]);
                if (_scripting.MarkAvailable()) _logger?.LogInformation("Redis runs Lua scripts again; touches and invalidates use the atomic compare-and-set script.");
                written = (int)result == 1;
            }
            catch (Exception e) when (RedisScriptingErrors.IsScriptingUnavailable(e))
            {
                LogScriptingUnavailable(connection, e);
                written = await CompareAndSetWithoutScriptAsync(db, key, read, readInThisCall, value, expiryMilliseconds);
            }
        }
        else
        {
            written = await CompareAndSetWithoutScriptAsync(db, key, read, readInThisCall, value, expiryMilliseconds);
        }

        if (!written) LogNotTouched(key);
        return written;
    }

    /// <summary>
    /// The compare-and-set for a server that does not run Lua: the stored value is compared on the client, then
    /// written with <c>SET XX</c> and the same expiry as the script. When the value was read in this call, that read is
    /// the comparison and it is not read again. Not atomic: a write by another client between the read and the <c>SET</c> is overwritten with the
    /// value that was read, plus the new update time. <c>XX</c> still means a key that was dropped is not created again.
    /// </summary>
    private static async Task<bool> CompareAndSetWithoutScriptAsync<T>(IDatabase db, Key key, RedisCacheItem<T> read, bool readInThisCall, string value, long expiryMilliseconds)
    {
        if (!readInThisCall)
        {
            var current = await db.StringGetAsync((string)key);
            if (current.IsNull || !((byte[])current).AsSpan().SequenceEqual(read.StoredValue)) return false;
        }

        var expiry = expiryMilliseconds > 0 ? TimeSpan.FromMilliseconds(expiryMilliseconds) : (TimeSpan?)null;
        return await db.StringSetAsync((string)key, value, expiry, false, When.Exists);
    }

    private void LogScriptingUnavailable(IConnectionMultiplexer connection, Exception exception)
    {
        if (_scripting.MarkUnavailable(connection))
        {
            _logger?.LogWarning("Redis rejects Lua scripting ({Message}). Touches and invalidates use a non-atomic GET and SET XX fallback instead of the compare-and-set script; scripting is tried again every {ReprobeInterval}.", exception.Message, RedisScriptingAvailability.ReprobeInterval);
        }
        else
        {
            _logger?.LogDebug("Redis still rejects Lua scripting ({Message}); the fallback stays in use.", exception.Message);
        }
    }

    private void LogNotTouched(Key key)
    {
        _logger?.LogDebug("Redis key '{Key}' was not updated: it was replaced, invalidated or removed after it was read.", key.Value);
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

    private static RedisCacheItem<T> Deserialize<T>(RedisValue value)
    {
        if (value.IsNullOrEmpty) return null;

        var cacheItem = JsonSerializer.Deserialize<RedisCacheItem<T>>((string)value);
        return cacheItem == null ? null : cacheItem with { StoredValue = (byte[])value };
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