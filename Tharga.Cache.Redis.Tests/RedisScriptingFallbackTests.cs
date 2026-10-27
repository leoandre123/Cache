using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using StackExchange.Redis;
using Tharga.Cache.Core;
using Tharga.Cache.Tests.Helper;
using Xunit;

namespace Tharga.Cache.Redis.Tests;

/// <summary>
/// The compare-and-set against a mocked connection whose server rejects Lua scripting: the persist must switch to
/// the GET and SET XX fallback within the same call, without the resilience policy seeing the rejection.
/// </summary>
public class RedisScriptingFallbackTests
{
    private const string StoredKey = "String.a";
    private const string NoPermission = "NOPERM User tharga has no permissions to run the 'evalsha' command";
    private const string UnknownCommand = "ERR unknown command 'EVALSHA', with args beginning with: 'abc' '1' 'String.a'";
    private static readonly TimeSpan FreshSpan = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan RemainingTimeToLive = TimeSpan.FromMinutes(3);

    private readonly Mock<IDatabase> _database = new();
    private readonly Mock<IConnectionMultiplexer> _connection = new();
    private readonly Mock<ILogger<Redis>> _logger = new();
    private readonly ManualTimeProvider _clock = new();
    private readonly List<(TimeSpan? Expiry, When When, string Value)> _writes = [];

    public RedisScriptingFallbackTests()
    {
        _connection.Setup(x => x.IsConnected).Returns(true);
        _connection.Setup(x => x.GetDatabase(It.IsAny<int>(), It.IsAny<object>())).Returns(_database.Object);
        SetupSetIfExists(true);
    }

