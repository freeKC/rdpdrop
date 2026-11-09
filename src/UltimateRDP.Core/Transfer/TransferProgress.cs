namespace UltimateRDP.Core.Transfer;

/// <summary>Unit of a <see cref="TimeLeft"/>.</summary>
public enum TimeLeftUnit
{
    /// <summary>Under a minute: <see cref="TimeLeft.Seconds"/>, rounded up to five seconds.</summary>
    Seconds,

    /// <summary>Under an hour: <see cref="TimeLeft.Minutes"/>, rounded up.</summary>
    Minutes,

    /// <summary><see cref="TimeLeft.Hours"/> and <see cref="TimeLeft.Minutes"/>, rounded up to the minute.</summary>
    Hours,
}

/// <summary>A remaining time, rounded for the status bar.</summary>
public readonly record struct TimeLeft(TimeLeftUnit Unit, int Hours, int Minutes, int Seconds)
{
    /// <summary>Rounds <paramref name="remaining"/> up, as described by <see cref="TimeLeftUnit"/>.</summary>
    public static TimeLeft From(TimeSpan remaining)
    {
        var seconds = (long)Math.Ceiling(Math.Max(0, remaining.TotalSeconds));
        if (seconds < 60)
        {
            var rounded = (int)Math.Max(5, (seconds + 4) / 5 * 5);
            return rounded >= 60 ? new TimeLeft(TimeLeftUnit.Minutes, 0, 1, 0) : new TimeLeft(TimeLeftUnit.Seconds, 0, 0, rounded);
        }

        var minutes = (seconds + 59) / 60;
        if (minutes < 60)
        {
            return new TimeLeft(TimeLeftUnit.Minutes, 0, (int)minutes, 0);
        }

        var hours = (int)Math.Min(int.MaxValue, minutes / 60);
        return new TimeLeft(TimeLeftUnit.Hours, hours, (int)(minutes % 60), 0);
    }
}

/// <summary>
/// Progress of a transfer as the client shows it: the share done, the rate over the last
/// few seconds and the time left, refreshed at most four times a second.
/// </summary>
/// <remarks>This class is not thread safe. Times are in milliseconds from any origin.</remarks>
public sealed class TransferProgress
{
    /// <summary>Shortest time between two refreshes of the text.</summary>
    public const int RefreshIntervalMilliseconds = 250;

    /// <summary>The rate is measured over about this long.</summary>
    public const int RateWindowMilliseconds = 5000;

    /// <summary>No rate is given before this much time has passed.</summary>
    public const int MinimumRateMilliseconds = 500;

    private readonly Queue<(long Time, long Bytes)> _samples = new();
    private long _lastRefresh = long.MinValue;

    /// <param name="totalBytes">Bytes of the whole transfer.</param>
    /// <param name="startMilliseconds">When the transfer started.</param>
    public TransferProgress(long totalBytes, long startMilliseconds)
    {
        TotalBytes = Math.Max(0, totalBytes);
        StartMilliseconds = startMilliseconds;
        LastSample = (startMilliseconds, 0);
        _samples.Enqueue(LastSample);
    }

    /// <summary>Bytes of the whole transfer.</summary>
    public long TotalBytes { get; }

    /// <summary>When the transfer started.</summary>
    public long StartMilliseconds { get; }

    /// <summary>Bytes done, as last reported.</summary>
    public long DoneBytes { get; private set; }

    /// <summary>Share done, from 0 to 100, rounded down. An empty transfer is at 100.</summary>
    public int Percent => TotalBytes == 0 ? 100 : (int)Math.Clamp(DoneBytes * 100.0 / TotalBytes, 0, 100);

    /// <summary>Bytes per second over the last <see cref="RateWindowMilliseconds"/>, or null while too early.</summary>
    public double? BytesPerSecond
    {
        get
        {
            var first = _samples.Peek();
            var last = LastSample;
            var span = last.Time - first.Time;
            return span < MinimumRateMilliseconds ? null : (last.Bytes - first.Bytes) * 1000.0 / span;
        }
    }

    /// <summary>Time left at the current rate, or null when the rate is unknown or zero.</summary>
    public TimeSpan? Remaining => BytesPerSecond is > 0 and var rate
        ? TimeSpan.FromSeconds(Math.Min((TotalBytes - DoneBytes) / rate, TimeSpan.MaxValue.TotalSeconds / 2))
        : null;

    private (long Time, long Bytes) LastSample { get; set; }

    /// <summary>Records the bytes done at <paramref name="nowMilliseconds"/>.</summary>
    public void Report(long nowMilliseconds, long doneBytes)
    {
        DoneBytes = Math.Clamp(doneBytes, DoneBytes, Math.Max(DoneBytes, TotalBytes));
        LastSample = (nowMilliseconds, DoneBytes);
        _samples.Enqueue(LastSample);

        // Keep one sample older than the window, so that the rate spans the whole window.
        while (_samples.Count > 2 && _samples.ElementAt(1).Time <= nowMilliseconds - RateWindowMilliseconds)
        {
            _samples.Dequeue();
        }
    }

    /// <summary>
    /// True when the text may be refreshed at <paramref name="nowMilliseconds"/>, at most
    /// once per <see cref="RefreshIntervalMilliseconds"/>; the refresh is then counted.
    /// </summary>
    public bool TryRefresh(long nowMilliseconds)
    {
        if (_lastRefresh != long.MinValue && nowMilliseconds - _lastRefresh < RefreshIntervalMilliseconds)
        {
            return false;
        }

        _lastRefresh = nowMilliseconds;
        return true;
    }
}
