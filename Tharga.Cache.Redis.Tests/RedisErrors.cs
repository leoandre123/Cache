using StackExchange.Redis;

namespace Tharga.Cache.Redis.Tests;

/// <summary>
/// Builds the exceptions StackExchange.Redis raises, with the error kind it would give the reply.
/// </summary>
internal static class RedisErrors
{
    private static readonly (string Prefix, RedisErrorKind Kind)[] Kinds =
    [
        ("NOPERM", RedisErrorKind.NoPermission),
        ("NOSCRIPT", RedisErrorKind.NoScript),
        ("BUSY", RedisErrorKind.Busy),
        ("LOADING", RedisErrorKind.Loading),
        ("READONLY", RedisErrorKind.ReadOnly),
        ("OOM", RedisErrorKind.OutOfMemory),
        ("ERR unknown command", RedisErrorKind.UnknownCommand),
        ("ERR", RedisErrorKind.UnknownError)
    ];

    public static RedisServerException Server(string message)
    {
        var kind = Kinds.FirstOrDefault(x => message.StartsWith(x.Prefix, StringComparison.Ordinal)).Kind;
        return new RedisServerException(kind == RedisErrorKind.None ? RedisErrorKind.Unknown : kind, CommandFlags.None, message);
    }

    public static RedisTimeoutException Timeout()
    {
        return new RedisTimeoutException(CommandFlags.None, "Timeout performing EVALSHA (5000ms)", CommandStatus.Sent);
    }

    public static RedisConnectionException Connection()
    {
        return new RedisConnectionException(ConnectionFailureType.SocketFailure, CommandFlags.None, "No connection is active/available to service this operation: EVALSHA", null, CommandStatus.Unknown);
    }
}
