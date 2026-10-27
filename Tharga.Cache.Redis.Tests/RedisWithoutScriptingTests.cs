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
/// Runs the persist as a temporary ACL user that may not run scripts (<c>-@scripting</c>), against the server named by
/// <see cref="RedisServerFixture.ConnectionStringVariable"/>. The user is created for each test and always deleted
/// afterwards, together with the keys the test created.
/// </summary>
[Trait("Category", "Integration")]
public class RedisWithoutScriptingTests : IClassFixture<RedisServerFixture>, IAsyncLifetime
{
    private static readonly TimeSpan FreshSpan = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan TouchDelay = TimeSpan.FromMilliseconds(1100);

    private readonly ConnectionMultiplexer _adminConnection;
    private readonly string _userName = $"tharga-cache-test-{Guid.NewGuid():N}";
    private readonly string _password = Guid.NewGuid().ToString("N");
    private readonly List<string> _createdKeys = [];
    private readonly Mock<ILogger<Redis>> _logger = new();
    private ConnectionMultiplexer _userConnection;

    public RedisWithoutScriptingTests(RedisServerFixture fixture)
    {
        _adminConnection = fixture.Connection;
    }

    public async ValueTask InitializeAsync()
    {
        if (_adminConnection == null) return;

        await AdminServer.ExecuteAsync("ACL", "SETUSER", _userName, "on", $">{_password}", "~*", "&*", "+@all", "-@scripting");
        try
        {
            var options = ConfigurationOptions.Parse(Environment.GetEnvironmentVariable(RedisServerFixture.ConnectionStringVariable));
            options.User = _userName;
            options.Password = _password;
            _userConnection = await ConnectionMultiplexer.ConnectAsync(options);
        }
        catch
        {
            await DeleteUserAsync();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_adminConnection == null) return;

        try
        {
            if (_userConnection != null) await _userConnection.DisposeAsync();
            await _adminConnection.GetDatabase().KeyDeleteAsync(_createdKeys.Select(x => (RedisKey)x).ToArray());
        }
        finally
        {
            await DeleteUserAsync();
        }
    }

    private IServer AdminServer => _adminConnection.GetServer(_adminConnection.GetEndPoints().First());

    private IDatabase Database => _adminConnection.GetDatabase();

    private Task<TimeSpan?> TimeToLiveAsync(Key key) => Database.KeyTimeToLiveAsync(key.Value);

    [Fact]
    public async Task The_server_rejects_scripting_for_the_user_and_the_error_is_recognised()
    {
        //Arrange
        Assert.SkipWhen(_userConnection == null, $"Set {RedisServerFixture.ConnectionStringVariable} to a Redis connection string to run this test.");

        //Act
        var act = () => _userConnection.GetDatabase().ScriptEvaluateAsync("return 1");

        //Assert
        var exception = (await act.Should().ThrowAsync<RedisServerException>()).Which;
        RedisScriptingErrors.IsScriptingUnavailable(exception).Should().BeTrue($"'{exception.Message}' means scripting is unavailable");
    }

    [Fact]
    public async Task Touch_with_the_read_item_falls_back_renews_the_ttl_and_is_not_retried()
    {
        //Arrange
        var persist = BuildPersist();
        var key = NewKey();
        await persist.SetAsync(key, BuildItem("value"), false);
        await Task.Delay(TouchDelay);
        var ttlBeforeTouch = await TimeToLiveAsync(key);
        var read = await persist.GetAsync<string>(key);

        //Act
        var first = await persist.BuyMoreTime(key, read, false);
        var second = await persist.BuyMoreTime(key, await persist.GetAsync<string>(key), false);

        //Assert
        first.Should().BeTrue();
        second.Should().BeTrue();
        var after = await persist.GetAsync<string>(key);
        after.Data.Should().Be("value");
        after.UpdateTime.Should().NotBeNull();
        var ttlAfterTouch = await TimeToLiveAsync(key);
        ttlAfterTouch.Should().NotBeNull("the touch must not wipe the TTL").And.BeGreaterThan(ttlBeforeTouch.Value, "a time-to-idle touch renews the TTL to the fresh span");
        ttlAfterTouch.Should().BeLessThanOrEqualTo(FreshSpan);
        _logger.Verify(x => x.Log(LogLevel.Warning, It.IsAny<EventId>(), It.IsAny<It.IsAnyType>(), It.IsAny<Exception>(), It.IsAny<Func<It.IsAnyType, Exception, string>>()), Times.Once, "the switch is logged once, and a retry would have logged too");
    }

    private Redis BuildPersist()
    {
        Assert.SkipWhen(_userConnection == null, $"Set {RedisServerFixture.ConnectionStringVariable} to a Redis connection string to run this test.");

        var options = Options.Create(new RedisCacheOptions { RetryCount = 3 });
        return new Redis(Mock.Of<IServiceProvider>(), Mock.Of<IHostEnvironment>(), Mock.Of<IManagedCacheMonitor>(), options, _logger.Object, _userConnection);
    }

    private Key NewKey()
    {
        var key = ((Key)$"tharga-cache-test-{Guid.NewGuid():N}").SetTypeKey<string>();
        _createdKeys.Add(key);
        return key;
    }

    private async Task DeleteUserAsync()
    {
        await AdminServer.ExecuteAsync("ACL", "DELUSER", _userName);
    }

    private static CacheItem<string> BuildItem(string data)
    {
        return new CacheItem<string> { KeyParts = [], CreateTime = DateTime.UtcNow, FreshSpan = FreshSpan, Data = data };
    }
}
