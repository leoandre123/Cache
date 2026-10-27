using StackExchange.Redis;
using Xunit;

namespace Tharga.Cache.Redis.Tests;

/// <summary>
/// One connection to the Redis server named by <see cref="ConnectionStringVariable"/>, shared by the tests of a class.
/// <see cref="Connection"/> is null when the variable is not set.
/// </summary>
public class RedisServerFixture : IAsyncLifetime
{
    public const string ConnectionStringVariable = "THARGA_CACHE_TEST_REDIS";

    public ConnectionMultiplexer Connection { get; private set; }

    public async ValueTask InitializeAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable(ConnectionStringVariable);
        if (string.IsNullOrEmpty(connectionString)) return;

        Connection = await ConnectionMultiplexer.ConnectAsync(connectionString);
    }

    public async ValueTask DisposeAsync()
    {
        if (Connection != null) await Connection.DisposeAsync();
    }
}
