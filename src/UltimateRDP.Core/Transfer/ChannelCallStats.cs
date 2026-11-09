using System.Globalization;

namespace UltimateRDP.Core.Transfer;

/// <summary>
/// The calls of the client to SendOnVirtualChannel during one transfer: how many, how many
/// characters, and how long the UI thread stayed inside them.
/// </summary>
/// <remarks>
/// Times are in ticks of <see cref="System.Diagnostics.Stopwatch"/>. When the UI thread
/// spends a large share of the transfer inside the calls, the control limits the transfer.
/// This class is not thread safe.
/// </remarks>
public sealed class ChannelCallStats
{
    private long _firstStart = -1;
    private long _lastEnd;

    /// <summary>Number of calls.</summary>
    public long Calls { get; private set; }

    /// <summary>Characters handed to the control, all calls together.</summary>
    public long Characters { get; private set; }

    /// <summary>Time inside the calls, all calls together.</summary>
    public long TotalTicks { get; private set; }

    /// <summary>The maximum time of one call.</summary>
    public long MaxTicks { get; private set; }

    /// <summary>Time from the start of the first call to the end of the last one, zero before a call.</summary>
    public long SpanTicks => _firstStart < 0 ? 0 : _lastEnd - _firstStart;

    /// <summary>One call of <paramref name="characters"/>, from <paramref name="startTicks"/> to <paramref name="endTicks"/>.</summary>
    public void Record(int characters, long startTicks, long endTicks)
    {
        var ticks = Math.Max(0, endTicks - startTicks);
        Calls++;
        Characters += characters;
        TotalTicks += ticks;
        MaxTicks = Math.Max(MaxTicks, ticks);
        if (_firstStart < 0)
        {
            _firstStart = startTicks;
        }

        _lastEnd = Math.Max(_lastEnd, endTicks);
    }

    /// <summary>
    /// The summary line of the client for a transfer, in English: the calls, the characters,
    /// the time inside the calls with its average and maximum, and its share of the time
    /// from the first call to the last one and of the whole transfer.
    /// </summary>
    /// <param name="transferId">The transfer.</param>
    /// <param name="ticksPerSecond">The frequency of the ticks, <see cref="System.Diagnostics.Stopwatch.Frequency"/>.</param>
    /// <param name="transferMilliseconds">The time of the whole transfer, from the deferred drop to its end.</param>
    public string Describe(string transferId, long ticksPerSecond, double transferMilliseconds)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(ticksPerSecond, 1);
        var inCalls = Milliseconds(TotalTicks, ticksPerSecond);
        var span = Milliseconds(SpanTicks, ticksPerSecond);
        var average = Calls > 0 ? inCalls / Calls : 0;
        var max = Milliseconds(MaxTicks, ticksPerSecond);
        return string.Create(
            CultureInfo.InvariantCulture,
            $"Transfer {transferId} channel calls: {Calls} calls, {Characters} characters, {inCalls:0} ms inside SendOnVirtualChannel, {average:0.00} ms per call on average, {max:0.00} ms at most; {Percent(inCalls, span):0} % of the {span:0} ms from the first call to the last, {Percent(inCalls, transferMilliseconds):0} % of the {transferMilliseconds:0} ms of the transfer.");
    }

    private static double Milliseconds(long ticks, long ticksPerSecond) => ticks * 1000.0 / ticksPerSecond;

    private static double Percent(double part, double whole) => whole > 0 ? Math.Min(100, part * 100 / whole) : 0;
}
