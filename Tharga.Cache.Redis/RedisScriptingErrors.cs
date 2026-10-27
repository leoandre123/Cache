using StackExchange.Redis;

namespace Tharga.Cache.Redis;

/// <summary>
/// Tells an error that means the server will not run Lua scripts at all (no permission, command not implemented,
/// scripting disabled) from one that is transient or specific to a call. Only the first kind switches the
/// compare-and-set to its fallback; everything else is left to the resilience policy as before.
/// </summary>
internal static class RedisScriptingErrors
{
    private const string NoPermissionPrefix = "NOPERM";
    private const string NoScriptPrefix = "NOSCRIPT";
    private const string ArgumentsMarker = "with args beginning with";
    private const string CommandMapDisabledPhrase = "disabled in the command-map";

    private static readonly char[] CommandQuotes = ['\'', '`', '"'];
    private static readonly string[] ScriptCommands = ["eval", "script"];
    private static readonly string[] UnknownCommandPhrases = ["unknown command", "unknown subcommand", "unsupported command", "command not supported", "command is not supported", "not supported command"];
    private static readonly string[] ScriptingWords = ["lua", "script"];
    private static readonly string[] DisabledPhrases = ["disabled", "not supported", "unsupported", "not enabled", "not available"];
    private static readonly string[] ScriptRuntimeErrorPhrases = ["error running script", "user_script", "busy"];

    /// <summary>
    /// True for a <see cref="RedisServerException"/> that says scripting is unavailable: <c>NOPERM</c> naming
    /// <c>EVAL</c>/<c>EVALSHA</c>/<c>SCRIPT</c>, an unknown or unsupported <c>EVAL</c>/<c>EVALSHA</c>/<c>SCRIPT</c>
    /// command (or one that quotes no command name), or wording that says Lua or scripting is disabled or not
    /// supported. Also true for a
    /// <see cref="RedisCommandException"/> from a command map that disables those commands on the client.
    /// <c>NOSCRIPT</c> (handled by StackExchange.Redis, which loads the script again), errors raised while a script
    /// runs, timeouts and connection errors are not.
    /// </summary>
    public static bool IsScriptingUnavailable(Exception exception)
    {
        return exception switch
        {
            RedisServerException serverException => IsScriptingUnavailableError(serverException.Kind, serverException.Message),
            RedisCommandException commandException => Contains(commandException.Message, CommandMapDisabledPhrase) && NamesScriptCommand(commandException.Message),
            _ => false
        };
    }

    private static bool IsScriptingUnavailableError(RedisErrorKind kind, string message)
    {
        if (kind is RedisErrorKind.NoScript or RedisErrorKind.Busy) return false;
        if (string.IsNullOrEmpty(message) || message.StartsWith(NoScriptPrefix, StringComparison.OrdinalIgnoreCase)) return false;

        var error = WithoutArguments(message);
        if (ContainsAny(error, ScriptRuntimeErrorPhrases)) return false;
        if (error.StartsWith(NoPermissionPrefix, StringComparison.OrdinalIgnoreCase)) return NamesScriptCommand(error);
        if (ContainsAny(error, UnknownCommandPhrases)) return NamesScriptCommand(error) || !NamesAnyCommand(error);

        return ContainsAny(error, ScriptingWords) && ContainsAny(error, DisabledPhrases);
    }

    /// <summary>
    /// An unknown-command error repeats the start of the arguments, which for <c>EVAL</c> is the script itself; only
    /// the part that names the command is looked at.
    /// </summary>
    private static string WithoutArguments(string message)
    {
        var argumentsIndex = message.IndexOf(ArgumentsMarker, StringComparison.OrdinalIgnoreCase);
        return argumentsIndex < 0 ? message : message[..argumentsIndex];
    }

    private static bool NamesScriptCommand(string error)
    {
        return ContainsAny(error, ScriptCommands);
    }

    /// <summary>
    /// The error comes from the script call, so an unknown-command error that does not quote a command name (as some
    /// proxies send it) can only be about <c>EVAL</c> or <c>EVALSHA</c>.
    /// </summary>
    private static bool NamesAnyCommand(string error)
    {
        return error.IndexOfAny(CommandQuotes) >= 0;
    }

    private static bool ContainsAny(string value, string[] phrases)
    {
        return phrases.Any(x => Contains(value, x));
    }

    private static bool Contains(string value, string phrase)
    {
        return value.Contains(phrase, StringComparison.OrdinalIgnoreCase);
    }
}
