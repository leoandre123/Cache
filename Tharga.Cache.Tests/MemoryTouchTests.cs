using FluentAssertions;
using Moq;
using Tharga.Cache.Core;
using Tharga.Cache.Persist;
using Tharga.Cache.Tests.Helper;
using Xunit;

namespace Tharga.Cache.Tests;

public class MemoryTouchTests
{
    private static readonly Key StoredKey = "String.Key";

    [Fact]
    public async Task Touch_with_a_replaced_item_keeps_the_newer_data()
    {
        //Arrange
        var sut = BuildSut();
        await sut.SetAsync(StoredKey, BuildItem("first"), false);
        var read = await sut.GetAsync<string>(StoredKey);
        await sut.SetAsync(StoredKey, BuildItem("second"), false);

        //Act
        var touched = await sut.BuyMoreTime(StoredKey, read, false);

        //Assert
        touched.Should().BeFalse("the entry was replaced after it was read");
        var stored = await sut.GetAsync<string>(StoredKey);
        stored.Data.Should().Be("second");
        stored.UpdateTime.Should().BeNull();
    }

    [Fact]
    public async Task Touch_after_an_invalidate_keeps_the_entry_invalid()
    {
        //Arrange
        var sut = BuildSut();
        await sut.SetAsync(StoredKey, BuildItem("first"), true);
        var read = await sut.GetAsync<string>(StoredKey);
        await sut.Invalidate<string>(StoredKey);

        //Act
        var touched = await sut.BuyMoreTime(StoredKey, read, true);

        //Assert
        touched.Should().BeFalse();
        (await sut.GetAsync<string>(StoredKey)).UpdateTime.Should().Be(DateTime.MinValue);
    }

    [Fact]
    public async Task Touch_with_the_read_item_takes_the_time_from_the_clock()
    {
        //Arrange
        var clock = new ManualTimeProvider();
        clock.Advance(TimeSpan.FromHours(1));
        var sut = new Memory(Mock.Of<IManagedCacheMonitor>(), clock);
        await sut.SetAsync(StoredKey, BuildItem("first"), false);
        var read = await sut.GetAsync<string>(StoredKey);

        //Act
        await sut.BuyMoreTime(StoredKey, read, false);

        //Assert
        (await sut.GetAsync<string>(StoredKey)).UpdateTime.Should().Be(clock.GetUtcNow().UtcDateTime);
    }

    private static IPersist BuildSut()
    {
        return new Memory(Mock.Of<IManagedCacheMonitor>());
    }

    private static CacheItem<string> BuildItem(string data)
    {
        return new CacheItem<string> { KeyParts = [], CreateTime = DateTime.UtcNow, FreshSpan = TimeSpan.FromMinutes(10), Data = data };
    }
}
