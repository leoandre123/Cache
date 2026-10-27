using FluentAssertions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Moq;
using StackExchange.Redis;
using Tharga.Cache.Core;
using Tharga.Cache.Tests.Helper;
using Xunit;

namespace Tharga.Cache.Redis.Tests;

/// <summary>
/// Runs against a real Redis server, because the touch's compare-and-set and expiry are a server-side script.
/// Set the connection string in the environment variable <see cref="RedisServerFixture.ConnectionStringVariable"/>
/// to run them, preferably on a database of its own, e.g. "127.0.0.1:6379,defaultDatabase=15".
/// Only keys created by the test are written and deleted.
/// </summary>
[Trait("Category", "Integration")]
public class RedisServerTests : IClassFixture<RedisServerFixture>, IAsyncLifetime
{
    private static readonly TimeSpan FreshSpan = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan TouchDelay = TimeSpan.FromMilliseconds(1100);

    private readonly ConnectionMultiplexer _connection;
    private readonly List<string> _createdKeys = [];

    public RedisServerTests(RedisServerFixture fixture)
    {
        _connection = fixture.Connection;
    }

    public ValueTask InitializeAsync()
    {
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection == null) return;

        await Database.KeyDeleteAsync(_createdKeys.Select(x => (RedisKey)x).ToArray());
    }

    private IDatabase Database => _connection.GetDatabase();

    private Task<TimeSpan?> TimeToLiveAsync(Key key) => Database.KeyTimeToLiveAsync(key.Value);

    [Fact]
    public async Task Time_to_idle_peek_touches_once_and_renews_the_ttl()
    {
        //Arrange
        var sut = BuildCache();
        var key = NewKey();
        await sut.Cache.SetAsync(key, "value");
        await Task.Delay(TouchDelay);
        var ttlBeforeTouch = await TimeToLiveAsync(StoredKey(key));

        //Act
        var result = await sut.Cache.PeekAsync<string>(key);

        //Assert
        result.Should().Be("value");
        sut.Persist.GetCalls.Should().Be(1);
        sut.Persist.BuyMoreTimeByKeyCalls.Should().Be(0);
        sut.Persist.CompletedTouches.Should().Be(1);
        var ttlAfterTouch = await TimeToLiveAsync(StoredKey(key));
        ttlBeforeTouch.Should().NotBeNull();
        ttlAfterTouch.Should().NotBeNull("the touch must not wipe the TTL").And.BeGreaterThan(ttlBeforeTouch.Value, "a time-to-idle touch renews the TTL to the fresh span");
        ttlAfterTouch.Should().BeLessThanOrEqualTo(FreshSpan);
        (await sut.Persist.GetAsync<string>(StoredKey(key))).UpdateTime.Should().NotBeNull();
    }

    [Fact]
    public async Task Background_touch_renews_the_ttl()
    {
        //Arrange
        var sut = BuildCache(o => o.TouchInBackground = true);
        var key = NewKey();
        await sut.Cache.SetAsync(key, "value");
        await Task.Delay(TouchDelay);
        var ttlBeforeTouch = await TimeToLiveAsync(StoredKey(key));

        //Act
        await sut.Cache.PeekAsync<string>(key);

        //Assert
        await WaitUntilAsync(() => sut.Persist.CompletedTouches == 1);
        var ttlAfterTouch = await TimeToLiveAsync(StoredKey(key));
        ttlAfterTouch.Should().NotBeNull().And.BeGreaterThan(ttlBeforeTouch.Value);
    }

    [Fact]
    public async Task Touch_with_stale_while_revalidate_keeps_no_ttl()
    {
        //Arrange
        var sut = BuildCache(o => o.StaleWhileRevalidate = true);
        var key = NewKey();
        await sut.Cache.SetAsync(key, "value");

        //Act
        await sut.Cache.PeekAsync<string>(key);

        //Assert
        sut.Persist.CompletedTouches.Should().Be(1);
        (await TimeToLiveAsync(StoredKey(key))).Should().BeNull("a stale value must stay readable");
    }

    [Fact]
    public async Task Touch_of_a_value_that_does_not_round_trip_is_applied()
    {
        //Arrange
        var persist = BuildPersist();
        var key = StoredKey(NewKey());
        var createTime = DateTime.UtcNow;
        var stored = $$"""{"Data":"value","Version":2,"KeyParts":{},"CreateTime":"{{createTime:O}}","FreshSpan":"00:10:00"}""";
        await Database.StringSetAsync(key.Value, stored, FreshSpan);
        await Task.Delay(TouchDelay);
        var ttlBeforeTouch = await TimeToLiveAsync(key);
        var read = await persist.GetAsync<string>(key);

        //Act
        var touched = await persist.BuyMoreTime(key, read, false);

        //Assert
        touched.Should().BeTrue("a different shape of the stored value, e.g. from another version of the type, must not stop the touch");
        var after = await persist.GetAsync<string>(key);
        after.UpdateTime.Should().NotBeNull();
        after.Data.Should().Be("value");
        (await TimeToLiveAsync(key)).Should().NotBeNull().And.BeGreaterThan(ttlBeforeTouch.Value);
    }

    [Fact]
    public async Task Touch_with_an_item_not_read_through_redis_is_applied()
    {
        //Arrange
        var persist = BuildPersist();
        var key = StoredKey(NewKey());
        var item = BuildItem("value");
        await persist.SetAsync(key, item, false);

        //Act
        var touched = await persist.BuyMoreTime(key, item, false);

        //Assert
        touched.Should().BeTrue();
        (await persist.GetAsync<string>(key)).UpdateTime.Should().NotBeNull();
    }

    [Fact]
    public async Task Touch_with_a_replaced_item_keeps_the_newer_data()
    {
        //Arrange
        var persist = BuildPersist();
        var key = StoredKey(NewKey());
        await persist.SetAsync(key, BuildItem("first"), false);
        var read = await persist.GetAsync<string>(key);
        await persist.SetAsync(key, BuildItem("second"), false);

        //Act
        var touched = await persist.BuyMoreTime(key, read, false);

        //Assert
        touched.Should().BeFalse();
        var stored = await persist.GetAsync<string>(key);
        stored.Data.Should().Be("second");
        stored.UpdateTime.Should().BeNull();
    }

    [Fact]
    public async Task Touch_after_an_invalidate_keeps_the_entry_invalid()
    {
        //Arrange
        var persist = BuildPersist();
        var key = StoredKey(NewKey());
        await persist.SetAsync(key, BuildItem("value"), true);
        var read = await persist.GetAsync<string>(key);
        await persist.Invalidate<string>(key);

        //Act
        var touched = await persist.BuyMoreTime(key, read, true);

        //Assert
        touched.Should().BeFalse();
        (await persist.GetAsync<string>(key)).UpdateTime.Should().Be(DateTime.MinValue);
    }

    [Fact]
    public async Task Touch_of_a_dropped_key_does_not_recreate_it()
    {
        //Arrange
        var persist = BuildPersist();
        var key = StoredKey(NewKey());
        await persist.SetAsync(key, BuildItem("value"), false);
        var read = await persist.GetAsync<string>(key);
        await persist.DropAsync<string>(key);

        //Act
        var touched = await persist.BuyMoreTime(key, read, false);

        //Assert
        touched.Should().BeFalse();
        (await Database.KeyExistsAsync(key.Value)).Should().BeFalse();
    }

    [Fact]
    public async Task Touch_by_key_renews_the_ttl()
    {
        //Arrange
        var persist = BuildPersist();
        var key = StoredKey(NewKey());
        await persist.SetAsync(key, BuildItem("value"), false);
        await Task.Delay(TouchDelay);
        var ttlBeforeTouch = await TimeToLiveAsync(key);

        //Act
        var touched = await persist.BuyMoreTime<string>(key);

        //Assert
        touched.Should().BeTrue();
        var ttlAfterTouch = await TimeToLiveAsync(key);
        ttlAfterTouch.Should().NotBeNull("the touch must not wipe the TTL").And.BeGreaterThan(ttlBeforeTouch.Value, "a time-to-idle touch renews the TTL to the fresh span");
        ttlAfterTouch.Should().BeLessThanOrEqualTo(FreshSpan);
        (await persist.GetAsync<string>(key)).UpdateTime.Should().NotBeNull();
    }

    [Fact]
    public async Task Touch_by_key_does_not_add_a_ttl()
    {
        //Arrange
        var persist = BuildPersist();
        var key = StoredKey(NewKey());
        await persist.SetAsync(key, BuildItem("value"), true);

        //Act
        var touched = await persist.BuyMoreTime<string>(key);

        //Assert
        touched.Should().BeTrue();
        (await TimeToLiveAsync(key)).Should().BeNull("a stale value must stay readable");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Invalidate_keeps_the_remaining_ttl(bool staleWhileRevalidate)
    {
        //Arrange
        var persist = BuildPersist();
        var key = StoredKey(NewKey());
        await persist.SetAsync(key, BuildItem("value"), staleWhileRevalidate);
        await Task.Delay(TouchDelay);
        var ttlBeforeInvalidate = await TimeToLiveAsync(key);

        //Act
        var invalidated = await persist.Invalidate<string>(key);

        //Assert
        invalidated.Should().BeTrue();
        (await persist.GetAsync<string>(key)).UpdateTime.Should().Be(DateTime.MinValue);
        var ttl = await TimeToLiveAsync(key);
        if (staleWhileRevalidate)
        {
            ttl.Should().BeNull();
        }
        else
        {
            ttl.Should().NotBeNull("invalidate must not wipe the TTL").And.BeLessThanOrEqualTo(ttlBeforeInvalidate.Value, "invalidate must not extend the TTL");
        }
    }

    [Fact]
    public async Task Touch_by_key_of_a_missing_key_does_not_create_it()
    {
        //Arrange
        var persist = BuildPersist();
        var key = StoredKey(NewKey());

        //Act
        var touched = await persist.BuyMoreTime<string>(key);

        //Assert
        touched.Should().BeFalse();
        (await Database.KeyExistsAsync(key.Value)).Should().BeFalse();
    }

    [Fact]
    public async Task GetManyAsync_matches_GetAsync_per_key()
    {
        //Arrange
        var persist = BuildPersist();
        Key[] keys = [StoredKey(NewKey()), StoredKey(NewKey()), StoredKey(NewKey())];
        await persist.SetAsync(keys[0], BuildItem("a"), false);
        await persist.SetAsync(keys[2], BuildItem("c"), true);

        //Act
        var result = await persist.GetManyAsync<string>(keys);

        //Assert
        var expected = new List<CacheItem<string>>();
        foreach (var key in keys)
        {
            expected.Add(await persist.GetAsync<string>(key));
        }

        result.Select(x => x.Key).Should().Equal(keys);
        result.Select(x => x.CacheItem).Should().BeEquivalentTo(expected, o => o.WithStrictOrdering());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PeekManyAsync_matches_PeekAsync_per_key(bool staleWhileRevalidate)
    {
        //Arrange
        var single = BuildCache(o => o.StaleWhileRevalidate = staleWhileRevalidate);
        var many = BuildCache(o => o.StaleWhileRevalidate = staleWhileRevalidate);
        var singleKeys = await SeedAsync(single, staleWhileRevalidate);
        var manyKeys = await SeedAsync(many, staleWhileRevalidate);

        //Act
        var expected = new List<string>();
        foreach (var key in singleKeys)
        {
            expected.Add(await single.Cache.PeekAsync<string>(key));
        }

        var result = await many.Cache.PeekManyAsync<string>(manyKeys);

        //Assert
        result.Select(x => x.Data).Should().Equal(expected);
        result.Select(x => x.Data).Should().Equal("fresh", null, staleWhileRevalidate ? "stale" : null);
        many.Persist.GetManyCalls.Should().Be(1);
        many.Persist.GetCalls.Should().Be(0);
        many.Persist.CompletedTouches.Should().Be(single.Persist.CompletedTouches);
    }

    private async Task<Key[]> SeedAsync(RecordingCache sut, bool staleWhileRevalidate)
    {
        Key[] keys = [NewKey(), NewKey(), NewKey()];
        await sut.Cache.SetAsync(keys[0], "fresh");
        var staleItem = BuildItem("stale") with { CreateTime = DateTime.UtcNow.AddHours(-1) };
        await sut.Persist.SetAsync(StoredKey(keys[2]), staleItem, staleWhileRevalidate);
        return keys;
    }

    private RecordingCache BuildCache(Action<CacheTypeOptions> typeOptions = null)
    {
        return RecordingCacheFactory.Create<string>(typeOptions: o =>
        {
            o.DefaultFreshSpan = FreshSpan;
            typeOptions?.Invoke(o);
        }, innerPersist: BuildPersist);
    }

    private Redis BuildPersist(IManagedCacheMonitor cacheMonitor = null)
    {
        Assert.SkipWhen(_connection == null, $"Set {RedisServerFixture.ConnectionStringVariable} to a Redis connection string to run this test.");

        var options = Options.Create(new RedisCacheOptions { RetryCount = 0 });
        return new Redis(Mock.Of<IServiceProvider>(), Mock.Of<IHostEnvironment>(), cacheMonitor ?? Mock.Of<IManagedCacheMonitor>(), options, null, _connection);
    }

    private string NewKey()
    {
        var key = $"tharga-cache-test-{Guid.NewGuid():N}";
        _createdKeys.Add(StoredKey(key));
        return key;
    }

    private static Key StoredKey(string key)
    {
        return ((Key)key).SetTypeKey<string>();
    }

    private static CacheItem<string> BuildItem(string data)
    {
        return new CacheItem<string> { KeyParts = [], CreateTime = DateTime.UtcNow, FreshSpan = FreshSpan, Data = data };
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("The condition was not met in time.");
            await Task.Delay(10);
        }
    }
}
