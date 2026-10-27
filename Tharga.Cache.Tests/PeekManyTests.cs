using FluentAssertions;
using Tharga.Cache.Tests.Helper;
using Xunit;

namespace Tharga.Cache.Tests;

public class PeekManyTests
{
    [Fact]
    public async Task PeekManyAsync_gives_the_same_result_as_PeekAsync_per_key_with_one_read()
    {
        //Arrange
        var (cache, persist) = RecordingCacheFactory.Create<string>();
        await cache.SetAsync("a", "value-a");
        await cache.SetAsync("c", "value-c");

        //Act
        var result = await cache.PeekManyAsync<string>(["c", "missing", "a", "c"]);

        //Assert
        result.Select(x => x.Key.Value).Should().Equal("c", "missing", "a", "c");
        result.Select(x => x.Data).Should().Equal("value-c", null, "value-a", "value-c");
        persist.GetManyCalls.Should().Be(1);
        persist.GetCalls.Should().Be(0);
        persist.BuyMoreTimeWithItemCalls.Should().Be(3, "every hit is touched, as with PeekAsync");
    }

    [Fact]
    public async Task PeekManyAsync_with_no_keys_does_not_read()
    {
        //Arrange
        var (cache, persist) = RecordingCacheFactory.Create<string>();

        //Act
        var result = await cache.PeekManyAsync<string>([]);

        //Assert
        result.Should().BeEmpty();
        persist.GetManyCalls.Should().Be(0);
    }

    [Fact]
    public async Task PeekManyAsync_fails_open_to_misses_when_the_backend_read_throws()
    {
        //Arrange
        var (cache, persist) = RecordingCacheFactory.Create<string>();
        await cache.SetAsync("a", "value-a");
        persist.GetManyException = new InvalidOperationException("backend down");

        //Act
        var result = await cache.PeekManyAsync<string>(["a", "b"]);

        //Assert
        result.Select(x => x.Data).Should().Equal(null, null);
    }
}
