namespace UltimateRDP.Core.Protocol;

/// <summary>One line waiting in a <see cref="SendQueue"/>.</summary>
/// <param name="Text">The line, ASCII, ending with <c>\n</c>.</param>
/// <param name="Type">The type of the message, for the log.</param>
/// <param name="Subject">What the line belongs to: the id of the drag or the transfer, or a hello number.</param>
/// <param name="TransferPosition">
/// For a <c>fileChunk</c> line, the bytes of the whole transfer sent once this chunk is
/// counted, so that an acknowledgement of the agent can be matched with the sends that
/// carried it; -1 for any other line.
/// </param>
public sealed record QueuedLine(string Text, string Type, string Subject, long TransferPosition = -1)
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
/// <para>This class is not thread safe.</para>
/// </remarks>
public sealed class SendQueue
{
    /// <summary>Most characters handed to the control in one call by default: 1 MiB.</summary>
    public const int DefaultMaxBatchCharacters = 1024 * 1024;

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
    /// <see cref="MaxBatchCharacters"/>, and at least one. Null when nothing waits.
    /// </summary>
    public SendBatch? TakeBatch()
    {
        if (_lines.Count == 0)
        {
            return null;
        }

        var lines = new List<QueuedLine>();
        long characters = 0;
        while (_lines.TryPeek(out var next) && (lines.Count == 0 || characters + next.Text.Length <= MaxBatchCharacters))
        {
            _lines.Dequeue();
            lines.Add(next);
            characters += next.Text.Length;
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
