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
- On an `ITimeToIdleCache` hit, the touch is one script call. It writes the entry with a new update time and renews the expiry, but only if the key still holds exactly the value that was read (the script compares a SHA1 of the stored value, so a different shape of the type in another deployment does not stop the touch). A value replaced or invalidated in between is left alone. Set `TouchInBackground` on the type to run the touch off the caller's path. A server that does not run Lua scripts gets a non-atomic fallback; see below.
- `InvalidateAsync` keeps the remaining expiry; it neither removes nor extends it.
- `PeekManyAsync` reads all keys with a single `MGET`, or one per hash slot on a cluster.

## Servers without Lua scripting

The time-to-idle touch, and `InvalidateAsync` on a type with stale-while-revalidate, run a Lua script on the server (`EVAL`/`EVALSHA`), so the compare and the write are one atomic step. Not every server allows that: an ACL user without the `@scripting` category, a proxy or managed service that does not implement `EVAL`/`EVALSHA`, or a Redis-compatible server without Lua. Reads, writes, drops, `PeekManyAsync` and invalidates without stale-while-revalidate (they delete the key) do not use scripting.

When the server rejects the script, the persist switches to a fallback within the same call, so the touch or invalidate still succeeds:

- **Detection** is by the server's error: `NOPERM` for `EVAL`/`EVALSHA`/`SCRIPT`, an unknown or unsupported `EVAL`/`EVALSHA` command, a reply that says Lua or scripting is disabled or not supported, or a client command map that disables `EVAL`/`EVALSHA`. `NOSCRIPT` (StackExchange.Redis loads the script again), timeouts and connection errors do not count; they go through the retry and circuit-breaker policy as before.
- **No retry, no breaker.** The rejection is handled before the resilience policy sees it: it is not retried and does not count toward the circuit breaker. A warning is logged once.
- **Remembered per connection.** Later calls go straight to the fallback, without a failed script call first. Every 10 minutes one call tries the script again; if the server allows it by then, an information message is logged and the script is used again. A new connection starts with the script.
- **Fallback.** The current value is read (`GET`, pipelined with `PTTL` where the remaining expiry is needed; a value already read in the same call is not read again), compared on the client with the value that was read, and only if it is unchanged written with `SET ... XX` and the same expiry the script would set.

The fallback is **not atomic**. Between the read and the `SET XX` there is one round trip in which another client can write the key. Such a write (a new value, an invalidate, another touch) is then overwritten by the value that was read plus the new update time: a concurrent update can be lost, and an entry invalidated in that window can be valid again until it is next refreshed or expires. `XX` still guarantees that a key that was dropped or has expired is never created again. If that window matters, allow scripting for the connecting user (for example `ACL SETUSER <user> +@scripting`).

## Keys without a TTL after upgrading

Earlier versions rewrote the key with a plain `SET` on a touch or an invalidate, which removed its expiry. Keys that an earlier version touched or invalidated therefore have no TTL, and upgrading does not reclaim them: a time-to-idle hit sets the expiry again, but a key that is never read again stays until it is deleted. Clean them up once after the upgrade. Keys are named `<TypeName>.<key>`, and a deleted entry is loaded again from the source on the next miss:

```bash
redis-cli -n 0 --scan --pattern 'SessionData.*' | while read -r key; do
  [ "$(redis-cli -n 0 TTL "$key")" = "-1" ] && redis-cli -n 0 DEL "$key"
done
```

Eternal entries, entries without a fresh span and types with stale-while-revalidate never have a TTL, so limit the pattern to time-to-live and time-to-idle types, or accept that those entries are loaded once more.

## Documentation

Full documentation, configuration options, and samples are available on the [GitHub project page](https://github.com/Tharga/Cache).

## License

MIT
