using System.Globalization;

namespace UltimateRDP.Core.Protocol;

/// <summary>
/// Counters of the reads of the agent on the channel, kept for the whole run of the
/// channel thread. A transfer takes a <see cref="Snapshot"/> when it starts and another
/// when it ends; the difference is what the channel did during the transfer.
/// </summary>
/// <remarks>
/// <para>
/// Times are in ticks of <see cref="System.Diagnostics.Stopwatch"/>, so that a read costs
/// two calls of <see cref="System.Diagnostics.Stopwatch.GetTimestamp"/> and a few
/// additions. Blocked time is the time inside the read call, with or without data;
/// processing time is the rest of the loop, from the return of a read to the next call:
/// decoding, line reader and the handling of the messages, of which
/// <see cref="ChannelReadSnapshot.HandlingTicks"/> is the handling alone (JSON, base64,
/// checksum, file write).
/// </para>
/// <para>
/// Only the channel thread writes. Another thread may take a snapshot; it then reads
/// each counter as it is at that moment.
/// </para>
/// </remarks>
public sealed class ChannelReadStats
{
    private long _reads;
    private long _emptyReads;
    private long _bytes;
    private long _fullReads;
    private long _shortReads;
    private long _longReads;
    private long _headers;
    private long _blockedTicks;
    private long _processingTicks;
    private long _handlingTicks;

    /// <param name="chunkLength">The size of a full read, CHANNEL_CHUNK_LENGTH by default.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="chunkLength"/> is below 1.</exception>
    public ChannelReadStats(int chunkLength = ChannelPduFraming.DefaultChunkLength)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(chunkLength, 1);
        ChunkLength = chunkLength;
    }

    /// <summary>The size of a full read.</summary>
    public int ChunkLength { get; }

    /// <summary>How the channel is read, for the summary: set by the channel thread at each connection.</summary>
    public string ReadMode { get; set; } = "wts";

    /// <summary>
    /// A read returned. <paramref name="bytes"/> is the data of the client it gave, after
    /// any header was taken out; zero for a read that timed out.
    /// </summary>
    public void RecordRead(int bytes, long blockedTicks, int headers = 0)
    {
        Volatile.Write(ref _blockedTicks, _blockedTicks + blockedTicks);
        if (bytes <= 0)
        {
            Volatile.Write(ref _emptyReads, _emptyReads + 1);
            return;
        }

        Volatile.Write(ref _reads, _reads + 1);
        Volatile.Write(ref _bytes, _bytes + bytes);
        Volatile.Write(ref _headers, _headers + headers);
        if (bytes == ChunkLength)
        {
            Volatile.Write(ref _fullReads, _fullReads + 1);
        }
        else if (bytes < ChunkLength)
        {
            Volatile.Write(ref _shortReads, _shortReads + 1);
        }
        else
        {
            Volatile.Write(ref _longReads, _longReads + 1);
        }
    }

    /// <summary>The loop worked on the data of a read: <paramref name="ticks"/> from the return of the read to the next call.</summary>
    public void RecordProcessing(long ticks) => Volatile.Write(ref _processingTicks, _processingTicks + ticks);

    /// <summary>The handling of one message took <paramref name="ticks"/>; it is part of the processing time.</summary>
    public void RecordHandling(long ticks) => Volatile.Write(ref _handlingTicks, _handlingTicks + ticks);

    /// <summary>The counters as they are now.</summary>
    public ChannelReadSnapshot Snapshot() => new(
        Volatile.Read(ref _reads),
        Volatile.Read(ref _emptyReads),
        Volatile.Read(ref _bytes),
        Volatile.Read(ref _fullReads),
        Volatile.Read(ref _shortReads),
        Volatile.Read(ref _longReads),
        Volatile.Read(ref _headers),
        Volatile.Read(ref _blockedTicks),
        Volatile.Read(ref _processingTicks),
        Volatile.Read(ref _handlingTicks));
}

