namespace UltimateRDP.Core.Agent;

/// <summary>
/// Spaces the log lines of a state that lasts, such as "still waiting for a client
/// channel": the first line comes one <see cref="Interval"/> after the state began, then
/// one line per interval. Times are in milliseconds from any monotonic clock, such as
/// <see cref="Environment.TickCount64"/>. Not thread safe.
/// </summary>
public sealed class IdleReport
{
    private long? _since;

    /// <summary>Creates a report with the given spacing.</summary>
    public IdleReport(TimeSpan interval)
    {
        if (interval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(interval), "The interval must be positive.");
        }

        Interval = interval;
    }

    /// <summary>Time between two lines.</summary>
    public TimeSpan Interval { get; }

    /// <summary>
    /// Records that the state still holds at <paramref name="nowMilliseconds"/>. Returns
    /// true when a line is due; the next one is then due one interval later.
    /// </summary>
    public bool IsDue(long nowMilliseconds)
    {
        if (_since is not { } since)
        {
            _since = nowMilliseconds;
            return false;
        }

        if (nowMilliseconds - since < (long)Interval.TotalMilliseconds)
        {
            return false;
        }

        _since = nowMilliseconds;
        return true;
    }

    /// <summary>The state ended; the next call to <see cref="IsDue"/> starts a new one.</summary>
    public void Reset() => _since = null;
}
