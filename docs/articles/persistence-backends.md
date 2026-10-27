# Persistence backends

By default every type is cached in memory (`IMemory`). You can assign a different backend per type so that data survives a process restart or is shared across instances. Each backend ships in its own package; the choice is made at registration time with `RegisterType<T, TBackend>()`.

| Backend | Interface | Package |
|---------|-----------|---------|
| In-memory (default) | `IMemory` | Tharga.Cache |
| Redis | `IRedis` | Tharga.Cache.Redis |
| MongoDB | `IMongoDB` | Tharga.Cache.MongoDB |
| File | `IFile` | Tharga.Cache.File |

> `IMemoryWithRedis` is deprecated. Use `IRedis` or `IMemory` explicitly instead.

## Redis

```
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

Entries are written with an expiry of their fresh span (none with stale-while-revalidate or without a fresh span). The time-to-idle touch renews it and an invalidate keeps what is left. `PeekManyAsync` reads all keys with a single `MGET` (one per hash slot on a cluster).

## MongoDB

```
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

## File

```
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

## Mixing backends

Different types can use different backends in the same application — register each one against the backend that fits it:

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

Types you never call `RegisterType` for fall back to `IMemory`.
