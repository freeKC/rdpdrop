using System.Globalization;

namespace UltimateRDP.Core.Transfer;

/// <summary>One acknowledgement matched with the call that carried its last byte.</summary>
/// <param name="AcknowledgedBytes">Bytes of the transfer acknowledged.</param>
/// <param name="RoundTripTicks">Time from the end of that call to the acknowledgement.</param>
/// <param name="InFlightBytes">Bytes handed to the control and not yet acknowledged, when the acknowledgement came.</param>
public readonly record struct AckSample(long AcknowledgedBytes, long RoundTripTicks, long InFlightBytes);

/// <summary>
/// The round trip of the acknowledgements of one transfer, seen from the client: when the
/// call that carried a chunk returned, and when the agent acknowledged the last byte of
/// that chunk, with the bytes in flight at that moment.
/// </summary>
/// <remarks>
/// Times are in ticks of <see cref="System.Diagnostics.Stopwatch"/>. The agent
/// acknowledges every 2 MiB and at the ends of the files. This class is not thread safe.
/// </remarks>
public sealed class AckRoundTrips
{
    private readonly Queue<(long Position, long Ticks)> _sent = new();
    private long _sentBytes;
    private long _sumRoundTrip;
    private long _minRoundTrip = long.MaxValue;
    private long _maxRoundTrip;
    private long _sumInFlight;
    private long _maxInFlight;

    /// <summary>Acknowledgements matched with a call.</summary>
    public long Count { get; private set; }

    /// <summary>Bytes of the transfer handed to the control so far.</summary>
    public long SentBytes => _sentBytes;

    /// <summary>
    /// A call that carried the chunk ending at <paramref name="transferPosition"/> returned at
    /// <paramref name="ticks"/>. Positions come in increasing order.
    /// </summary>
    public void OnSent(long transferPosition, long ticks)
    {
        if (transferPosition <= _sentBytes)
        {
            return;
        }

        _sent.Enqueue((transferPosition, ticks));
        _sentBytes = transferPosition;
    }

    /// <summary>
    /// The agent acknowledged the first <paramref name="acknowledgedBytes"/> of the transfer at
    /// <paramref name="ticks"/>. Returns the sample, or null when no call waits for that
    /// acknowledgement: a repeated one, or one for bytes that were never sent.
    /// </summary>
    public AckSample? OnAck(long acknowledgedBytes, long ticks)
    {
        while (_sent.Count > 0 && _sent.Peek().Position < acknowledgedBytes)
        {
            _sent.Dequeue();
        }

        if (_sent.Count == 0 || _sent.Peek().Position != acknowledgedBytes)
        {
            return null;
        }

        var (_, sentTicks) = _sent.Dequeue();
        var sample = new AckSample(acknowledgedBytes, Math.Max(0, ticks - sentTicks), _sentBytes - acknowledgedBytes);
        Count++;
        _sumRoundTrip += sample.RoundTripTicks;
        _minRoundTrip = Math.Min(_minRoundTrip, sample.RoundTripTicks);
        _maxRoundTrip = Math.Max(_maxRoundTrip, sample.RoundTripTicks);
        _sumInFlight += sample.InFlightBytes;
        _maxInFlight = Math.Max(_maxInFlight, sample.InFlightBytes);
        return sample;
    }

    /// <summary>
    /// The summary line of the client for a transfer, in English: the acknowledgements, their
    /// round trip on average, at least and at most, and the bytes in flight when they came.
    /// </summary>
    /// <param name="transferId">The transfer.</param>
    /// <param name="ticksPerSecond">The frequency of the ticks, <see cref="System.Diagnostics.Stopwatch.Frequency"/>.</param>
    /// <param name="windowBytes">The window of the transfer.</param>
    public string Describe(string transferId, long ticksPerSecond, long windowBytes)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(ticksPerSecond, 1);
        if (Count == 0)
        {
            return string.Create(CultureInfo.InvariantCulture, $"Transfer {transferId} acknowledgements: none matched a call; window {windowBytes} bytes.");
        }

        var average = _sumRoundTrip * 1000.0 / ticksPerSecond / Count;
        var min = _minRoundTrip * 1000.0 / ticksPerSecond;
        var max = _maxRoundTrip * 1000.0 / ticksPerSecond;
        var inFlight = (double)_sumInFlight / Count;
        return string.Create(
            CultureInfo.InvariantCulture,
            $"Transfer {transferId} acknowledgements: {Count} matched a call, round trip {average:0} ms on average, {min:0} ms at least, {max:0} ms at most; {inFlight:0} bytes in flight on average when one came, {_maxInFlight} at most, {Percent(inFlight, windowBytes):0} % of the window of {windowBytes} bytes.");
    }

    private static double Percent(double part, double whole) => whole > 0 ? part * 100 / whole : 0;
}
