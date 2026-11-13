namespace UltimateRDP.Core.Direct;

/// <summary>The rates of a direct transfer, in bytes per second, for the summary line.</summary>
/// <param name="Average">Over the whole time from <see cref="DirectRateMeter.Start"/> to <see cref="DirectRateMeter.Stop"/>.</param>
/// <param name="First">Over the first <see cref="DirectRateMeter.WindowMilliseconds"/>, or the whole time when shorter.</param>
/// <param name="Last">Over the last <see cref="DirectRateMeter.WindowMilliseconds"/>, or the whole time when shorter.</param>
public readonly record struct DirectRates(double Average, double First, double Last);

/// <summary>
/// Counts the bytes of a direct transfer and keeps a sample of the total about every
/// second, taken by <see cref="Sample"/> from a timer of the caller, so that the summary
/// gives the rate of the first and of the last seconds besides the average: the first
/// production transfers started at 135 to 180 MB/s and fell to about 20 MB/s.
/// </summary>
/// <remarks>Thread safe.</remarks>
public sealed class DirectRateMeter
{
    /// <summary>The span of the first and last rates of the summary.</summary>
    public const long WindowMilliseconds = 5000;

    private readonly Lock _gate = new();
    private readonly Func<long> _clock;
    private readonly List<(long Milliseconds, long Bytes)> _samples = [];
    private long _bytes;
    private long _start = -1;
    private long _end = -1;
    private long _lastMilliseconds;
    private long _lastBytes;

    /// <param name="clockMilliseconds">A clock in milliseconds.</param>
    public DirectRateMeter(Func<long> clockMilliseconds)
    {
        ArgumentNullException.ThrowIfNull(clockMilliseconds);
        _clock = clockMilliseconds;
    }

    /// <summary>Bytes counted so far.</summary>
    public long Bytes => Interlocked.Read(ref _bytes);

    /// <summary>True between <see cref="Start"/> and <see cref="Stop"/>.</summary>
    public bool IsRunning
    {
        get
        {
            lock (_gate)
            {
                return _start >= 0 && _end < 0;
            }
        }
    }

    /// <summary>Milliseconds from <see cref="Start"/> to <see cref="Stop"/>, or until now while it runs; 0 before the start.</summary>
    public long ElapsedMilliseconds
    {
        get
        {
            lock (_gate)
            {
                return _start < 0 ? 0 : Math.Max(0, (_end >= 0 ? _end : _clock()) - _start);
            }
        }
    }

    /// <summary>The data starts to flow. Only the first call counts.</summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_start >= 0)
            {
                return;
            }

            _start = _clock();
            _lastMilliseconds = _start;
            _lastBytes = Bytes;
            _samples.Add((0, _lastBytes));
        }
    }

    /// <summary>Counts <paramref name="bytes"/>. Callable from any thread, many times a second.</summary>
    public void Add(long bytes) => Interlocked.Add(ref _bytes, bytes);

    /// <summary>
    /// Keeps the total of now, and returns the rate in bytes per second since the previous
    /// sample, or null before the start, after the stop, or when no time passed.
    /// </summary>
    public double? Sample()
    {
        lock (_gate)
        {
            if (_start < 0 || _end >= 0)
            {
                return null;
            }

            var now = _clock();
            var bytes = Bytes;
            if (now <= _lastMilliseconds)
            {
                return null;
            }

            var rate = (bytes - _lastBytes) * 1000.0 / (now - _lastMilliseconds);
            _samples.Add((now - _start, bytes));
            _lastMilliseconds = now;
            _lastBytes = bytes;
            return rate;
        }
    }

    /// <summary>The transfer is over: the last total is kept. Only the first call counts, and only after the start.</summary>
    public void Stop()
    {
        lock (_gate)
        {
            if (_start < 0 || _end >= 0)
            {
                return;
            }

            _end = Math.Max(_clock(), _start);
            _samples.Add((_end - _start, Bytes));
        }
    }

    /// <summary>The average, first and last rates, until the stop or until now.</summary>
    public DirectRates Rates()
    {
        lock (_gate)
        {
            if (_start < 0)
            {
                return default;
            }

            var duration = (_end >= 0 ? _end : _clock()) - _start;
            if (duration <= 0)
            {
                return default;
            }

            List<(long Milliseconds, long Bytes)> samples = [.. _samples];
            if (_end < 0)
            {
                samples.Add((duration, Bytes));
            }

            var window = Math.Min(WindowMilliseconds, duration);
            var total = BytesAt(samples, duration);
            return new DirectRates(
                total * 1000.0 / duration,
                BytesAt(samples, window) * 1000.0 / window,
                (total - BytesAt(samples, duration - window)) * 1000.0 / window);
        }
    }

    /// <summary>The bytes at <paramref name="milliseconds"/> from the start, between the two samples around it.</summary>
    private static double BytesAt(List<(long Milliseconds, long Bytes)> samples, long milliseconds)
    {
        var before = samples[0];
        foreach (var sample in samples)
        {
            if (sample.Milliseconds >= milliseconds)
            {
                if (sample.Milliseconds == before.Milliseconds)
                {
                    return sample.Bytes;
                }

                var share = (milliseconds - before.Milliseconds) / (double)(sample.Milliseconds - before.Milliseconds);
                return before.Bytes + ((sample.Bytes - before.Bytes) * share);
            }

            before = sample;
        }

        return before.Bytes;
    }
}
