using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using StackExchange.Redis;
using Xunit;

namespace Tharga.Cache.Redis.Tests;

/// <summary>
/// Runs against the Redis server named by the environment variable THARGA_CACHE_TEST_REDIS, e.g.
/// "127.0.0.1:6379,defaultDatabase=15", and is skipped without it. Only keys created by the test are written and deleted.
/// </summary>
[Trait("Category", "Integration")]
public class RedisServerTests : IAsyncLifetime
{
    private const string ConnectionStringVariable = "THARGA_CACHE_TEST_REDIS";
    private static readonly TimeSpan FreshSpan = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan TouchDelay = TimeSpan.FromMilliseconds(1100);

    private readonly string _connectionString = Environment.GetEnvironmentVariable(ConnectionStringVariable);
    private readonly string _key = $"tharga-cache-test-{Guid.NewGuid():N}";
    private ConnectionMultiplexer _connection;
    private IHost _host;

    public async ValueTask InitializeAsync()
    {
        if (string.IsNullOrEmpty(_connectionString)) return;

        _connection = await ConnectionMultiplexer.ConnectAsync(_connectionString);
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddCache(o =>
        {
            o.AddRedisDBOptions(r => r.ConnectionStringLoader = _ => _connectionString);
            o.RegisterType<TestItem, IRedis>(s => s.DefaultFreshSpan = FreshSpan);
            o.RegisterType<StaleItem, IRedis>(s =>
            {
                s.DefaultFreshSpan = FreshSpan;
                s.StaleWhileRevalidate = true;
            });
        });
        _host = builder.Build();
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection == null) return;

        await Database.KeyDeleteAsync([$"{nameof(TestItem)}.{_key}", $"{nameof(StaleItem)}.{_key}"]);
        _host.Dispose();
        await _connection.DisposeAsync();
    }

    private IDatabase Database => _connection.GetDatabase();

    private ITimeToIdleCache Cache => _host.Services.GetRequiredService<ITimeToIdleCache>();

    private IRedis Persist => _host.Services.GetRequiredService<IRedis>();

    private Task<TimeSpan?> TimeToLiveAsync<T>() => Database.KeyTimeToLiveAsync($"{typeof(T).Name}.{_key}");

    [Fact]
    public async Task Hit_renews_the_ttl()
    {
        //Arrange
        SkipWithoutServer();
        await Cache.SetAsync(_key, new TestItem("value"));
        await Task.Delay(TouchDelay, TestContext.Current.CancellationToken);
        var before = await TimeToLiveAsync<TestItem>();

        //Act
        await Cache.PeekAsync<TestItem>(_key);

        //Assert
        (await TimeToLiveAsync<TestItem>()).Should().NotBeNull("the touch must not remove the TTL").And.BeGreaterThan(before.Value);
    }

    [Fact]
    public async Task Load_keeps_the_ttl()
    {
        //Arrange
        SkipWithoutServer();

        //Act
        await Cache.GetAsync(_key, () => Task.FromResult(new TestItem("value")));

        //Assert
        (await TimeToLiveAsync<TestItem>()).Should().NotBeNull("the touch by key after a load must not remove the TTL");
    }

    [Fact]
    public async Task Stale_while_revalidate_keeps_no_ttl_on_load_hit_and_invalidate()
    {
        //Arrange
        SkipWithoutServer();

        //Act
        await Cache.GetAsync(_key, () => Task.FromResult(new StaleItem("value")));
        await Cache.PeekAsync<StaleItem>(_key);
        await Cache.InvalidateAsync<StaleItem>(_key);

        //Assert
        (await TimeToLiveAsync<StaleItem>()).Should().BeNull("a stale value must stay readable");
        (await Cache.PeekAsync<StaleItem>(_key)).Should().Be(new StaleItem("value"));
    }

    [Fact]
    public async Task Invalidate_keeps_the_remaining_ttl()
    {
        //Arrange
        SkipWithoutServer();
        var key = $"{nameof(TestItem)}.{_key}";
        await Persist.SetAsync<TestItem>(key, BuildItem(), false);
        await Task.Delay(TouchDelay, TestContext.Current.CancellationToken);
        var before = await TimeToLiveAsync<TestItem>();

        //Act
        await Persist.Invalidate<TestItem>(key);

        //Assert
        (await TimeToLiveAsync<TestItem>()).Should().NotBeNull("an invalidate must not remove the TTL").And.BeLessThanOrEqualTo(before.Value, "nor extend it");
    }

    [Fact]
    public async Task Touch_of_a_dropped_key_does_not_recreate_it()
    {
        //Arrange
        SkipWithoutServer();
        var key = $"{nameof(TestItem)}.{_key}";
        await Persist.SetAsync<TestItem>(key, BuildItem(), false);
        var read = await Persist.GetAsync<TestItem>(key);
        await Persist.DropAsync<TestItem>(key);

        //Act
        var touched = await Persist.BuyMoreTime(key, read, false);

        //Assert
        touched.Should().BeFalse();
        (await Database.KeyExistsAsync(key)).Should().BeFalse();
    }

    [Fact]
    public async Task PeekManyAsync_matches_PeekAsync_per_key()
    {
        //Arrange
        SkipWithoutServer();
        await Cache.SetAsync(_key, new TestItem("value"));

        //Act
        var result = await Cache.PeekManyAsync<TestItem>([_key, $"{_key}-missing", _key]);

        //Assert
        result.Select(x => x.Data).Should().Equal(new TestItem("value"), null, new TestItem("value"));
    }

    private void SkipWithoutServer()
    {
        Assert.SkipWhen(_connection == null, $"Set {ConnectionStringVariable} to a Redis connection string to run this test.");
    }

    private static CacheItem<TestItem> BuildItem()
    {
        return new CacheItem<TestItem> { KeyParts = [], CreateTime = DateTime.UtcNow, FreshSpan = FreshSpan, Data = new TestItem("value") };
    }

    public record TestItem(string Value);

    public record StaleItem(string Value);
}
