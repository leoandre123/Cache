using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Tharga.Cache.Tests.Helper;
using Xunit;

namespace Tharga.Cache.Tests;

public class TouchTests
{
    private const string CacheKey = "Key";
    private const string Value = "value";
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task Hit_reads_once_and_touches_with_the_read_item()
    {
        //Arrange
        var (cache, persist) = RecordingCacheFactory.Create<string>();
        await cache.SetAsync(CacheKey, Value);

        //Act
        var peeked = await cache.PeekAsync<string>(CacheKey);
        var got = await cache.GetAsync(CacheKey, () => Task.FromResult("other"));

        //Assert
        peeked.Should().Be(Value);
        got.Should().Be(Value);
        persist.GetCalls.Should().Be(2, "each hit is a single read");
        persist.BuyMoreTimeWithItemCalls.Should().Be(2);
        persist.BuyMoreTimeByKeyCalls.Should().Be(0, "the touch must not read the item again");
    }

    [Fact]
    public async Task Load_touches_by_key()
    {
        //Arrange
        var (cache, persist) = RecordingCacheFactory.Create<string>();

        //Act
        await cache.GetAsync(CacheKey, () => Task.FromResult(Value));

        //Assert
        persist.BuyMoreTimeWithItemCalls.Should().Be(0, "there is no read item after a load");
        persist.BuyMoreTimeByKeyCalls.Should().Be(1);
    }

    [Fact]
    public async Task Stale_hit_touches_by_key()
    {
        //Arrange
        var (cache, persist) = RecordingCacheFactory.Create<string>(o => o.StaleWhileRevalidate = true);
        var stale = new CacheItem<string> { KeyParts = [], CreateTime = DateTime.UtcNow.AddHours(-1), FreshSpan = TimeSpan.FromMinutes(10), Data = Value };
        await persist.SetAsync(((Key)CacheKey).SetTypeKey<string>(), stale, true);

        //Act
        var result = await cache.PeekAsync<string>(CacheKey);

        //Assert
        result.Should().Be(Value, "stale-while-revalidate returns the stale value");
        persist.BuyMoreTimeWithItemCalls.Should().Be(0, "a stale item may be reloading, so the copy that was read must not be written back");
        persist.BuyMoreTimeByKeyCalls.Should().Be(1);
    }

    [Fact]
    public async Task Background_touch_does_not_hold_the_read()
    {
        //Arrange
        var (cache, persist) = RecordingCacheFactory.Create<string>(o => o.TouchInBackground = true);
        await cache.SetAsync(CacheKey, Value);
        persist.TouchGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        //Act
        var result = await cache.PeekAsync<string>(CacheKey).WaitAsync(WaitTimeout, TestContext.Current.CancellationToken);

        //Assert
        result.Should().Be(Value);
        persist.TouchGate.SetResult();
    }

    [Fact]
    public async Task Background_touch_failure_is_logged_and_never_reaches_the_caller()
    {
        //Arrange
        var logged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var logger = new Mock<ILogger>();
        logger.Setup(x => x.Log(LogLevel.Warning, It.IsAny<EventId>(), It.IsAny<It.IsAnyType>(), It.IsAny<Exception>(), It.IsAny<Func<It.IsAnyType, Exception, string>>()))
            .Callback(() => logged.TrySetResult());
        var (cache, persist) = RecordingCacheFactory.Create<string>(o => o.TouchInBackground = true, logger.Object);
        await cache.SetAsync(CacheKey, Value);
        persist.TouchException = new InvalidOperationException("touch failed");

        //Act
        var result = await cache.PeekAsync<string>(CacheKey);

        //Assert
        result.Should().Be(Value);
        await logged.Task.WaitAsync(WaitTimeout, TestContext.Current.CancellationToken);
    }
}
