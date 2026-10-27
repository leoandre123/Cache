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

### Lua scripting is required

The time-to-idle touch, and `InvalidateAsync` on a type with stale-while-revalidate, run a Lua script on the server (`EVAL`/`EVALSHA`). The connecting user must be allowed to run scripts (ACL category `@scripting`). Proxies and Redis-compatible servers that block Lua or do not implement it cannot run these operations. Reads, writes, drops, `PeekManyAsync` and invalidates without stale-while-revalidate do not use scripting.

If scripting is not available:

- A time-to-idle hit with fail-open (`FailOpenOnBackendError`, the default) still returns the value, but logs a warning on every hit, and the entry is never renewed: it expires its fresh span after it was written. The failed script is retried first (`RetryCount`, with backoff), which adds that delay to every hit unless `TouchInBackground` is set.
- A time-to-idle hit without fail-open throws; with `TouchInBackground` the failure is logged as an error instead.
- The failures count toward the circuit breaker. `CircuitBreakerFailureThreshold` failed calls in a row open the circuit, and every Redis call then fails fast for `CircuitBreakerDuration`.
- `InvalidateAsync` with stale-while-revalidate throws, with or without fail-open.

### Keys without a TTL after upgrading

Earlier versions removed the key's expiry when they touched or invalidated it, so those keys have no TTL. Upgrading does not reclaim them: a time-to-idle hit sets the expiry again, but a key that is never read again stays until it is deleted. Clean them up once after the upgrade; keys are named `<TypeName>.<key>`, and a deleted entry is loaded again on the next miss:

```bash
redis-cli -n 0 --scan --pattern 'SessionData.*' | while read -r key; do
  [ "$(redis-cli -n 0 TTL "$key")" = "-1" ] && redis-cli -n 0 DEL "$key"
done
```

Eternal entries, entries without a fresh span and types with stale-while-revalidate never have a TTL, so limit the pattern to time-to-live and time-to-idle types, or accept that those entries are loaded once more.

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
