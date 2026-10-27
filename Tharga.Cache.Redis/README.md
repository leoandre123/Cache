# Tharga.Cache.Redis

[![NuGet](https://img.shields.io/nuget/v/Tharga.Cache.Redis)](https://www.nuget.org/packages/Tharga.Cache.Redis)
![Nuget](https://img.shields.io/nuget/dt/Tharga.Cache.Redis)
[![License](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

Redis persistence backend for [Tharga.Cache](https://www.nuget.org/packages/Tharga.Cache). Store cached data in Redis for sharing across instances or surviving application restarts.

## Get Started

```bash
dotnet add package Tharga.Cache
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

Any type registered with `IRedis` is persisted to Redis. Unregistered types default to in-memory caching.

## Why Redis?

- **Shared cache** across multiple application instances
- **Survives restarts** — cached data is not lost on deploy
- **High throughput** with low latency

## Resilience (fail-open)

If Redis becomes unreachable, the cache **fails open**: a backend read error is treated as a miss so the
call falls through to your source loader, and a backend write error is swallowed. A cache outage therefore
never faults the caller as long as the source of truth is healthy. This is on by default and can be turned
off with `CacheOptions.FailOpenOnBackendError = false` (which restores the previous throwing behavior).

A Polly **circuit breaker** sits in front of the Redis connection so a sustained outage short-circuits
immediately instead of paying retry latency on every call (which is what prevents thread-pool starvation).
The breaker recovers automatically once Redis is healthy again.

```csharp
o.AddRedisDBOptions(r =>
{
    r.ConnectionStringLoader = sp => "localhost:6379";
    r.RetryCount = 3;                                     // transient-error retries before a call fails (default 3)
    r.CircuitBreakerFailureThreshold = 5;                 // consecutive failures before the circuit opens (default 5)
    r.CircuitBreakerDuration = TimeSpan.FromSeconds(30);  // how long it stays open before probing again (default 30s)
    r.CommandTimeout = TimeSpan.FromSeconds(1);           // optional shorter per-command timeout for fast fail-open
});
```

## Expiry, time-to-idle and batch reads

- Entries are written with a Redis expiry of their fresh span. There is no expiry for entries without a fresh span, for eternal entries, or with stale-while-revalidate, because the stale value must stay readable.
- On an `ITimeToIdleCache` hit, the touch is one script call. It writes the entry with a new update time and renews the expiry, but only if the key still holds exactly the value that was read (the script compares a SHA1 of the stored value, so a different shape of the type in another deployment does not stop the touch). A value replaced or invalidated in between is left alone. Set `TouchInBackground` on the type to run the touch off the caller's path.
- `InvalidateAsync` keeps the remaining expiry; it neither removes nor extends it.
- `PeekManyAsync` reads all keys with a single `MGET`, or one per hash slot on a cluster.

## Documentation

Full documentation, configuration options, and samples are available on the [GitHub project page](https://github.com/Tharga/Cache).

## License

MIT
