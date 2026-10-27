using FluentAssertions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Moq;
using StackExchange.Redis;
using Tharga.Cache.Core;
using Xunit;

namespace Tharga.Cache.Redis.Tests;

/// <summary>
/// Runs against a real Redis server, because the expiry rules are only observable on a server.
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
}