/// <summary>The counters of a <see cref="ChannelReadStats"/> at one moment, or the difference of two.</summary>
/// <param name="Reads">Reads that returned data.</param>
/// <param name="EmptyReads">Reads that timed out without data.</param>
/// <param name="Bytes">Data of the client read, headers taken out.</param>
/// <param name="FullReads">Reads of exactly one chunk length.</param>
/// <param name="ShortReads">Reads of less than one chunk length.</param>
/// <param name="LongReads">Reads of more than one chunk length.</param>
/// <param name="Headers">Channel headers taken out of the reads.</param>
/// <param name="BlockedTicks">Time inside the read calls.</param>
/// <param name="ProcessingTicks">Time between the return of a read and the next call.</param>
/// <param name="HandlingTicks">Part of the processing time spent handling messages.</param>
public readonly record struct ChannelReadSnapshot(
    long Reads,
    long EmptyReads,
    long Bytes,
    long FullReads,
    long ShortReads,
    long LongReads,
    long Headers,
    long BlockedTicks,
    long ProcessingTicks,
    long HandlingTicks)
{
    /// <summary>What happened between <paramref name="start"/> and <paramref name="end"/>.</summary>
    public static ChannelReadSnapshot operator -(ChannelReadSnapshot end, ChannelReadSnapshot start) => new(
        end.Reads - start.Reads,
        end.EmptyReads - start.EmptyReads,
        end.Bytes - start.Bytes,
        end.FullReads - start.FullReads,
        end.ShortReads - start.ShortReads,
        end.LongReads - start.LongReads,
        end.Headers - start.Headers,
        end.BlockedTicks - start.BlockedTicks,
        end.ProcessingTicks - start.ProcessingTicks,
        end.HandlingTicks - start.HandlingTicks);

    /// <summary>Average data per read that returned data, zero without such a read.</summary>
    public double AverageBytesPerRead => Reads > 0 ? (double)Bytes / Reads : 0;

    /// <summary>
    /// The summary line of the agent for a transfer, in English: for example
    /// "Transfer X channel reads: 1250 reads with data and 3 empty, 2000000 bytes, 1600 bytes per read on average;
    /// 1250 of 1600 bytes, 0 shorter, 0 longer; blocked in the read 9500 ms, processing 480 ms
    /// (320 ms handling messages), over 10000 ms; read mode wts.".
    /// </summary>
    /// <param name="transferId">The transfer.</param>
    /// <param name="chunkLength">The size of a full read.</param>
    /// <param name="ticksPerSecond">The frequency of the ticks, <see cref="System.Diagnostics.Stopwatch.Frequency"/>.</param>
    /// <param name="elapsedMilliseconds">The time between the two snapshots.</param>
    /// <param name="readMode">How the channel was read.</param>
    public string Describe(string transferId, int chunkLength, long ticksPerSecond, double elapsedMilliseconds, string readMode)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(ticksPerSecond, 1);
        var blocked = Milliseconds(BlockedTicks, ticksPerSecond);
        var processing = Milliseconds(ProcessingTicks, ticksPerSecond);
        var handling = Milliseconds(HandlingTicks, ticksPerSecond);
        var headers = Headers > 0 ? string.Create(CultureInfo.InvariantCulture, $", {Headers} channel headers taken out") : string.Empty;
        return string.Create(
            CultureInfo.InvariantCulture,
            $"Transfer {transferId} channel reads: {Reads} reads with data and {EmptyReads} empty, {Bytes} bytes, {AverageBytesPerRead:0} bytes per read on average; {FullReads} of {chunkLength} bytes, {ShortReads} shorter, {LongReads} longer{headers}; blocked in the read {blocked:0} ms, processing {processing:0} ms ({handling:0} ms handling messages), over {elapsedMilliseconds:0} ms; read mode {readMode}.");
    }

    private static double Milliseconds(long ticks, long ticksPerSecond) => ticks * 1000.0 / ticksPerSecond;
}
