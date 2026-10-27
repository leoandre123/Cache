using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Tharga.Cache.Tests.Helper;
using Xunit;

namespace Tharga.Cache.Tests;

public class TouchTests
{
    private const string CacheKey = "Key";
    private const string OtherCacheKey = "OtherKey";
    private const string Value = "value";
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(5);
    private static readonly Key StoredKey = ((Key)CacheKey).SetTypeKey<string>();

    [Fact]
    public async Task PeekAsync_hit_reads_once_and_touches_with_the_read_item()
    {
        //Arrange
        var sut = RecordingCacheFactory.Create<string>();
        await sut.Cache.SetAsync(CacheKey, Value);

        //Act
        var result = await sut.Cache.PeekAsync<string>(CacheKey);

        //Assert
        result.Should().Be(Value);
        sut.Persist.GetCalls.Should().Be(1, "a hit is a single read");
        sut.Persist.BuyMoreTimeWithItemCalls.Should().Be(1, "the touch reuses the item that was read");
        sut.Persist.BuyMoreTimeByKeyCalls.Should().Be(0, "the touch must not read the item again");
    }

    [Fact]
    public async Task GetAsync_hit_reads_once_and_touches_with_the_read_item()
    {
        //Arrange
        var sut = RecordingCacheFactory.Create<string>();
        await sut.Cache.SetAsync(CacheKey, Value);

        //Act
        var result = await sut.Cache.GetAsync(CacheKey, () => Task.FromResult("other"));

        //Assert
        result.Should().Be(Value);
        sut.Persist.GetCalls.Should().Be(1);
        sut.Persist.BuyMoreTimeWithItemCalls.Should().Be(1);
        sut.Persist.BuyMoreTimeByKeyCalls.Should().Be(0);
    }

    [Fact]
    public async Task GetAsync_after_a_load_touches_by_key()
    {
        //Arrange
        var sut = RecordingCacheFactory.Create<string>();

        //Act
        var result = await sut.Cache.GetAsync(CacheKey, () => Task.FromResult(Value));

        //Assert
        result.Should().Be(Value);
        sut.Persist.BuyMoreTimeWithItemCalls.Should().Be(0, "there is no read item after a load");
        sut.Persist.BuyMoreTimeByKeyCalls.Should().Be(1);
    }

    [Fact]
    public async Task PeekAsync_hit_moves_the_update_time_forward()
    {
        //Arrange
        var sut = RecordingCacheFactory.Create<string>();
        await sut.Cache.SetAsync(CacheKey, Value);
        var beforePeek = DateTime.UtcNow;

        //Act
        await sut.Cache.PeekAsync<string>(CacheKey);

        //Assert
        var after = await sut.Persist.GetAsync<string>(StoredKey);
        after.UpdateTime.Should().NotBeNull().And.BeOnOrAfter(beforePeek);
        after.Data.Should().Be(Value);
    }

    [Fact]
    public async Task Time_to_live_hit_does_not_touch()
    {
        //Arrange
        var sut = RecordingCacheFactory.Create<string>(timeToIdle: false);
        await sut.Cache.SetAsync(CacheKey, Value);

        //Act
        await sut.Cache.PeekAsync<string>(CacheKey);

        //Assert
        sut.Persist.BuyMoreTimeWithItemCalls.Should().Be(0);
        sut.Persist.BuyMoreTimeByKeyCalls.Should().Be(0);
    }

    [Fact]
    public async Task Foreground_touch_completes_before_the_read_returns()
    {
        //Arrange
        var sut = RecordingCacheFactory.Create<string>();
        await sut.Cache.SetAsync(CacheKey, Value);
        sut.Persist.TouchGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        //Act
        var peek = sut.Cache.PeekAsync<string>(CacheKey);
        await WaitUntilAsync(() => sut.Persist.BuyMoreTimeWithItemCalls == 1);

        //Assert
        peek.IsCompleted.Should().BeFalse("by default the read waits for the touch");
        sut.Persist.TouchGate.SetResult();
        (await peek).Should().Be(Value);
        sut.Persist.CompletedTouches.Should().Be(1);
    }

    [Fact]
    public async Task Background_touch_does_not_hold_the_read_but_still_raises_events_and_monitors()
    {
        //Arrange
        var sut = RecordingCacheFactory.Create<string>(typeOptions: o => o.TouchInBackground = true);
        await sut.Cache.SetAsync(CacheKey, Value);
        sut.Persist.TouchGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var dataGetEventCount = 0;
        var monitorGetEventCount = 0;
        sut.Cache.DataGetEvent += (_, _) => dataGetEventCount++;
        sut.Monitor.DataGetEvent += (_, _) => monitorGetEventCount++;

        //Act
        var result = await sut.Cache.PeekAsync<string>(CacheKey).WaitAsync(WaitTimeout);

        //Assert
        result.Should().Be(Value);
        dataGetEventCount.Should().Be(1);
        monitorGetEventCount.Should().Be(1, "the monitor's access bookkeeping happens before the read returns");
        sut.Persist.CompletedTouches.Should().Be(0, "the touch is still held open");

        sut.Persist.TouchGate.SetResult();
        await WaitUntilAsync(() => sut.Persist.CompletedTouches == 1);
        var stored = await sut.Persist.GetAsync<string>(StoredKey);
        stored.UpdateTime.Should().NotBeNull("the background touch was applied");
    }

