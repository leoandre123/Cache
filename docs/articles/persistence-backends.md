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

### Servers without Lua scripting

The time-to-idle touch, and `InvalidateAsync` on a type with stale-while-revalidate, run a Lua script on the server (`EVAL`/`EVALSHA`), so the compare and the write are one atomic step. Not every server allows that: an ACL user without the `@scripting` category, a proxy or managed service that does not implement `EVAL`/`EVALSHA`, or a Redis-compatible server without Lua. Reads, writes, drops, `PeekManyAsync` and invalidates without stale-while-revalidate do not use scripting.

When the server rejects the script, the persist switches to a fallback within the same call, so the touch or invalidate still succeeds:

- Detection is by the server's error: `NOPERM` for `EVAL`/`EVALSHA`/`SCRIPT`, an unknown or unsupported `EVAL`/`EVALSHA` command, a reply that says Lua or scripting is disabled or not supported, or a client command map that disables `EVAL`/`EVALSHA`. `NOSCRIPT`, timeouts and connection errors do not count and go through the retry and circuit-breaker policy as before.
- The rejection is not retried and does not count toward the circuit breaker. A warning is logged once.
- It is remembered per connection, so later calls go straight to the fallback. Every 10 minutes one call tries the script again, and the script is used again once the server allows it.
- The fallback reads the current value (`GET`, with `PTTL` where the remaining expiry is needed; a value already read in the same call is not read again), compares it on the client with the value that was read, and only if it is unchanged writes it with `SET ... XX` and the same expiry the script would set.

The fallback is **not atomic**: a write by another client in the round trip between the read and the `SET XX` is overwritten, so a concurrent update or invalidate can be lost. `XX` still guarantees that a dropped or expired key is never created again. Allow scripting for the connecting user (`+@scripting`) if that window matters.

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
