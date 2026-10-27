using StackExchange.Redis;

namespace Tharga.Cache.Redis;

/// <summary>
/// Remembers, per connection, that the server rejected Lua scripting, so later compare-and-sets go straight to the
/// fallback without a failed script call first. Every <see cref="ReprobeInterval"/> one call tries the script again,
/// so a server whose permissions or configuration changed is picked up without a restart; the other calls keep using
/// the fallback meanwhile. A new connection is probed again from the start.
/// </summary>
internal class RedisScriptingAvailability
{
    public static readonly TimeSpan ReprobeInterval = TimeSpan.FromMinutes(10);

    private readonly object _lock = new();
    private readonly TimeProvider _timeProvider;
    private volatile IConnectionMultiplexer _unavailableOn;
    private DateTimeOffset _nextProbe;

    public RedisScriptingAvailability(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider;
    }

    /// <summary>
    /// False while scripting is known to be unavailable on <paramref name="connection"/>. True otherwise, and for the
    /// one call that gets the probe once <see cref="ReprobeInterval"/> has passed.
    /// </summary>
    public bool ShouldTryScript(IConnectionMultiplexer connection)
    {
        if (!ReferenceEquals(_unavailableOn, connection)) return true;

        lock (_lock)
        {
            if (!ReferenceEquals(_unavailableOn, connection)) return true;

            var now = _timeProvider.GetUtcNow();
            if (now < _nextProbe) return false;

            _nextProbe = now + ReprobeInterval;
            return true;
        }
    }

    /// <summary>
    /// Records that the server rejected scripting on <paramref name="connection"/>. Returns true only when scripting
    /// was considered available until now, so the switch to the fallback is logged once and not on every probe.
    /// </summary>
    public bool MarkUnavailable(IConnectionMultiplexer connection)
    {
        lock (_lock)
        {
            var wasAvailable = _unavailableOn == null;
            _unavailableOn = connection;
            _nextProbe = _timeProvider.GetUtcNow() + ReprobeInterval;
            return wasAvailable;
        }
    }

    /// <summary>
    /// Records that a script ran. Returns true if scripting had been considered unavailable, so its return is logged.
    /// </summary>
    public bool MarkAvailable()
    {
        if (_unavailableOn == null) return false;

        lock (_lock)
        {
            if (_unavailableOn == null) return false;

            _unavailableOn = null;
            return true;
        }
    }
}