    [Fact]
    public async Task Touch_falls_back_to_SET_XX_when_the_server_rejects_scripting_and_is_not_retried()
    {
        //Arrange
        var sut = BuildSut(retryCount: 3);
        var stored = JsonSerializer.Serialize(BuildItem("a", FreshSpan));
        SetupStoredValue(stored, null);
        SetupScriptThrows(RedisErrors.Server(NoPermission));
        var read = await sut.GetAsync<string>(StoredKey);

        //Act
        var touched = await sut.BuyMoreTime(StoredKey, read, false);

        //Assert
        touched.Should().BeTrue();
        VerifyScriptCalls(Times.Once(), "the rejection must not reach the retry policy");
        _database.Verify(x => x.StringGetAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()), Times.Exactly(2), "the fallback reads the stored value once to compare it");
        var write = _writes.Should().ContainSingle().Subject;
        write.When.Should().Be(When.Exists, "XX never creates a key that was dropped");
        write.Expiry.Should().Be(FreshSpan);
        JsonSerializer.Deserialize<CacheItem<string>>(write.Value).Should().BeEquivalentTo(read, o => o.Excluding(x => x.UpdateTime));
        JsonSerializer.Deserialize<CacheItem<string>>(write.Value).UpdateTime.Should().NotBeNull();
    }

    [Fact]
    public async Task Later_touches_go_straight_to_the_fallback_and_the_switch_is_logged_once()
    {
        //Arrange
        var sut = BuildSut();
        SetupStoredValue(JsonSerializer.Serialize(BuildItem("a", FreshSpan)), null);
        SetupScriptThrows(RedisErrors.Server(NoPermission));
        var read = await sut.GetAsync<string>(StoredKey);

        //Act
        var first = await sut.BuyMoreTime(StoredKey, read, false);
        var second = await sut.BuyMoreTime(StoredKey, read, false);
        var byKey = await sut.BuyMoreTime<string>(StoredKey);

        //Assert
        first.Should().BeTrue();
        second.Should().BeTrue();
        byKey.Should().BeTrue();
        VerifyScriptCalls(Times.Once(), "scripting is remembered as unavailable");
        _writes.Should().HaveCount(3);
        VerifyLogged(LogLevel.Warning, Times.Once());
    }

    [Fact]
    public async Task Rejected_scripting_does_not_count_toward_the_circuit_breaker()
    {
        //Arrange
        var sut = BuildSut(retryCount: 0, circuitBreakerFailureThreshold: 1);
        SetupStoredValue(JsonSerializer.Serialize(BuildItem("a", FreshSpan)), null);
        SetupScriptThrows(RedisErrors.Server(UnknownCommand));
        var read = await sut.GetAsync<string>(StoredKey);

        //Act
        var touched = await sut.BuyMoreTime(StoredKey, read, false);
        var readAfterTouch = () => sut.GetAsync<string>(StoredKey);

        //Assert
        touched.Should().BeTrue();
        await readAfterTouch.Should().NotThrowAsync("one counted failure would have opened the circuit");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Fallback_touch_by_key_renews_a_ttl_and_never_adds_one_without_reading_again(bool hasTimeToLive)
    {
        //Arrange
        var sut = BuildSut();
        SetupStoredValue(JsonSerializer.Serialize(BuildItem("a", FreshSpan)), hasTimeToLive ? RemainingTimeToLive : null);
        SetupScriptThrows(RedisErrors.Server(NoPermission));

        //Act
        var touched = await sut.BuyMoreTime<string>(StoredKey);

        //Assert
        touched.Should().BeTrue();
        _database.Verify(x => x.StringGetAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()), Times.Once, "the value read with PTTL in this call is the one compared");
        var write = _writes.Should().ContainSingle().Subject;
        write.When.Should().Be(When.Exists);
        write.Expiry.Should().Be(hasTimeToLive ? FreshSpan : null);
        JsonSerializer.Deserialize<CacheItem<string>>(write.Value).UpdateTime.Should().NotBeNull();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Fallback_invalidate_keeps_the_remaining_ttl(bool hasTimeToLive)
    {
        //Arrange
        var sut = BuildSut(retryCount: 3);
        SetupStoredValue(JsonSerializer.Serialize(BuildItem("a", FreshSpan)), hasTimeToLive ? RemainingTimeToLive : null);
        SetupScriptThrows(RedisErrors.Server(NoPermission));

        //Act
        var invalidated = await sut.Invalidate<string>(StoredKey);

        //Assert
        invalidated.Should().BeTrue();
        VerifyScriptCalls(Times.Once());
        var write = _writes.Should().ContainSingle().Subject;
        write.When.Should().Be(When.Exists);
        write.Expiry.Should().Be(hasTimeToLive ? RemainingTimeToLive : null, "an invalidate must neither wipe nor extend the TTL");
        JsonSerializer.Deserialize<CacheItem<string>>(write.Value).UpdateTime.Should().Be(DateTime.MinValue);
    }

    [Fact]
    public async Task Fallback_leaves_a_replaced_value_alone()
    {
        //Arrange
        var sut = BuildSut();
        SetupStoredValue(JsonSerializer.Serialize(BuildItem("a", FreshSpan)), null);
        SetupScriptThrows(RedisErrors.Server(NoPermission));
        var read = await sut.GetAsync<string>(StoredKey);
        SetupStoredValue(JsonSerializer.Serialize(BuildItem("b", FreshSpan)), null);

        //Act
        var touched = await sut.BuyMoreTime(StoredKey, read, false);

        //Assert
        touched.Should().BeFalse();
        _writes.Should().BeEmpty();
    }

    [Fact]
    public async Task Fallback_does_not_write_a_key_that_is_gone()
    {
        //Arrange
        var sut = BuildSut();
        SetupStoredValue(JsonSerializer.Serialize(BuildItem("a", FreshSpan)), null);
        SetupScriptThrows(RedisErrors.Server(NoPermission));
        var read = await sut.GetAsync<string>(StoredKey);
        SetupStoredValue(null, null);

        //Act
        var touched = await sut.BuyMoreTime(StoredKey, read, false);

        //Assert
        touched.Should().BeFalse();
        _writes.Should().BeEmpty();
    }

    [Fact]
    public async Task Fallback_reports_false_when_SET_XX_finds_no_key()
    {
        //Arrange
        var sut = BuildSut();
        SetupStoredValue(JsonSerializer.Serialize(BuildItem("a", FreshSpan)), RemainingTimeToLive);
        SetupScriptThrows(RedisErrors.Server(NoPermission));
        SetupSetIfExists(false);

        //Act
        var touched = await sut.BuyMoreTime<string>(StoredKey);

        //Assert
        touched.Should().BeFalse();
        _writes.Should().ContainSingle().Which.When.Should().Be(When.Exists);
    }

    [Fact]
    public async Task NOSCRIPT_is_not_treated_as_scripting_unavailable()
    {
        //Arrange
        var sut = BuildSut();
        SetupStoredValue(JsonSerializer.Serialize(BuildItem("a", FreshSpan)), null);
        SetupScriptThrows(RedisErrors.Server("NOSCRIPT No matching script. Please use EVAL."));

        //Act
        var act = () => sut.BuyMoreTime<string>(StoredKey);

        //Assert
        await act.Should().ThrowAsync<RedisServerException>();
        await act.Should().ThrowAsync<RedisServerException>();
        VerifyScriptCalls(Times.Exactly(2), "the script is tried again on the next call");
        _writes.Should().BeEmpty();
        VerifyLogged(LogLevel.Warning, Times.Never());
    }

    [Fact]
    public async Task Scripting_is_probed_again_after_the_reprobe_interval()
    {
        //Arrange
        var sut = BuildSut();
        SetupStoredValue(JsonSerializer.Serialize(BuildItem("a", FreshSpan)), null);
        SetupScriptThrows(RedisErrors.Server(NoPermission));
        await sut.BuyMoreTime<string>(StoredKey);
        _clock.Advance(RedisScriptingAvailability.ReprobeInterval - TimeSpan.FromSeconds(1));
        await sut.BuyMoreTime<string>(StoredKey);
        _clock.Advance(TimeSpan.FromSeconds(1));
        _database.Setup(x => x.ScriptEvaluateAsync(It.IsAny<string>(), It.IsAny<RedisKey[]>(), It.IsAny<RedisValue[]>(), It.IsAny<CommandFlags>())).ReturnsAsync(RedisResult.Create(1));

        //Act
        var probed = await sut.BuyMoreTime<string>(StoredKey);
        var afterProbe = await sut.BuyMoreTime<string>(StoredKey);

        //Assert
        probed.Should().BeTrue();
        afterProbe.Should().BeTrue();
        VerifyScriptCalls(Times.Exactly(3), "one failed call, the probe, and the script again once it works");
        _writes.Should().HaveCount(2, "only the calls before the probe used the fallback");
        VerifyLogged(LogLevel.Information, Times.Once());
    }

    [Fact]
    public async Task A_failed_probe_keeps_the_fallback_without_warning_again()
    {
        //Arrange
        var sut = BuildSut();
        SetupStoredValue(JsonSerializer.Serialize(BuildItem("a", FreshSpan)), null);
        SetupScriptThrows(RedisErrors.Server(NoPermission));
        await sut.BuyMoreTime<string>(StoredKey);
        _clock.Advance(RedisScriptingAvailability.ReprobeInterval);

        //Act
        var probed = await sut.BuyMoreTime<string>(StoredKey);
        var afterProbe = await sut.BuyMoreTime<string>(StoredKey);

        //Assert
        probed.Should().BeTrue();
        afterProbe.Should().BeTrue();
        VerifyScriptCalls(Times.Exactly(2));
        _writes.Should().HaveCount(3);
        VerifyLogged(LogLevel.Warning, Times.Once());
    }

    private Redis BuildSut(int retryCount = 0, int circuitBreakerFailureThreshold = 5)
    {
        var options = Options.Create(new RedisCacheOptions { RetryCount = retryCount, CircuitBreakerFailureThreshold = circuitBreakerFailureThreshold });
        return new Redis(Mock.Of<IServiceProvider>(), Mock.Of<IHostEnvironment>(), Mock.Of<IManagedCacheMonitor>(), options, _logger.Object, _connection.Object, _clock);
    }

    private void SetupStoredValue(string stored, TimeSpan? timeToLive)
    {
        _database.Setup(x => x.StringGetAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>())).ReturnsAsync(stored);
        _database.Setup(x => x.KeyTimeToLiveAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>())).ReturnsAsync(timeToLive);
    }

    private void SetupScriptThrows(Exception exception)
    {
        _database.Setup(x => x.ScriptEvaluateAsync(It.IsAny<string>(), It.IsAny<RedisKey[]>(), It.IsAny<RedisValue[]>(), It.IsAny<CommandFlags>())).ThrowsAsync(exception);
    }

    private void SetupSetIfExists(bool result)
    {
        _database.Setup(x => x.StringSetAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<TimeSpan?>(), It.IsAny<bool>(), It.IsAny<When>(), It.IsAny<CommandFlags>()))
            .Callback<RedisKey, RedisValue, TimeSpan?, bool, When, CommandFlags>((_, value, expiry, _, when, _) => _writes.Add((expiry, when, value)))
            .ReturnsAsync(result);
    }

    private void VerifyScriptCalls(Times times, string because = "")
    {
        _database.Verify(x => x.ScriptEvaluateAsync(It.IsAny<string>(), It.IsAny<RedisKey[]>(), It.IsAny<RedisValue[]>(), It.IsAny<CommandFlags>()), times, because);
    }

    private void VerifyLogged(LogLevel level, Times times)
    {
        _logger.Verify(x => x.Log(level, It.IsAny<EventId>(), It.IsAny<It.IsAnyType>(), It.IsAny<Exception>(), It.IsAny<Func<It.IsAnyType, Exception, string>>()), times);
    }

    private static CacheItem<string> BuildItem(string data, TimeSpan? freshSpan)
    {
        return new CacheItem<string> { KeyParts = [], CreateTime = DateTime.UtcNow, FreshSpan = freshSpan, Data = data };
    }
}
