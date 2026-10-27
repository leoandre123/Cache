using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using Moq;
using Tharga.Cache.Core;
using Tharga.MongoDB;
using Xunit;
using Sut = Tharga.Cache.MongoDB.MongoDB;

namespace Tharga.Cache.MongoDB.Tests;

/// <summary>
/// Asserts on the operations the MongoDB persist sends, against a mocked collection.
/// </summary>
public class MongoDBRoundTripTests
{
    private const string CacheKey = "String.my-key";
    private readonly Mock<ICacheRepositoryCollection> _collection = new();
    private readonly Sut _sut;

    public MongoDBRoundTripTests()
    {
        var collectionProvider = new Mock<ICollectionProvider>();
        collectionProvider
            .Setup(x => x.GetCollection<ICacheRepositoryCollection, CacheEntity, string>(It.IsAny<DatabaseContext>()))
            .Returns(_collection.Object);

        _sut = new Sut(collectionProvider.Object, Mock.Of<IManagedCacheMonitor>(), Options.Create(new MongoDBCacheOptions()), Mock.Of<ILogger<Sut>>());
    }

    [Fact]
    public async Task Touch_with_the_read_item_updates_only_the_entry_that_was_read()
    {
        //Arrange
        var createTime = new DateTime(2026, 10, 23, 12, 0, 0, DateTimeKind.Utc);
        var item = new CacheItem<string> { KeyParts = [], CreateTime = createTime, FreshSpan = TimeSpan.FromMinutes(1), Data = "value" };
        var entity = BuildEntity(CacheKey, createTime);
        FilterDefinition<CacheEntity> captured = null;
        _collection
            .Setup(x => x.UpdateOneAsync(It.IsAny<FilterDefinition<CacheEntity>>(), It.IsAny<UpdateDefinition<CacheEntity>>(), It.IsAny<OneOption<CacheEntity>>(), It.IsAny<IClientSessionHandle>(), It.IsAny<CancellationToken>()))
            .Callback<FilterDefinition<CacheEntity>, UpdateDefinition<CacheEntity>, OneOption<CacheEntity>, IClientSessionHandle, CancellationToken>((f, _, _, _, _) => captured = f)
            .ReturnsAsync(BuildChangeResult(entity));

        //Act
        var touched = await _sut.BuyMoreTime<string>(CacheKey, item, false);

        //Assert
        touched.Should().BeTrue();
        _collection.Verify(x => x.GetOneAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never, "the touch must not read the item again");
        var rendered = Render(captured);
        rendered.Names.Should().BeEquivalentTo("_id", "CreateTime", "UpdateTime");
        rendered["_id"].AsString.Should().Be(CacheKey);
        rendered["CreateTime"].ToUniversalTime().Should().Be(createTime);
        rendered["UpdateTime"].IsBsonNull.Should().BeTrue();
    }

    private static CacheEntity BuildEntity(string id, DateTime createTime)
    {
        return new CacheEntity
        {
            Id = id,
            Type = typeof(string).AssemblyQualifiedName,
            Data = JsonSerializer.Serialize(id),
            CreateTime = createTime,
            FreshSpan = TimeSpan.FromMinutes(10),
            StaleWhileRevalidate = false
        };
    }

    private static EntityChangeResult<CacheEntity> BuildChangeResult(CacheEntity before)
    {
        return (EntityChangeResult<CacheEntity>)Activator.CreateInstance(typeof(EntityChangeResult<CacheEntity>), BindingFlags.NonPublic | BindingFlags.Instance, null, [before, before], null);
    }

    private static BsonDocument Render(FilterDefinition<CacheEntity> filter)
    {
        return filter.Render(new RenderArgs<CacheEntity>(BsonSerializer.SerializerRegistry.GetSerializer<CacheEntity>(), BsonSerializer.SerializerRegistry));
    }
}
