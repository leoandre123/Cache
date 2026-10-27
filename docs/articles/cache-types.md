# Cache types

Tharga.Cache exposes four cache interfaces, each with a different expiration strategy. Inject the one that matches the lifetime your data needs — they share the same `ICache` operations (see [Common operations](getting-started.md#common-operations)) and differ only in when items expire.

| Interface | Expiration | Lifetime | Typical use |
|-----------|------------|----------|-------------|
| `IEternalCache` | Never (until explicitly removed) | Singleton | Reference data that rarely changes |
| `ITimeToLiveCache` | Fixed time after insertion (TTL) | Singleton | API responses, computed results |
| `ITimeToIdleCache` | Reset on every access (TTI) | Singleton | Session-like data kept alive while in use |
| `IScopeCache` | End of the DI scope | Scoped | Per-request memoization |

## IEternalCache

Data never expires unless explicitly dropped or invalidated. Registered as a singleton.

```csharp
public class UserService(IEternalCache cache)
{
    public Task<User> GetUserAsync(string userId) =>
        cache.GetAsync<User>(userId, () => LoadUserAsync(userId));
}
```

## ITimeToLiveCache

Data expires a fixed time after insertion (TTL). Registered as a singleton.

```csharp
var data = await ttlCache.GetAsync<Product>(
    "product-123",
    () => LoadProductAsync(123),
    TimeSpan.FromMinutes(10));
```

## ITimeToIdleCache

The expiration clock resets every time the item is accessed (TTI). Useful for session-like data that should stay cached while it is actively being used and expire once it goes quiet.

```csharp
var session = await ttiCache.GetAsync<SessionData>(
    "session-abc",
    () => LoadSessionAsync("abc"),
    TimeSpan.FromMinutes(30));
```

Each hit "touches" the item to reset its clock. The touch reuses the item that was just read, so a hit is one read plus one write. With the built-in Memory, MongoDB and Redis backends the touch only applies if the stored item is still the one that was read, so a value replaced or invalidated in between is never overwritten; the File backend gives that guarantee within one process. A custom `IPersist` that does not implement the item-aware `BuyMoreTime` overload touches whatever is stored. On Redis the touch also renews the key's TTL to the fresh span.

To keep the write off the caller's path, opt in per type; the read then returns after a single backend read, and a failed touch is logged instead of thrown. At most one background touch per key is pending at a time, and none are started after the cache is disposed:

```csharp
o.RegisterType<SessionData, IRedis>(t => t.TouchInBackground = true);
```

For hot keys, `MinTimeBetweenTouches` skips the touch of an item that was written or touched less than that long ago. That saves a write per hit, at the cost of precision: the item can expire up to that much earlier than its idle time after the last hit.

```csharp
o.RegisterType<SessionData, IRedis>(t => t.MinTimeBetweenTouches = TimeSpan.FromSeconds(30));
```

On Redis the touch runs an atomic Lua script. A server that does not allow scripting gets a non-atomic `SET XX` fallback instead; see [Persistence backends](persistence-backends.md#redis).

**Custom persists and test doubles.** A hit on an item read from the persist calls the item-aware `IPersist.BuyMoreTime<T>(key, item, staleWhileRevalidate)`; `BuyMoreTime<T>(key)` is only called when there is no read copy (after a load). A strict `IPersist` mock needs a setup for the item-aware overload. A loose mock returns its default (`false`) for it without running the interface's default implementation, so setups and verifications on `BuyMoreTime<T>(key)` no longer see those hits.

## IScopeCache

A scoped cache instance, cleared at the end of the DI scope (for example, per HTTP request). Data never expires within the scope, which makes it a clean way to memoize a value that may be requested several times while handling a single request.

```csharp
var result = await scopeCache.GetAsync<RequestContext>(
    "current-context",
    () => BuildContextAsync());
```

## Stale-while-revalidate

For the time-based caches, enabling `StaleWhileRevalidate` returns expired data immediately while fresh data is fetched in the background — eliminating the latency spike of a cache miss.

```csharp
o.RegisterType<Product, IMemory>(t =>
{
    t.StaleWhileRevalidate = true;
    t.DefaultFreshSpan = TimeSpan.FromMinutes(5);
});
```

Use `GetWithCallbackAsync` to be notified when the fresh value arrives:

```csharp
var (data, isFresh) = await cache.GetWithCallbackAsync<Product>(
    "product-123",
    () => LoadProductAsync(123),
    async freshData =>
    {
        // Called when the background refresh completes
        await NotifyClientsAsync(freshData);
    },
    TimeSpan.FromMinutes(5));

if (!isFresh)
{
    // data is stale; the callback will fire when fresh data is ready
}
```

## Events

All cache types raise events for observing activity:

```csharp
cache.DataSetEvent  += (sender, args) => Console.WriteLine($"Cached: {args.Key}");
cache.DataGetEvent  += (sender, args) => Console.WriteLine($"Retrieved: {args.Key}");
cache.DataDropEvent += (sender, args) => Console.WriteLine($"Removed: {args.Key}");
```
