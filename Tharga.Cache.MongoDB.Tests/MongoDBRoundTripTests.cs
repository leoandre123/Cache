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
    public async Task GetManyAsync_reads_all_keys_with_one_find()
    {
        //Arrange
        var now = DateTime.UtcNow;
        FilterDefinition<CacheEntity> captured = null;
        _collection
            .Setup(x => x.GetAsync(It.IsAny<FilterDefinition<CacheEntity>>(), It.IsAny<Options<CacheEntity>>(), It.IsAny<CancellationToken>()))
            .Callback<FilterDefinition<CacheEntity>, Options<CacheEntity>, CancellationToken>((f, _, _) => captured = f)
            .Returns(ToAsync(BuildEntity("String.c", now), BuildEntity("String.a", now)));

        //Act
        var result = await _sut.GetManyAsync<string>(["String.a", "String.b", "String.c"]);

        //Assert
        _collection.Verify(x => x.GetAsync(It.IsAny<FilterDefinition<CacheEntity>>(), It.IsAny<Options<CacheEntity>>(), It.IsAny<CancellationToken>()), Times.Once);
        _collection.Verify(x => x.GetOneAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        Render(captured)["_id"]["$in"].AsBsonArray.Select(x => x.AsString).Should().Equal("String.a", "String.b", "String.c");
        result.Select(x => x.Key.Value).Should().Equal("String.a", "String.b", "String.c");
        result.Select(x => x.CacheItem?.Data).Should().Equal("String.a", null, "String.c");
    }

    [Fact]
    public async Task GetManyAsync_skips_an_entity_that_was_not_asked_for()
    {
        //Arrange
        var now = DateTime.UtcNow;
        _collection
            .Setup(x => x.GetAsync(It.IsAny<FilterDefinition<CacheEntity>>(), It.IsAny<Options<CacheEntity>>(), It.IsAny<CancellationToken>()))
            .Returns(ToAsync(BuildEntity("String.a", now), BuildEntity("String.unknown", now)));

        //Act
        var result = await _sut.GetManyAsync<string>(["String.a", "String.b"]);

        //Assert
        result.Select(x => x.CacheItem?.Data).Should().Equal("String.a", null);
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

    private static BsonDocument Render(FilterDefinition<CacheEntity> filter)
    {
        return filter.Render(new RenderArgs<CacheEntity>(BsonSerializer.SerializerRegistry.GetSerializer<CacheEntity>(), BsonSerializer.SerializerRegistry));
    }

    private static async IAsyncEnumerable<CacheEntity> ToAsync(params CacheEntity[] entities)
    {
        foreach (var entity in entities)
        {
            await Task.Yield();
            yield return entity;
        }
    }
}
