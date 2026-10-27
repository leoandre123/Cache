using FluentAssertions;
using Tharga.Cache.Tests.Helper;
using Xunit;

namespace Tharga.Cache.Tests;

public class PeekManyTests
{
    private const string FreshKey = "fresh";
    private const string OtherFreshKey = "other-fresh";
    private const string StaleKey = "stale";
    private const string MissingKey = "missing";
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(5);
    private static readonly Key[] Keys = [FreshKey, MissingKey, StaleKey, OtherFreshKey];

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task PeekManyAsync_gives_the_same_result_as_one_PeekAsync_per_key(bool staleWhileRevalidate, bool timeToIdle)
    {
        //Arrange
        var single = await BuildSeededAsync(staleWhileRevalidate, timeToIdle);
        var many = await BuildSeededAsync(staleWhileRevalidate, timeToIdle);
        var singleEvents = CountEvents(single);
        var manyEvents = CountEvents(many);

        //Act
        var expected = new List<(Key Key, string Data)>();
        foreach (var key in Keys)
        {
            expected.Add((key, await single.Cache.PeekAsync<string>(key)));
        }

        var result = await many.Cache.PeekManyAsync<string>(Keys);

        //Assert
        result.Should().Equal(expected);
        result.Select(x => x.Data).Should().Equal(FreshKey, null, staleWhileRevalidate ? StaleKey : null, OtherFreshKey);
        manyEvents.DataGet.Should().Be(singleEvents.DataGet);
        manyEvents.MonitorGet.Should().Be(singleEvents.MonitorGet);
        many.Persist.BuyMoreTimeWithItemCalls.Should().Be(single.Persist.BuyMoreTimeWithItemCalls);
        many.Persist.BuyMoreTimeByKeyCalls.Should().Be(0);
    }

    [Fact]
    public async Task PeekManyAsync_reads_the_persist_once()
    {
        //Arrange
        var sut = await BuildSeededAsync(false, true);

        //Act
        await sut.Cache.PeekManyAsync<string>(Keys);

        //Assert
        sut.Persist.GetManyCalls.Should().Be(1);
        sut.Persist.GetCalls.Should().Be(0);
    }

    [Fact]
    public async Task PeekManyAsync_touches_in_the_background_when_opted_in()
    {
        //Arrange
        var sut = await BuildSeededAsync(false, true, o => o.TouchInBackground = true);
        sut.Persist.TouchGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        //Act
        var result = await sut.Cache.PeekManyAsync<string>(Keys).WaitAsync(WaitTimeout);

        //Assert
        result.Select(x => x.Data).Should().Equal(FreshKey, null, null, OtherFreshKey);
        sut.Persist.CompletedTouches.Should().Be(0);
        sut.Persist.TouchGate.SetResult();
        await WaitUntilAsync(() => sut.Persist.CompletedTouches == 2);
    }

    [Fact]
    public async Task PeekManyAsync_keeps_the_requested_order_and_duplicates()
    {
        //Arrange
        var sut = await BuildSeededAsync(false, false);
        Key[] keys = [OtherFreshKey, FreshKey, OtherFreshKey];

        //Act
        var result = await sut.Cache.PeekManyAsync<string>(keys);

        //Assert
        result.Select(x => x.Key.Value).Should().Equal(OtherFreshKey, FreshKey, OtherFreshKey);
        result.Select(x => x.Data).Should().Equal(OtherFreshKey, FreshKey, OtherFreshKey);
    }

    [Fact]
    public async Task PeekManyAsync_with_no_keys_does_not_read()
    {
        //Arrange
        var sut = RecordingCacheFactory.Create<string>();

        //Act
        var result = await sut.Cache.PeekManyAsync<string>([]);

        //Assert
        result.Should().BeEmpty();
        sut.Persist.GetManyCalls.Should().Be(0);
    }

    [Fact]
    public async Task PeekManyAsync_fails_open_to_misses_when_the_backend_read_throws()
    {
        //Arrange
        var sut = await BuildSeededAsync(false, true);
        sut.Persist.GetManyException = new TimeoutException("Simulated backend read timeout.");

        //Act
        var result = await sut.Cache.PeekManyAsync<string>(Keys);

        //Assert
        result.Select(x => x.Data).Should().AllSatisfy(x => x.Should().BeNull());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PeekManyAsync_throws_when_the_persist_breaks_the_GetManyAsync_contract(bool failOpen)
    {
        //Arrange
        var sut = RecordingCacheFactory.Create<string>(failOpen: failOpen);
        sut.Persist.DropLastOfGetMany = true;

        //Act
        var act = () => sut.Cache.PeekManyAsync<string>(Keys);

        //Assert
        await act.Should().ThrowAsync<InvalidOperationException>("a persist that does not return one item per key is a programming error, not a backend failure");
    }

    private static async Task<RecordingCache> BuildSeededAsync(bool staleWhileRevalidate, bool timeToIdle, Action<CacheTypeOptions> typeOptions = null)
    {
        var sut = RecordingCacheFactory.Create<string>(timeToIdle, o =>
        {
            o.StaleWhileRevalidate = staleWhileRevalidate;
            typeOptions?.Invoke(o);
        });

        await sut.Cache.SetAsync(FreshKey, FreshKey);
        await sut.Cache.SetAsync(OtherFreshKey, OtherFreshKey);
        var staleItem = new CacheItem<string>
        {
            KeyParts = [],
            CreateTime = DateTime.UtcNow.AddHours(-1),
            FreshSpan = TimeSpan.FromMinutes(1),
            Data = StaleKey
        };
        await sut.Persist.SetAsync(((Key)StaleKey).SetTypeKey<string>(), staleItem, staleWhileRevalidate);

        return sut;
    }

    private static EventCounts CountEvents(RecordingCache sut)
    {
        var counts = new EventCounts();
        sut.Cache.DataGetEvent += (_, _) => counts.DataGet++;
        sut.Monitor.DataGetEvent += (_, _) => counts.MonitorGet++;
        return counts;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + WaitTimeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("The condition was not met in time.");
            await Task.Delay(10);
        }
    }

    private class EventCounts
    {
        public int DataGet { get; set; }
        public int MonitorGet { get; set; }
    }
}
