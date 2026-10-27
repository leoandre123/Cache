using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Moq;
using StackExchange.Redis;
using Tharga.Cache.Core;
using Xunit;

namespace Tharga.Cache.Redis.Tests;

/// <summary>
/// Asserts on the commands the Redis persist sends, against a mocked connection, so no server is needed.
/// </summary>
public class RedisRoundTripTests
{
    private const string StoredKey = "String.a";
    private static readonly TimeSpan FreshSpan = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan RemainingTimeToLive = TimeSpan.FromMinutes(3);

    private readonly Mock<IDatabase> _database = new();
    private readonly Mock<IConnectionMultiplexer> _connection = new();
    private readonly Redis _sut;

    public RedisRoundTripTests()
    {
        _connection.Setup(x => x.IsConnected).Returns(true);
        _connection.Setup(x => x.GetDatabase(It.IsAny<int>(), It.IsAny<object>())).Returns(_database.Object);
        _sut = new Redis(Mock.Of<IServiceProvider>(), Mock.Of<IHostEnvironment>(), Mock.Of<IManagedCacheMonitor>(), Options.Create(new RedisCacheOptions { RetryCount = 0 }), null, _connection.Object);
    }

    [Theory]
    [InlineData(true, 600_000)]
    [InlineData(false, null)]
    public async Task Touch_by_key_renews_a_ttl_and_never_adds_one(bool hasTimeToLive, int? expectedExpiryMilliseconds)
    {
        //Arrange
        var write = SetupStoredItem(BuildItem("a"), hasTimeToLive ? RemainingTimeToLive : null);

        //Act
        var touched = await _sut.BuyMoreTime<string>(StoredKey);

        //Assert
        touched.Should().BeTrue();
        write().Expiry.Should().Be(expectedExpiryMilliseconds.HasValue ? TimeSpan.FromMilliseconds(expectedExpiryMilliseconds.Value) : null);
        write().When.Should().Be(When.Exists, "a key dropped in between must not be recreated");
        JsonSerializer.Deserialize<CacheItem<string>>(write().Value).UpdateTime.Should().NotBeNull();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Invalidate_keeps_the_remaining_ttl(bool hasTimeToLive)
    {
        //Arrange
        var write = SetupStoredItem(BuildItem("a"), hasTimeToLive ? RemainingTimeToLive : null);

        //Act
        var invalidated = await _sut.Invalidate<string>(StoredKey);

        //Assert
        invalidated.Should().BeTrue();
        write().Expiry.Should().Be(hasTimeToLive ? RemainingTimeToLive : null, "an invalidate must neither wipe nor extend the TTL");
        JsonSerializer.Deserialize<CacheItem<string>>(write().Value).UpdateTime.Should().Be(DateTime.MinValue);
    }

    private Func<(string Value, TimeSpan? Expiry, When When)> SetupStoredItem(CacheItem<string> item, TimeSpan? timeToLive)
    {
        (string Value, TimeSpan? Expiry, When When) written = default;
        _database.Setup(x => x.StringGetAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>())).ReturnsAsync(JsonSerializer.Serialize(item));
        _database.Setup(x => x.KeyTimeToLiveAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>())).ReturnsAsync(timeToLive);
        _database.Setup(x => x.StringSetAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<TimeSpan?>(), It.IsAny<bool>(), It.IsAny<When>(), It.IsAny<CommandFlags>()))
            .Callback<RedisKey, RedisValue, TimeSpan?, bool, When, CommandFlags>((_, value, expiry, _, when, _) => written = (value, expiry, when))
            .ReturnsAsync(true);
        return () => written;
    }

    private static CacheItem<string> BuildItem(string data)
    {
        return new CacheItem<string> { KeyParts = [], CreateTime = DateTime.UtcNow, FreshSpan = FreshSpan, Data = data };
    }
}
