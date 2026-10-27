# Getting started

## Install

```
dotnet add package Tharga.Cache
```

The core package caches in memory and has no external dependencies. Add a backend package only when you want a type to persist outside the process — see [Persistence backends](persistence-backends.md).

## Register

```csharp
builder.Services.AddCache();
```

`AddCache` is idempotent — calling it more than once (for example when several libraries each register cache types) merges the registrations instead of throwing.

The merge is scoped to the service collection it is called on, so it is safe to build several hosts concurrently in one process — parallel integration tests each constructing a `WebApplicationFactory`, or a multi-tenant host spinning up isolated containers. Registrations made on one service collection never appear in another.

## The get-or-load pattern

Inject one of the four cache interfaces and call `GetAsync` with a key and a fetch delegate. The first call runs the delegate and stores the result; subsequent calls within the fresh span return the cached value without invoking the delegate.

```csharp
public class WeatherService(ITimeToLiveCache cache)
{
    public Task<WeatherForecast[]> GetForecastAsync() =>
        cache.GetAsync<WeatherForecast[]>(
            "weather-forecast",
            () => LoadFromApiAsync(),
            TimeSpan.FromMinutes(5));
}
```

Pick the interface that matches the lifetime you need — `IEternalCache`, `ITimeToLiveCache`, `ITimeToIdleCache`, or `IScopeCache`. See [Cache types](cache-types.md).

## Common operations

Every cache type shares these operations from `ICache`:

```csharp
// Get or load
var data = await cache.GetAsync<MyData>("key", () => FetchDataAsync());

// Peek without triggering a load (returns default if not cached)
var cached = await cache.PeekAsync<MyData>("key");

// Peek many keys with one backend read (one MGET on Redis)
var many = await cache.PeekManyAsync<MyData>(["key-1", "key-2"]);

// Manually set a value
await cache.SetAsync<MyData>("key", myData);

// Remove a specific item
await cache.DropAsync<MyData>("key");

// Mark as stale (triggers reload on next access)
await cache.InvalidateAsync<MyData>("key");
```

Time-based caches (`ITimeToLiveCache`, `ITimeToIdleCache`) also take an explicit fresh span:

```csharp
var data = await timeCache.GetAsync<MyData>("key", () => FetchAsync(), TimeSpan.FromMinutes(5));
await timeCache.SetAsync<MyData>("key", myData, TimeSpan.FromHours(1));
```

> The no-span `SetAsync<T>(key, data)` overload requires `DefaultFreshSpan` to be configured on the type registration. The explicit-span overload works unconditionally.

## Configuration

### Per-type options

Use `RegisterType` to configure behavior for a specific cached type:

```csharp
builder.Services.AddCache(o =>
{
    o.RegisterType<Product, IMemory>(t =>
    {
        t.DefaultFreshSpan = TimeSpan.FromMinutes(10);
        t.StaleWhileRevalidate = true;
        t.MaxCount = 1000;
        t.MaxSize = Size.MB * 100;
        t.EvictionPolicy = EvictionPolicy.LeastRecentlyUsed;
    });
});
```

| Option | Default | Description |
|--------|---------|-------------|
| `DefaultFreshSpan` | `null` | Default TTL when not specified per call |
| `StaleWhileRevalidate` | `false` | Return stale data immediately while refreshing in the background |
| `TouchInBackground` | `false` | For `ITimeToIdleCache`: touch after a hit in the background, so the read returns after a single backend read |
| `ReturnDefaultOnFirstLoad` | `false` | Return `default(T)` on first cache miss instead of blocking; factory runs in the background |
| `MaxCount` | `null` | Maximum number of cached items for this type |
| `MaxSize` | `null` | Maximum total size in bytes for this type |
| `EvictionPolicy` | `FirstInFirstOut` | Strategy when `MaxCount` or `MaxSize` is exceeded |

### Global options

```csharp
builder.Services.AddCache(o =>
{
    o.MaxConcurrentFetchCount = 20;               // Max parallel background fetches (default: 10)
    o.WatchDogInterval = TimeSpan.FromMinutes(2); // Stale-cleanup interval (default: 60s)

    o.Default = new CacheTypeOptions             // Defaults applied to all types
    {
        DefaultFreshSpan = TimeSpan.FromSeconds(30)
    };
});
```

### Eviction policies

When `MaxCount` or `MaxSize` is exceeded, items are evicted according to the configured policy:

| Policy | Description |
|--------|-------------|
| `FirstInFirstOut` | Removes the oldest items first (default) |
| `LeastRecentlyUsed` | Removes items that haven't been accessed recently |
| `RandomReplacement` | Removes items at random (lowest overhead) |

### Size constants

Use the `Size` helper for readable byte values — `Size.KB`, `Size.MB`, `Size.GB`, `Size.TB`:

```csharp
t.MaxSize = Size.MB * 500;   // 500 MB
t.MaxSize = Size.GB * 2;     // 2 GB
```

## Key building

Cache keys can be simple strings or composed from multiple parts with `KeyBuilder`:

```csharp
// Simple string key (implicit conversion)
Key key = "my-cache-key";

// Composite key from multiple parts
var key = KeyBuilder
    .Set("userId", userId)
    .Set("department", department);

var data = await cache.GetAsync<UserProfile>(key, () => LoadProfileAsync(userId, department));
```

## Next steps

- [Cache types](cache-types.md) — choose the right expiration strategy.
- [Persistence backends](persistence-backends.md) — persist types to Redis, MongoDB, or disk.
- [Monitoring](monitoring.md) — inspect and manage the cache at runtime.
