using System.Collections.Concurrent;
using FluentAssertions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Tharga.Cache.Core;
using Xunit;

namespace Tharga.Cache.File.Tests;

public class FileTouchTests
{
    private const string StoredKey = "String.Key";
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(5);
    private readonly ConcurrentDictionary<string, string> _files = new();
    private readonly IPersist _sut;
    private TaskCompletionSource _writeGate;
    private TaskCompletionSource _writeReached;

    public FileTouchTests()
    {
        var fileService = new Mock<IFileService>();
        fileService.Setup(x => x.GetDataAsync(It.IsAny<string>())).Returns<string>(x => Task.FromResult(_files.GetValueOrDefault(x)));
        fileService.Setup(x => x.SetDataAsync(It.IsAny<string>(), It.IsAny<string>())).Returns<string, string>(async (name, data) =>
        {
            var gate = Interlocked.Exchange(ref _writeGate, null);
            if (gate != null)
            {
                _writeReached.SetResult();
                await gate.Task;
            }

            _files[name] = data;
        });

        var options = Options.Create(new FileCacheOptions { CompanyName = "Tharga", AppName = "Test" });
        var fileFormatService = new FileFormatService(options, Mock.Of<ILogger<FileFormatService>>());
        _sut = new File(Mock.Of<IManagedCacheMonitor>(), fileService.Object, fileFormatService, Mock.Of<IHostEnvironment>(), options);
    }

    [Fact]
    public async Task Touch_with_the_read_item_sets_the_update_time()
    {
        //Arrange
        await _sut.SetAsync(StoredKey, BuildItem("first"), false);
        var read = await _sut.GetAsync<string>(StoredKey);

        //Act
        var touched = await _sut.BuyMoreTime(StoredKey, read, false);

        //Assert
        touched.Should().BeTrue();
        (await _sut.GetAsync<string>(StoredKey)).UpdateTime.Should().NotBeNull();
    }

    [Fact]
    public async Task Touch_with_a_replaced_item_keeps_the_newer_data()
    {
        //Arrange
        await _sut.SetAsync(StoredKey, BuildItem("first"), false);
        var read = await _sut.GetAsync<string>(StoredKey);
        await _sut.SetAsync(StoredKey, BuildItem("second") with { CreateTime = read.CreateTime.AddTicks(1) }, false);

        //Act
        var touched = await _sut.BuyMoreTime(StoredKey, read, false);

        //Assert
        touched.Should().BeFalse();
        var stored = await _sut.GetAsync<string>(StoredKey);
        stored.Data.Should().Be("second");
        stored.UpdateTime.Should().BeNull();
    }

    [Fact]
    public async Task Replace_during_a_touch_waits_for_the_touch_and_wins()
    {
        //Arrange
        await _sut.SetAsync(StoredKey, BuildItem("first"), false);
        var read = await _sut.GetAsync<string>(StoredKey);
        _writeReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _writeGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = _writeGate;

        //Act
        var touch = _sut.BuyMoreTime(StoredKey, read, false);
        await _writeReached.Task.WaitAsync(WaitTimeout, TestContext.Current.CancellationToken);
        var replace = _sut.SetAsync(StoredKey, BuildItem("second") with { CreateTime = read.CreateTime.AddTicks(1) }, false);
        var replaceWaited = !replace.IsCompleted;
        gate.SetResult();
        await Task.WhenAll(touch, replace).WaitAsync(WaitTimeout, TestContext.Current.CancellationToken);

        //Assert
        replaceWaited.Should().BeTrue("the touch holds the entry between its read and its write");
        (await touch).Should().BeTrue();
        (await _sut.GetAsync<string>(StoredKey)).Data.Should().Be("second", "a replace is never overwritten by an older touch");
    }

    private static CacheItem<string> BuildItem(string data)
    {
        return new CacheItem<string> { KeyParts = [], CreateTime = DateTime.UtcNow, FreshSpan = TimeSpan.FromMinutes(10), Data = data };
    }
}
