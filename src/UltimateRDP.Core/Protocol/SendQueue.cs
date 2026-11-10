namespace UltimateRDP.Core.Protocol;

/// <summary>One line waiting in a <see cref="SendQueue"/>.</summary>
/// <param name="Text">The line, ending with <c>\n</c>: ASCII, or a chunk in <c>u15</c>.</param>
/// <param name="Type">The type of the message, for the log.</param>
/// <param name="Subject">What the line belongs to: the id of the drag or the transfer, or a hello number.</param>
/// <param name="TransferPosition">
/// For a <c>fileChunk</c> line, the bytes of the whole transfer sent once this chunk is
/// counted, so that an acknowledgement of the agent can be matched with the sends that
/// carried it; -1 for any other line.
/// </param>
/// <param name="Seq">The number of a line of a transfer that numbers its lines, -1 otherwise.</param>
/// <param name="IsResend">True for a line sent again, at the request of the agent or because no acknowledgement came.</param>
/// <param name="Label">What the line is, for the log, such as the file of a <c>fileBegin</c>; null to use the type and the subject.</param>
public sealed record QueuedLine(string Text, string Type, string Subject, long TransferPosition = -1, long Seq = -1, bool IsResend = false, string? Label = null)
{
    /// <summary>True for a <c>fileChunk</c> line, which carries a <see cref="TransferPosition"/>.</summary>
    public bool IsChunk => TransferPosition >= 0;
}

/// <summary>Lines taken from a <see cref="SendQueue"/> to go out in one call of the control.</summary>
/// <param name="Text">The lines one after the other, as handed to the control.</param>
/// <param name="Lines">The lines, in their order.</param>
public sealed record SendBatch(string Text, IReadOnlyList<QueuedLine> Lines)
{
    /// <summary>The type of the first message, for the log.</summary>
    public string FirstType => Lines[0].Type;

    /// <summary>The first chunk line of the batch, or null when it holds none.</summary>
    public QueuedLine? FirstChunk => Lines.FirstOrDefault(line => line.IsChunk);

    /// <summary>
    /// The first chunk line of the batch sent for the first time, or null: an
    /// acknowledgement of its bytes proves that this call arrived. A chunk sent again may
    /// have arrived the first time, so its acknowledgement proves nothing about this call.
    /// </summary>
    public QueuedLine? FirstNewChunk => Lines.FirstOrDefault(line => line.IsChunk && !line.IsResend);
}

/// <summary>
/// Lines waiting to go out on the channel, sent together: the line protocol allows several
/// lines in one call of the control, and the line reader of the agent splits them. Fewer
/// calls means fewer chances for the control to lose a short string sent just before a
/// large one.
/// </summary>
/// <remarks>
/// <para>
/// The client queues every line as it is decided, and takes the batches at the end of the
/// current message of the UI thread, or at once when more than
/// <see cref="MaxBatchCharacters"/> wait. A batch keeps the order of the lines and holds
/// at most <see cref="MaxBatchCharacters"/> characters, except a single line longer than
/// that, which goes alone.
/// </para>
/// <para>
/// Since 0.5.1, short lines never leave alone just before a line that does not fit with
/// them: when the lines taken so far hold less than <see cref="SmallBatchCharacters"/>, or a
/// tenth of the limit when that is lower, and the next one does not fit, it joins them, and
/// the batch may go past the limit by less than <see cref="SmallBatchCharacters"/>. A short call followed within a few
/// milliseconds by a large one is the case where the control lost calls in production.
/// </para>
/// <para>This class is not thread safe.</para>
/// </remarks>
public sealed class SendQueue
{
    /// <summary>
    /// Most characters handed to the control in one call by default: 200 000, just above
    /// the largest call seen to work in production, 174 868 characters.
    /// </summary>
    public const int DefaultMaxBatchCharacters = 200_000;

    /// <summary>Smallest limit the client accepts in its settings.</summary>
    public const int MinBatchLimit = 50_000;

    /// <summary>Largest limit the client accepts in its settings: 1 MiB of characters.</summary>
    public const int MaxBatchLimit = 1024 * 1024;

    /// <summary>
    /// Below this many characters a batch is short: it should not leave alone just before a
    /// large line. The lost calls of production held 100 to 400 characters.
    /// </summary>
    public const int SmallBatchCharacters = 1000;

    private readonly Queue<QueuedLine> _lines = new();

    /// <param name="maxBatchCharacters">Most characters in one batch, at least 1.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxBatchCharacters"/> is below 1.</exception>
    public SendQueue(int maxBatchCharacters = DefaultMaxBatchCharacters)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxBatchCharacters, 1);
        MaxBatchCharacters = maxBatchCharacters;
    }

    /// <summary>Most characters in one batch.</summary>
    public int MaxBatchCharacters { get; }

    /// <summary>
    /// A limit from the settings, brought within <see cref="MinBatchLimit"/> and
    /// <see cref="MaxBatchLimit"/>.
    /// </summary>
    public static int ClampBatchLimit(int characters) => Math.Clamp(characters, MinBatchLimit, MaxBatchLimit);

    /// <summary>Below this many characters, a batch takes the next line along even when it does not fit.</summary>
    private int ShortBatchCharacters => Math.Min(SmallBatchCharacters, MaxBatchCharacters / 10);

    /// <summary>Number of lines waiting.</summary>
    public int Count => _lines.Count;

    /// <summary>Characters waiting, all lines together.</summary>
    public long QueuedCharacters { get; private set; }

    /// <summary>True when more than <see cref="MaxBatchCharacters"/> wait: a full batch should go out at once.</summary>
    public bool IsOverLimit => QueuedCharacters > MaxBatchCharacters;

    /// <summary>Adds a line at the end of the queue.</summary>
    /// <exception cref="ArgumentException">The line is empty.</exception>
    public void Enqueue(QueuedLine line)
    {
        ArgumentNullException.ThrowIfNull(line);
        if (line.Text.Length == 0)
        {
            throw new ArgumentException("A line holds at least its line feed.", nameof(line));
        }

        _lines.Enqueue(line);
        QueuedCharacters += line.Text.Length;
    }

    /// <summary>
    /// Takes the next batch: the first lines, in their order, as many as fit in
    /// <see cref="MaxBatchCharacters"/>, and at least one. Short lines that would end a
    /// batch just before a line that does not fit go with that line. Null when nothing waits.
    /// </summary>
    public SendBatch? TakeBatch()
    {
        if (_lines.Count == 0)
        {
            return null;
        }

        var lines = new List<QueuedLine>();
        long characters = 0;
        while (_lines.TryPeek(out var next))
        {
            var fits = lines.Count == 0 || characters + next.Text.Length <= MaxBatchCharacters;
            if (!fits && characters >= ShortBatchCharacters)
            {
                break;
            }

            _lines.Dequeue();
            lines.Add(next);
            characters += next.Text.Length;
            if (!fits)
            {
                // The short lines go with the line that did not fit, and nothing more.
                break;
            }
        }

        QueuedCharacters -= characters;
        var text = lines.Count == 1 ? lines[0].Text : string.Concat(lines.Select(line => line.Text));
        return new SendBatch(text, lines);
    }

    /// <summary>Empties the queue and returns the lines that were waiting, in their order.</summary>
    public IReadOnlyList<QueuedLine> Clear()
    {
        var dropped = _lines.ToArray();
        _lines.Clear();
        QueuedCharacters = 0;
        return dropped;
    }
}
