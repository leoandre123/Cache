using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
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

    /// <summary>
    /// The hash slot a non-cluster connection gives every key (StackExchange.Redis' internal ServerSelectionStrategy.NoSlot).
    /// </summary>
    private const int NoSlot = -1;
    private static readonly TimeSpan FreshSpan = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan RemainingTimeToLive = TimeSpan.FromMinutes(3);

    private readonly Mock<IDatabase> _database = new();
    private readonly Mock<IConnectionMultiplexer> _connection = new();
    private readonly Mock<ILogger<Redis>> _logger = new();
    private readonly Redis _sut;

    public RedisRoundTripTests()
    {
        _connection.Setup(x => x.IsConnected).Returns(true);
        _connection.Setup(x => x.GetDatabase(It.IsAny<int>(), It.IsAny<object>())).Returns(_database.Object);
        _sut = new Redis(Mock.Of<IServiceProvider>(), Mock.Of<IHostEnvironment>(), Mock.Of<IManagedCacheMonitor>(), Options.Create(new RedisCacheOptions { RetryCount = 0 }), _logger.Object, _connection.Object);
    }

    [Fact]
    public async Task GetManyAsync_reads_all_keys_with_one_MGET()
    {
        //Arrange
        var item = BuildItem("a", TimeSpan.FromMinutes(1));
        RedisKey[] requested = null;
        _connection.Setup(x => x.HashSlot(It.IsAny<RedisKey>())).Returns(NoSlot);
        _database.Setup(x => x.StringGetAsync(It.IsAny<RedisKey[]>(), It.IsAny<CommandFlags>()))
            .Callback<RedisKey[], CommandFlags>((keys, _) => requested = keys)
            .ReturnsAsync([JsonSerializer.Serialize(item), RedisValue.Null, JsonSerializer.Serialize(item with { Data = "c" })]);

        //Act
        var result = await _sut.GetManyAsync<string>(["String.a", "String.b", "String.c"]);

        //Assert
        _database.Verify(x => x.StringGetAsync(It.IsAny<RedisKey[]>(), It.IsAny<CommandFlags>()), Times.Once);
        _database.Verify(x => x.StringGetAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()), Times.Never);
        requested.Select(x => (string)x).Should().Equal("String.a", "String.b", "String.c");
        result.Select(x => x.Key.Value).Should().Equal("String.a", "String.b", "String.c");
        result.Select(x => x.CacheItem?.Data).Should().Equal("a", null, "c");
    }

    [Fact]
    public async Task GetManyAsync_sends_one_MGET_per_cluster_hash_slot()
    {
        //Arrange
        _connection.Setup(x => x.HashSlot(It.IsAny<RedisKey>())).Returns<RedisKey>(x => (string)x == "String.b" ? 2 : 1);
        _database.Setup(x => x.StringGetAsync(It.IsAny<RedisKey[]>(), It.IsAny<CommandFlags>()))
            .Returns<RedisKey[], CommandFlags>((keys, _) => Task.FromResult(keys.Select(x => (RedisValue)JsonSerializer.Serialize(BuildItem((string)x, null))).ToArray()));

        //Act
        var result = await _sut.GetManyAsync<string>(["String.a", "String.b", "String.c"]);

        //Assert
        _database.Verify(x => x.StringGetAsync(It.IsAny<RedisKey[]>(), It.IsAny<CommandFlags>()), Times.Exactly(2));
        result.Select(x => x.CacheItem.Data).Should().Equal("String.a", "String.b", "String.c");
    }

    [Fact]
    public async Task Touch_after_GetManyAsync_does_not_read_again()
    {
        //Arrange
        var stored = JsonSerializer.Serialize(BuildItem("a", FreshSpan));
        _connection.Setup(x => x.HashSlot(It.IsAny<RedisKey>())).Returns(NoSlot);
        _database.Setup(x => x.StringGetAsync(It.IsAny<RedisKey[]>(), It.IsAny<CommandFlags>())).ReturnsAsync([stored]);
        var script = SetupScript(1);
        var read = (await _sut.GetManyAsync<string>([StoredKey])).Single().CacheItem;

        //Act
        var touched = await _sut.BuyMoreTime(StoredKey, read, false);

        //Assert
        touched.Should().BeTrue();
        _database.Verify(x => x.StringGetAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()), Times.Never);
        script().Expected.Should().Be(Sha1(stored));
    }

    [Theory]
    [InlineData(true, 600_000)]
    [InlineData(false, 0)]
    public async Task Touch_by_key_renews_a_ttl_and_never_adds_one(bool hasTimeToLive, long expectedExpiryMilliseconds)
    {
        //Arrange
        var stored = JsonSerializer.Serialize(BuildItem("a", FreshSpan));
        SetupStoredValue(stored, hasTimeToLive ? RemainingTimeToLive : null);
        var script = SetupScript(1);

        //Act
        var touched = await _sut.BuyMoreTime<string>(StoredKey);

        //Assert
        touched.Should().BeTrue();
        script().Expected.Should().Be(Sha1(stored), "the write only applies if the key still holds the value that was read");
        script().ExpiryMilliseconds.Should().Be(expectedExpiryMilliseconds);
        JsonSerializer.Deserialize<CacheItem<string>>(script().Value).UpdateTime.Should().NotBeNull();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Invalidate_keeps_the_remaining_ttl(bool hasTimeToLive)
    {
        //Arrange
        SetupStoredValue(JsonSerializer.Serialize(BuildItem("a", FreshSpan)), hasTimeToLive ? RemainingTimeToLive : null);
        var script = SetupScript(1);

        //Act
        var invalidated = await _sut.Invalidate<string>(StoredKey);

        //Assert
        invalidated.Should().BeTrue();
        script().ExpiryMilliseconds.Should().Be(hasTimeToLive ? (long)RemainingTimeToLive.TotalMilliseconds : 0, "an invalidate must neither wipe nor extend the TTL");
        JsonSerializer.Deserialize<CacheItem<string>>(script().Value).UpdateTime.Should().Be(DateTime.MinValue);
    }

    [Theory]
    [InlineData(600_000, false, 600_000)]
    [InlineData(600_000, true, 0)]
    [InlineData(null, false, 0)]
    [InlineData(-1, false, 0)]
    public async Task Touch_with_the_read_item_is_one_compare_and_set_with_the_set_expiry_rule(int? freshSpanMilliseconds, bool staleWhileRevalidate, long expectedExpiryMilliseconds)
    {
        //Arrange
        var freshSpan = freshSpanMilliseconds switch
        {
            null => (TimeSpan?)null,
            -1 => TimeSpan.MaxValue,
            _ => TimeSpan.FromMilliseconds(freshSpanMilliseconds.Value)
        };
        var stored = JsonSerializer.Serialize(BuildItem("a", freshSpan));
        SetupStoredValue(stored, null);
        var script = SetupScript(1);
        var read = await _sut.GetAsync<string>(StoredKey);

        //Act
        var touched = await _sut.BuyMoreTime(StoredKey, read, staleWhileRevalidate);

        //Assert
        touched.Should().BeTrue();
        _database.Verify(x => x.ScriptEvaluateAsync(It.IsAny<string>(), It.IsAny<RedisKey[]>(), It.IsAny<RedisValue[]>(), It.IsAny<CommandFlags>()), Times.Once);
        _database.Verify(x => x.StringGetAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()), Times.Once, "the touch must not read the item again");
        script().Expected.Should().Be(Sha1(stored), "the touch only applies if the stored value is the one that was read");
        JsonSerializer.Deserialize<CacheItem<string>>(script().Value).Should().BeEquivalentTo(read, o => o.Excluding(x => x.UpdateTime));
        JsonSerializer.Deserialize<CacheItem<string>>(script().Value).UpdateTime.Should().NotBeNull();
        script().ExpiryMilliseconds.Should().Be(expectedExpiryMilliseconds);
    }

    [Fact]
    public async Task Touch_compares_the_stored_value_and_not_a_new_serialization_of_the_item()
    {
        //Arrange
        var item = BuildItem("a", FreshSpan);
        var stored = $$"""{"Data":"a","Version":2,"KeyParts":{},"CreateTime":"{{item.CreateTime:O}}","FreshSpan":"00:10:00"}""";
        SetupStoredValue(stored, null);
        var script = SetupScript(1);
        var read = await _sut.GetAsync<string>(StoredKey);

        //Act
        var touched = await _sut.BuyMoreTime(StoredKey, read, false);

        //Assert
        Sha1(JsonSerializer.Serialize<CacheItem<string>>(read)).Should().NotBe(Sha1(stored), "this test needs a stored value that does not round-trip");
        touched.Should().BeTrue();
        script().Expected.Should().Be(Sha1(stored));
    }

    [Fact]
    public async Task Touch_with_an_item_not_read_through_redis_reads_once_and_compares_the_stored_value()
    {
        //Arrange
        var item = BuildItem("a", FreshSpan);
        var stored = JsonSerializer.Serialize(item);
        SetupStoredValue(stored, null);
        var script = SetupScript(1);

        //Act
        var touched = await _sut.BuyMoreTime(StoredKey, item, false);

        //Assert
        touched.Should().BeTrue();
        _database.Verify(x => x.StringGetAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()), Times.Once);
        script().Expected.Should().Be(Sha1(stored));
        script().ExpiryMilliseconds.Should().Be((long)FreshSpan.TotalMilliseconds);
    }

    [Fact]
    public async Task Touch_with_an_item_not_read_through_redis_leaves_a_replaced_entry_alone()
    {
        //Arrange
        var item = BuildItem("a", FreshSpan);
        SetupStoredValue(JsonSerializer.Serialize(item with { CreateTime = item.CreateTime.AddTicks(1), Data = "b" }), null);
        SetupScript(1);

        //Act
        var touched = await _sut.BuyMoreTime(StoredKey, item, false);

        //Assert
        touched.Should().BeFalse();
        _database.Verify(x => x.ScriptEvaluateAsync(It.IsAny<string>(), It.IsAny<RedisKey[]>(), It.IsAny<RedisValue[]>(), It.IsAny<CommandFlags>()), Times.Never);
    }

    [Fact]
    public async Task Touch_reports_false_and_logs_at_debug_when_the_stored_value_changed()
    {
        //Arrange
        SetupStoredValue(JsonSerializer.Serialize(BuildItem("a", FreshSpan)), null);
        SetupScript(0);
        var read = await _sut.GetAsync<string>(StoredKey);

        //Act
        var touched = await _sut.BuyMoreTime(StoredKey, read, false);

        //Assert
        touched.Should().BeFalse();
        _logger.Verify(x => x.Log(LogLevel.Debug, It.IsAny<EventId>(), It.IsAny<It.IsAnyType>(), It.IsAny<Exception>(), It.IsAny<Func<It.IsAnyType, Exception, string>>()), Times.Once);
    }

    private void SetupStoredValue(string stored, TimeSpan? timeToLive)
    {
        _database.Setup(x => x.StringGetAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>())).ReturnsAsync(stored);
        _database.Setup(x => x.KeyTimeToLiveAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>())).ReturnsAsync(timeToLive);
    }

    private Func<(string Expected, string Value, long ExpiryMilliseconds)> SetupScript(int result)
    {
        RedisValue[] arguments = null;
        _database.Setup(x => x.ScriptEvaluateAsync(It.IsAny<string>(), It.IsAny<RedisKey[]>(), It.IsAny<RedisValue[]>(), It.IsAny<CommandFlags>()))
            .Callback<string, RedisKey[], RedisValue[], CommandFlags>((_, _, values, _) => arguments = values)
            .ReturnsAsync(RedisResult.Create(result));
        return () => ((string)arguments[0], (string)arguments[1], (long)arguments[2]);
    }

    private static string Sha1(string value)
    {
        return Convert.ToHexStringLower(SHA1.HashData(Encoding.UTF8.GetBytes(value)));
    }

    private static CacheItem<string> BuildItem(string data, TimeSpan? freshSpan)
    {
        return new CacheItem<string> { KeyParts = [], CreateTime = DateTime.UtcNow, FreshSpan = freshSpan, Data = data };
    }
}
