using System.Diagnostics;
using System.Globalization;
using UltimateRDP.Core.Protocol;

namespace UltimateRDP.Agent.Channel;

/// <summary>Where a transfer started, for <see cref="ChannelMeter.Summary"/>.</summary>
/// <param name="Reads">The read counters then.</param>
/// <param name="Timestamp">The time then, in ticks of <see cref="Stopwatch"/>.</param>
/// <param name="Session">The traffic counters of the session then, or null.</param>
internal readonly record struct ChannelMark(ChannelReadSnapshot Reads, long Timestamp, SessionCounters? Session);

/// <summary>
/// Measures what the channel did during one transfer: the reads of the channel thread
/// (<see cref="ChannelReadStats"/>) and the traffic counters of the session. A transfer
/// takes a <see cref="Mark"/> when it starts and logs the <see cref="Summary"/> when it
/// ends.
/// </summary>
internal sealed class ChannelMeter(ChannelReadStats reads)
{
    /// <summary>The counters of the channel thread.</summary>
    public ChannelReadStats Reads { get; } = reads;

    /// <summary>The counters now.</summary>
    public ChannelMark Mark() => new(Reads.Snapshot(), Stopwatch.GetTimestamp(), SessionTraffic.Counters());

    /// <summary>The summary lines of the transfer <paramref name="transferId"/> since <paramref name="start"/>.</summary>
    public IReadOnlyList<string> Summary(string transferId, ChannelMark start)
    {
        var end = Mark();
        var elapsed = Stopwatch.GetElapsedTime(start.Timestamp, end.Timestamp).TotalMilliseconds;
        var lines = new List<string>
        {
            (end.Reads - start.Reads).Describe(transferId, Reads.ChunkLength, Stopwatch.Frequency, elapsed, Reads.ReadMode),
        };

        if (start.Session is { } before && end.Session is { } after)
        {
            // 32 bit counters that may wrap; unchecked differences stay right across one wrap.
            lines.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"Transfer {transferId} session counters: {unchecked(after.IncomingBytes - before.IncomingBytes)} bytes, {unchecked(after.IncomingFrames - before.IncomingFrames)} frames and {unchecked(after.IncomingCompressedBytes - before.IncomingCompressedBytes)} compressed bytes in, {unchecked(after.OutgoingBytes - before.OutgoingBytes)} bytes out, from WTSSessionInfoEx (zero when Windows does not count them)."));
        }

        return lines;
    }
}
