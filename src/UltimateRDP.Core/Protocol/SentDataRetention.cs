namespace UltimateRDP.Core.Protocol;

/// <summary>
/// Keeps the memory of the strings handed to the control alive after the call returned,
/// until the agent surely received them, and says when each one may be freed.
/// </summary>
/// <remarks>
/// <para>
/// The control may send a string after its call returned. When the caller frees the
/// string at once, as the default marshalling does, the control can read memory that was
/// already given to something else, and send a wrong length or wrong content without
/// any error. The client therefore allocates each string itself and keeps it here.
/// </para>
/// <para>
/// A send is released as soon as the agent acknowledged a chunk of a later send: the
/// channel keeps the order of the data, so the agent then received every earlier send in
/// full. Otherwise a send without chunk is released <see cref="HoldTime"/> after it went
/// out, and a send that carries chunks <see cref="ChunkHoldTime"/> after it went out,
/// which only happens when its transfer ended without an acknowledgement. When more than
/// <see cref="MaxRetainedBytes"/> are kept, the oldest sends are released first.
/// </para>
/// <para>This class is not thread safe.</para>
/// </remarks>
/// <typeparam name="T">What is kept for each send, such as the pointer of the string.</typeparam>
public sealed class SentDataRetention<T>
{
    /// <summary>Most bytes kept by default: 64 MiB.</summary>
    public const long DefaultMaxRetainedBytes = 64L * 1024 * 1024;

    private readonly List<Entry> _entries = [];

    /// <param name="holdTime">How long a send without chunk is kept, 10 seconds by default.</param>
    /// <param name="chunkHoldTime">How long a send with chunks is kept when no acknowledgement releases it, 60 seconds by default.</param>
    /// <param name="maxRetainedBytes">Most bytes kept; beyond that the oldest sends are released.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxRetainedBytes"/> is below 1.</exception>
    public SentDataRetention(TimeSpan? holdTime = null, TimeSpan? chunkHoldTime = null, long maxRetainedBytes = DefaultMaxRetainedBytes)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxRetainedBytes, 1);
        HoldTime = holdTime ?? TimeSpan.FromSeconds(10);
        ChunkHoldTime = chunkHoldTime ?? TimeSpan.FromSeconds(60);
        MaxRetainedBytes = maxRetainedBytes;
    }

    /// <summary>How long a send without chunk is kept.</summary>
    public TimeSpan HoldTime { get; }

    /// <summary>How long a send with chunks is kept when no acknowledgement releases it.</summary>
    public TimeSpan ChunkHoldTime { get; }

    /// <summary>Most bytes kept.</summary>
    public long MaxRetainedBytes { get; }

    /// <summary>
    /// The limit for a transfer window of <paramref name="windowBytes"/>: four times the
    /// window, at least <see cref="DefaultMaxRetainedBytes"/>. A byte of a file takes about
    /// 2.7 bytes once in base64 in a string of two bytes per character, so the limit holds
    /// the window and half as much again, and the default window of 4 MiB keeps the
    /// default limit.
    /// </summary>
    public static long LimitForWindow(long windowBytes) => Math.Max(DefaultMaxRetainedBytes, 4 * Math.Max(0, windowBytes));

    /// <summary>Number of sends kept.</summary>
    public int Count => _entries.Count;

    /// <summary>Bytes kept, all sends together.</summary>
    public long RetainedBytes { get; private set; }

    /// <summary>
    /// Keeps a send. <paramref name="firstChunk"/> is the first chunk line of the send, or
    /// null when it holds none. Returns the sends to release because more than
    /// <see cref="MaxRetainedBytes"/> are kept, oldest first; the new one is kept even when
    /// it is larger than the limit on its own.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="bytes"/> is negative.</exception>
    public IReadOnlyList<T> Add(T item, long bytes, long nowMilliseconds, QueuedLine? firstChunk)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bytes);
        var chunk = firstChunk is { IsChunk: true };
        var hold = chunk ? ChunkHoldTime : HoldTime;
        _entries.Add(new Entry(item, bytes, nowMilliseconds + (long)hold.TotalMilliseconds, chunk ? firstChunk!.Subject : null, chunk ? firstChunk!.TransferPosition : -1));
        RetainedBytes += bytes;
        var count = 0;
        var excess = RetainedBytes;
        while (count < _entries.Count - 1 && excess > MaxRetainedBytes)
        {
            excess -= _entries[count].Bytes;
            count++;
        }

        return ReleaseFirst(count);
    }

    /// <summary>Releases the sends whose time is over, oldest first.</summary>
    public IReadOnlyList<T> ReleaseExpired(long nowMilliseconds)
    {
        List<T>? released = null;
        for (var i = 0; i < _entries.Count; i++)
        {
            var entry = _entries[i];
            if (nowMilliseconds >= entry.ExpiresMilliseconds)
            {
                (released ??= []).Add(entry.Item);
                RetainedBytes -= entry.Bytes;
            }
        }

        if (released is null)
        {
            return [];
        }

        _entries.RemoveAll(entry => nowMilliseconds >= entry.ExpiresMilliseconds);
        return released;
    }

    /// <summary>
    /// The agent acknowledged the first <paramref name="acknowledgedBytes"/> of the transfer:
    /// the sends before the last one whose first chunk is acknowledged arrived in full and
    /// are released, oldest first.
    /// </summary>
    public IReadOnlyList<T> ReleaseAcknowledged(string transferId, long acknowledgedBytes)
    {
        ArgumentNullException.ThrowIfNull(transferId);
        var count = 0;
        for (var i = _entries.Count - 1; i > 0; i--)
        {
            var entry = _entries[i];
            if (entry.FirstChunkPosition >= 0 && entry.FirstChunkPosition <= acknowledgedBytes && string.Equals(entry.TransferId, transferId, StringComparison.Ordinal))
            {
                count = i;
                break;
            }
        }

        return ReleaseFirst(count);
    }

    /// <summary>Releases every send, oldest first.</summary>
    public IReadOnlyList<T> ReleaseAll() => ReleaseFirst(_entries.Count);

    private IReadOnlyList<T> ReleaseFirst(int count)
    {
        if (count == 0)
        {
            return [];
        }

        var released = new T[count];
        for (var i = 0; i < count; i++)
        {
            released[i] = _entries[i].Item;
            RetainedBytes -= _entries[i].Bytes;
        }

        _entries.RemoveRange(0, count);
        return released;
    }

    private readonly record struct Entry(T Item, long Bytes, long ExpiresMilliseconds, string? TransferId, long FirstChunkPosition);
}
