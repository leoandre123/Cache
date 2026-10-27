using FluentAssertions;
using StackExchange.Redis;
using Xunit;

namespace Tharga.Cache.Redis.Tests;

public class RedisScriptingErrorsTests
{
    [Theory]
    [InlineData("NOPERM User tharga has no permissions to run the 'evalsha' command")]
    [InlineData("NOPERM User tharga has no permissions to run the 'eval' command")]
    [InlineData("NOPERM this user has no permissions to run the 'evalsha' command or its subcommand")]
    [InlineData("NOPERM User tharga has no permissions to run the 'script|load' command")]
    [InlineData("ERR unknown command 'EVALSHA', with args beginning with: 'abc' '1' 'String.a'")]
    [InlineData("ERR unknown command `EVAL`, with args beginning with: `local current = redis.call('GET', KEYS[1])`")]
    [InlineData("ERR unsupported command 'eval'")]
    [InlineData("ERR unknown command")]
    [InlineData("ERR command not supported: EVALSHA")]
    [InlineData("ERR Lua scripting is disabled")]
    [InlineData("ERR scripting is not supported by this server")]
    public void Server_errors_that_mean_scripting_is_unavailable(string message)
    {
        //Act
        var result = RedisScriptingErrors.IsScriptingUnavailable(RedisErrors.Server(message));

        //Assert
        result.Should().BeTrue();
    }

    [Theory]
    [InlineData("NOSCRIPT No matching script. Please use EVAL.")]
    [InlineData("NOPERM No permissions to access a key")]
    [InlineData("NOPERM User tharga has no permissions to run the 'set' command")]
    [InlineData("BUSY Redis is busy running a script. You can only call SCRIPT KILL or SHUTDOWN NOSAVE.")]
    [InlineData("ERR Error running script (call to f_0123): @user_script:1: Script attempted to access nonexistent global variable")]
    [InlineData("ERR unknown command 'FOO', with args beginning with: 'eval' 'script'")]
    [InlineData("LOADING Redis is loading the dataset in memory")]
    [InlineData("READONLY You can't write against a read only replica.")]
    [InlineData("OOM command not allowed when used memory > 'maxmemory'.")]
    public void Server_errors_that_do_not_mean_scripting_is_unavailable(string message)
    {
        //Act
        var result = RedisScriptingErrors.IsScriptingUnavailable(RedisErrors.Server(message));

        //Assert
        result.Should().BeFalse();
    }

    [Fact]
    public void Command_map_that_disables_EVAL_means_scripting_is_unavailable()
    {
        //Act
        var result = RedisScriptingErrors.IsScriptingUnavailable(new RedisCommandException("This operation has been disabled in the command-map and cannot be used: EVALSHA"));

        //Assert
        result.Should().BeTrue();
    }

    [Fact]
    public void Command_map_that_disables_another_command_does_not()
    {
        //Act
        var result = RedisScriptingErrors.IsScriptingUnavailable(new RedisCommandException("This operation has been disabled in the command-map and cannot be used: KEYS"));

        //Assert
        result.Should().BeFalse();
    }

    [Fact]
    public void Timeouts_and_connection_errors_do_not()
    {
        //Act
        var timeout = RedisScriptingErrors.IsScriptingUnavailable(RedisErrors.Timeout());
        var connection = RedisScriptingErrors.IsScriptingUnavailable(RedisErrors.Connection());
        var plainTimeout = RedisScriptingErrors.IsScriptingUnavailable(new TimeoutException("EVALSHA"));

        //Assert
        timeout.Should().BeFalse();
        connection.Should().BeFalse();
        plainTimeout.Should().BeFalse();
    }
}
