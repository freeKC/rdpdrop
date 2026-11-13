namespace UltimateRDP.Core.Direct;

/// <summary>
/// A token bucket shared by the connections of one direct transfer, for the setting
/// <c>directMaxMegabytesPerSecond</c> of the agent. Every connection takes the bytes it
/// just read and waits the time it is told before it reads again: while it waits, TCP
/// flow control slows the client down. Bytes taken beyond the bucket become a debt that
/// every connection pays, so that the rate of all of them together stays at the limit.
/// </summary>
/// <remarks>Thread safe. It only computes waits; the caller sleeps.</remarks>
public sealed class DirectPacer
{
    /// <summary>The bucket holds at least two blocks of 1 MiB of the protocol.</summary>
    private const long MinimumBurst = 2L * 1024 * 1024;

    private readonly Lock _gate = new();
    private readonly Func<long> _clock;
    private readonly double _bytesPerMillisecond;
    private readonly long _burst;
    private double _tokens;
    private long _last;

    /// <param name="bytesPerSecond">The limit, above 0.</param>
    /// <param name="clockMilliseconds">A clock in milliseconds.</param>
    /// <param name="burstBytes">
    /// Most bytes that may go at once after a pause; 0 takes a tenth of a second at the
    /// limit, and at least two blocks of the protocol.
    /// </param>
    public DirectPacer(long bytesPerSecond, Func<long> clockMilliseconds, long burstBytes = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bytesPerSecond);
        ArgumentNullException.ThrowIfNull(clockMilliseconds);
        BytesPerSecond = bytesPerSecond;
        _clock = clockMilliseconds;
        _bytesPerMillisecond = bytesPerSecond / 1000.0;
        _burst = burstBytes > 0 ? burstBytes : Math.Max(MinimumBurst, bytesPerSecond / 10);
        _tokens = _burst;
        _last = clockMilliseconds();
    }

    /// <summary>The limit, in bytes per second.</summary>
    public long BytesPerSecond { get; }

    /// <summary>A pacer for a limit in millions of bytes per second; null for 0, which means no limit.</summary>
    public static DirectPacer? ForMegabytesPerSecond(int megabytesPerSecond, Func<long> clockMilliseconds) =>
        megabytesPerSecond > 0 ? new DirectPacer(megabytesPerSecond * 1_000_000L, clockMilliseconds) : null;

    /// <summary>
    /// Takes <paramref name="bytes"/> just read from the bucket and returns how many
    /// milliseconds the caller waits before it reads again: 0 while the rate stays within
    /// the limit.
    /// </summary>
    public long Take(long bytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bytes);
        lock (_gate)
        {
            var now = _clock();
            if (now > _last)
            {
                _tokens = Math.Min(_burst, _tokens + ((now - _last) * _bytesPerMillisecond));
                _last = now;
            }

            _tokens -= bytes;
            return _tokens >= 0 ? 0 : (long)Math.Ceiling(-_tokens / _bytesPerMillisecond);
        }
    }
}