    [Fact]
    public async Task Background_touch_is_not_started_again_while_one_is_pending_for_the_key()
    {
        //Arrange
        var sut = RecordingCacheFactory.Create<string>(typeOptions: o => o.TouchInBackground = true);
        await sut.Cache.SetAsync(CacheKey, Value);
        await sut.Cache.SetAsync(OtherCacheKey, Value);
        sut.Persist.TouchGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        //Act
        await sut.Cache.PeekAsync<string>(CacheKey);
        await WaitUntilAsync(() => sut.Persist.BuyMoreTimeWithItemCalls == 1);
        await sut.Cache.PeekAsync<string>(CacheKey);
        await sut.Cache.PeekAsync<string>(CacheKey);
        await sut.Cache.PeekAsync<string>(OtherCacheKey);
        await WaitUntilAsync(() => sut.Persist.BuyMoreTimeWithItemCalls == 2);

        //Assert
        sut.Persist.TouchGate.SetResult();
        await WaitUntilAsync(() => sut.Persist.CompletedTouches == 2);
        sut.Persist.BuyMoreTimeWithItemCalls.Should().Be(2, "one touch per key while it is pending, another key is not held up");

        sut.Persist.TouchGate = null;
        await WaitUntilAsync(async () =>
        {
            await sut.Cache.PeekAsync<string>(CacheKey);
            return sut.Persist.CompletedTouches >= 3;
        });
    }

    [Fact]
    public async Task Background_touch_is_not_started_after_the_cache_is_disposed()
    {
        //Arrange
        var sut = RecordingCacheFactory.Create<string>(typeOptions: o => o.TouchInBackground = true);
        await sut.Cache.SetAsync(CacheKey, Value);
        ((IDisposable)sut.Cache).Dispose();

        //Act
        var result = await sut.Cache.PeekAsync<string>(CacheKey);

        //Assert
        result.Should().Be(Value);
        await Task.Delay(TimeSpan.FromMilliseconds(100));
        sut.Persist.BuyMoreTimeWithItemCalls.Should().Be(0);
    }

    [Theory]
    [InlineData(true, LogLevel.Warning)]
    [InlineData(false, LogLevel.Error)]
    public async Task Background_touch_failure_is_logged_and_never_reaches_the_caller(bool failOpen, LogLevel expectedLevel)
    {
        //Arrange
        var logger = new Mock<ILogger>();
        var logged = new TaskCompletionSource<LogLevel>(TaskCreationOptions.RunContinuationsAsynchronously);
        logger.Setup(x => x.Log(It.IsAny<LogLevel>(), It.IsAny<EventId>(), It.IsAny<It.IsAnyType>(), It.IsAny<Exception>(), It.IsAny<Func<It.IsAnyType, Exception, string>>()))
            .Callback(new InvocationAction(x => logged.TrySetResult((LogLevel)x.Arguments[0])));
        var sut = RecordingCacheFactory.Create<string>(typeOptions: o => o.TouchInBackground = true, failOpen: failOpen, logger: logger.Object);
        await sut.Cache.SetAsync(CacheKey, Value);
        sut.Persist.TouchException = new TimeoutException("Simulated backend timeout.");

        //Act
        var result = await sut.Cache.PeekAsync<string>(CacheKey);

        //Assert
        result.Should().Be(Value);
        (await logged.Task.WaitAsync(WaitTimeout)).Should().Be(expectedLevel);
    }

    [Fact]
    public async Task Foreground_touch_failure_propagates_when_fail_open_is_disabled()
    {
        //Arrange
        var sut = RecordingCacheFactory.Create<string>(failOpen: false);
        await sut.Cache.SetAsync(CacheKey, Value);
        sut.Persist.TouchException = new TimeoutException("Simulated backend timeout.");

        //Act
        var act = async () => await sut.Cache.PeekAsync<string>(CacheKey);

        //Assert
        await act.Should().ThrowAsync<TimeoutException>("the foreground touch keeps today's behaviour");
    }

    private static Task WaitUntilAsync(Func<bool> condition)
    {
        return WaitUntilAsync(() => Task.FromResult(condition()));
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow + WaitTimeout;
        while (!await condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("The condition was not met in time.");
            await Task.Delay(10);
        }
    }
}
