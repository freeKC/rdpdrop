using System.Diagnostics;
using System.Globalization;
using UltimateRDP.Core.Protocol;

namespace UltimateRDP.Agent.Channel;

/// <summary>What is measured for one transfer, from <see cref="ChannelMeter.Begin"/> to <see cref="ChannelMeter.End"/>.</summary>
/// <param name="Reads">The read counters of the channel thread for this transfer.</param>
/// <param name="Timestamp">When the transfer started, in ticks of <see cref="Stopwatch"/>.</param>
/// <param name="Session">The traffic counters of the session then, or null.</param>
internal sealed record ChannelMeasure(ChannelReadStats Reads, long Timestamp, SessionCounters? Session);

/// <summary>
/// Measures what the channel did during one transfer: the reads of the channel thread
/// (<see cref="ChannelReadStats"/>) and the traffic counters of the session. A transfer
/// calls <see cref="Begin"/> when it starts and <see cref="End"/> when it ends; the
/// counters exist only in between, so that nothing is measured without a transfer.
/// </summary>
internal sealed class ChannelMeter(ChannelLoop loop)
{
    /// <summary>Starts counting for a transfer.</summary>
    public ChannelMeasure Begin() => new(loop.StartReadMeasure(), Stopwatch.GetTimestamp(), SessionTraffic.Counters());

    /// <summary>Stops counting and gives the summary lines of the transfer <paramref name="transferId"/>.</summary>
    public IReadOnlyList<string> End(string transferId, ChannelMeasure measure)
    {
        loop.StopReadMeasure(measure.Reads);
        var elapsed = Stopwatch.GetElapsedTime(measure.Timestamp).TotalMilliseconds;
        var lines = new List<string>
        {
            measure.Reads.Snapshot().Describe(transferId, measure.Reads.ChunkLength, Stopwatch.Frequency, elapsed, loop.ReadMode),
        };

        if (measure.Session is { } before && SessionTraffic.Counters() is { } after)
        {
            // 32 bit counters that may wrap; unchecked differences stay right across one wrap.
            lines.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"Transfer {transferId} session counters: {unchecked(after.IncomingBytes - before.IncomingBytes)} bytes, {unchecked(after.IncomingFrames - before.IncomingFrames)} frames and {unchecked(after.IncomingCompressedBytes - before.IncomingCompressedBytes)} compressed bytes in, {unchecked(after.OutgoingBytes - before.OutgoingBytes)} bytes out, from WTSSessionInfoEx (zero when Windows does not count them)."));
        }

        return lines;
    }
}
