# Tharga Cache

[![GitHub repo Issues](https://img.shields.io/github/issues/Tharga/Cache?style=flat&logo=github&logoColor=red&label=Issues)](https://github.com/Tharga/Cache/issues?q=is%3Aopen)
[![NuGet](https://img.shields.io/nuget/v/Tharga.Cache)](https://www.nuget.org/packages/Tharga.Cache)
![Nuget](https://img.shields.io/nuget/dt/Tharga.Cache)
[![License](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

A flexible .NET caching library with multiple cache strategies, pluggable persistence backends, eviction policies, and a Blazor monitoring UI.

📖 **Documentation:** [cache.tharga.net](https://cache.tharga.net)

## Packages

| Package | Description | NuGet |
|---------|-------------|-------|
| **Tharga.Cache** | Core library with in-memory caching | [![NuGet](https://img.shields.io/nuget/v/Tharga.Cache)](https://www.nuget.org/packages/Tharga.Cache) |
| **Tharga.Cache.Redis** | Redis persistence backend | [![NuGet](https://img.shields.io/nuget/v/Tharga.Cache.Redis)](https://www.nuget.org/packages/Tharga.Cache.Redis) |
| **Tharga.Cache.MongoDB** | MongoDB persistence backend | [![NuGet](https://img.shields.io/nuget/v/Tharga.Cache.MongoDB)](https://www.nuget.org/packages/Tharga.Cache.MongoDB) |
| **Tharga.Cache.File** | File-based persistence backend | [![NuGet](https://img.shields.io/nuget/v/Tharga.Cache.File)](https://www.nuget.org/packages/Tharga.Cache.File) |
| **Tharga.Cache.Blazor** | Blazor monitoring UI components | [![NuGet](https://img.shields.io/nuget/v/Tharga.Cache.Blazor)](https://www.nuget.org/packages/Tharga.Cache.Blazor) |
| **Tharga.Cache.Mcp** | MCP (Model Context Protocol) provider | [![NuGet](https://img.shields.io/nuget/v/Tharga.Cache.Mcp)](https://www.nuget.org/packages/Tharga.Cache.Mcp) |

## Quick Start

Install the core package:

```bash
dotnet add package Tharga.Cache
```

Register the cache in your DI container:

```csharp
builder.Services.AddCache();
```

Inject a cache and use it:

```csharp
public class WeatherService
{
    private readonly ITimeToLiveCache _cache;

    public WeatherService(ITimeToLiveCache cache)
    {
        _cache = cache;
    }

    public async Task<WeatherForecast[]> GetForecastAsync()
    {
        return await _cache.GetAsync<WeatherForecast[]>(
            "weather-forecast",
            () => LoadFromApiAsync(),
            TimeSpan.FromMinutes(5));
    }
}
```

The first call loads data via the fetch delegate. Subsequent calls within the fresh span return the cached value without calling the delegate.

## Cache Types

Four cache interfaces are available, each registered as a different DI lifetime or expiration strategy:

### IEternalCache

Data never expires unless explicitly removed. Registered as a singleton.

```csharp
public class UserService
{
    private readonly IEternalCache _cache;

    public UserService(IEternalCache cache)
    {
        _cache = cache;
    }

    public async Task<User> GetUserAsync(string userId)
    {
        return await _cache.GetAsync<User>(userId, () => LoadUserAsync(userId));
    }
}
```

### ITimeToLiveCache

Data expires a fixed time after insertion (TTL). Registered as a singleton.

```csharp
var data = await _ttlCache.GetAsync<Product>(
    "product-123",
    () => LoadProductAsync(123),
    TimeSpan.FromMinutes(10));
```

### ITimeToIdleCache

The expiration clock resets every time the item is accessed (TTI). Useful for session-like data that should stay cached while actively used.

```csharp
var session = await _ttiCache.GetAsync<SessionData>(
    "session-abc",
    () => LoadSessionAsync("abc"),
    TimeSpan.FromMinutes(30));
```

### IScopeCache

A scoped cache instance, cleared at the end of the DI scope (e.g., per HTTP request). Data never expires within the scope.

```csharp
var result = await _scopeCache.GetAsync<RequestContext>(
    "current-context",
    () => BuildContextAsync());
```

## Common Cache Operations

All cache types share these operations from `ICache`:

```csharp
// Get or load data
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

Time-based caches (`ITimeToLiveCache`, `ITimeToIdleCache`) also support:

```csharp
// Get with explicit fresh span
var data = await timeCache.GetAsync<MyData>("key", () => FetchAsync(), TimeSpan.FromMinutes(5));

// Set with explicit fresh span
await timeCache.SetAsync<MyData>("key", myData, TimeSpan.FromHours(1));
```

## Persistence Backends

By default, all data is cached in memory (`IMemory`). You can configure specific types to use a different backend.

> **Note:** `IMemoryWithRedis` is deprecated. Use `IRedis` or `IMemory` explicitly instead.

### Redis

```bash
dotnet add package Tharga.Cache.Redis
```

```csharp
builder.Services.AddCache(o =>
{
    o.AddRedisDBOptions(r =>
        r.ConnectionStringLoader = sp => "localhost:6379");

    o.RegisterType<SessionData, IRedis>();
});
```

### MongoDB

```bash
dotnet add package Tharga.Cache.MongoDB
```

```csharp
builder.Services.AddCache(o =>
{
    o.AddMongoDBOptions(m =>
    {
        m.CollectionName = "_cache";
        m.ConfigurationName = "Default";
    });

    o.RegisterType<AuditLog, IMongoDB>();
});
```

### File

```bash
dotnet add package Tharga.Cache.File
```

```csharp
builder.Services.AddCache(o =>
{
    o.AddFileDBOptions(f =>
    {
        f.CompanyName = "MyCompany";
        f.AppName = "MyApp";
        f.Format = Format.Json; // Json, Base64, GZip, or Brotli
    });

    o.RegisterType<Settings, IFile>();
});
```

### Mixing Backends

You can register different types with different backends in the same application:

```csharp
builder.Services.AddCache(o =>
{
    o.AddRedisDBOptions(r => r.ConnectionStringLoader = sp => "localhost:6379");
    o.AddMongoDBOptions();

    o.RegisterType<SessionData, IRedis>();
    o.RegisterType<AuditLog, IMongoDB>();
    o.RegisterType<WeatherForecast[], IMemory>();
});
```

## Configuration

### Per-Type Options

Use `RegisterType` to configure behavior for specific cached types:

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

### Global Options

```csharp
builder.Services.AddCache(o =>
{
    o.MaxConcurrentFetchCount = 20;          // Max parallel background fetches (default: 10)
    o.WatchDogInterval = TimeSpan.FromMinutes(2); // Stale cleanup interval (default: 60s)

    o.Default = new CacheTypeOptions           // Defaults applied to all types
    {
        DefaultFreshSpan = TimeSpan.FromSeconds(30)
    };
});
```

### Eviction Policies

When `MaxCount` or `MaxSize` is exceeded, items are evicted according to the configured policy:

| Policy | Description |
|--------|-------------|
| `FirstInFirstOut` | Removes the oldest items first (default) |
| `LeastRecentlyUsed` | Removes items that haven't been accessed recently |
| `RandomReplacement` | Removes items at random (lowest overhead) |

### Size Constants

Use the `Size` helper for readable byte values:

```csharp
t.MaxSize = Size.MB * 500;   // 500 MB
t.MaxSize = Size.GB * 2;     // 2 GB
```

Available constants: `Size.KB`, `Size.MB`, `Size.GB`, `Size.TB`.

## Stale-While-Revalidate

When enabled, expired data is returned immediately while fresh data is fetched in the background. This eliminates latency spikes caused by cache misses.

```csharp
o.RegisterType<Product, IMemory>(t =>
{
    t.StaleWhileRevalidate = true;
    t.DefaultFreshSpan = TimeSpan.FromMinutes(5);
});
```

Use `GetWithCallbackAsync` to be notified when the fresh data arrives:

```csharp
var (data, isFresh) = await _cache.GetWithCallbackAsync<Product>(
    "product-123",
    () => LoadProductAsync(123),
    async freshData =>
    {
        // Called when background refresh completes
        await NotifyClientsAsync(freshData);
    },
    TimeSpan.FromMinutes(5));

if (!isFresh)
{
    // data is stale, callback will fire when fresh data is ready
}
```

## Key Building

Cache keys can be simple strings or built from multiple parts using `KeyBuilder`:

```csharp
// Simple string key (implicit conversion)
Key key = "my-cache-key";

// Composite key from multiple parts
var key = KeyBuilder
    .Set("userId", userId)
    .Set("department", department);

var data = await _cache.GetAsync<UserProfile>(key, () => LoadProfileAsync(userId, department));
```

## Events

All cache types expose events for observing cache activity:

```csharp
cache.DataSetEvent += (sender, args) =>
{
    Console.WriteLine($"Cached: {args.Key}");
};

cache.DataGetEvent += (sender, args) =>
{
    Console.WriteLine($"Retrieved: {args.Key}");
};

cache.DataDropEvent += (sender, args) =>
{
    Console.WriteLine($"Removed: {args.Key}");
};
```

## Monitoring

### ICacheMonitor

Inject `ICacheMonitor` to inspect cache state programmatically:

```csharp
public class CacheHealthCheck
{
    private readonly ICacheMonitor _monitor;

    public CacheHealthCheck(ICacheMonitor monitor)
    {
        _monitor = monitor;
    }

    public void PrintStats()
    {
        foreach (var typeInfo in _monitor.GetInfos())
        {
            Console.WriteLine($"{typeInfo.Type.Name}: {typeInfo.Items.Count} items");
        }

        Console.WriteLine($"Fetch queue: {_monitor.GetFetchQueueCount()}");
    }

    public void Cleanup()
    {
        _monitor.ClearStale(); // Remove expired items
        _monitor.ClearAll();   // Remove everything
    }
}
```

### Blazor UI

Add the Blazor monitoring package for a visual dashboard:

```bash
dotnet add package Tharga.Cache.Blazor
```

Use the components in your Blazor pages:

```razor
@page "/cache"
@rendermode InteractiveServer

<Tharga.Cache.Blazor.SummaryView />
<Tharga.Cache.Blazor.ListView />
```

- **SummaryView** shows total item count, total size, fetch queue depth, and a "Clear Cache" button.
- **ListView** shows a hierarchical grid of all cached types and their items with details like key, size, access count, staleness, and expiration.

## Samples

The repository includes sample projects demonstrating different scenarios:

- **Sample/Tharga.Cache.WebApi** — ASP.NET Core Web API with multiple backends
- **Sample/Tharga.Cache.BlazorServer** — Blazor Server with monitoring UI
- **Sample/Tharga.Cache.Console** — Console app with file persistence

## License

This project is licensed under the MIT License. See [LICENSE](LICENSE) for details.
