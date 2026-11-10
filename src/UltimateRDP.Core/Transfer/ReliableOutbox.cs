using UltimateRDP.Core.Protocol;

namespace UltimateRDP.Core.Transfer;

/// <summary>
/// The numbered lines of one transfer that the client sent and the agent did not yet
/// process, kept as strings so that they can go again when the agent asks for them, even
/// long after the call of the control returned.
/// </summary>
/// <remarks>
/// <para>
/// Every line is added with its number, in order, from 0. An acknowledgement that carries
/// <c>seq</c> frees every line up to that number (<see cref="Acknowledge"/>). A
/// <see cref="TransferResendMessage"/> takes the lines it names (<see cref="Take"/>);
/// when the client waits for an acknowledgement that does not come, it sends the newest
/// line again (<see cref="TakeNewest"/>), which shows the agent what it misses at the
/// end of what was sent. The lines taken are copies marked <see cref="QueuedLine.IsResend"/>.
/// </para>
/// <para>This class is not thread safe.</para>
/// </remarks>
public sealed class ReliableOutbox
{
    private readonly List<QueuedLine> _lines = [];
    private int _head;

    /// <summary>The number of the oldest line kept; the lines below it were acknowledged.</summary>
    public long FirstSeq { get; private set; }

    /// <summary>The number the next line must carry.</summary>
    public long NextSeq => FirstSeq + Count;

    /// <summary>Lines kept.</summary>
    public int Count => _lines.Count - _head;

    /// <summary>Characters of the lines kept.</summary>
    public long Characters { get; private set; }

    /// <summary>Requests of the agent answered so far.</summary>
    public int Requests { get; private set; }

    /// <summary>Lines sent again for the requests of the agent.</summary>
    public long ResentLines { get; private set; }

    /// <summary>Times the newest line went again because no acknowledgement came.</summary>
    public int Probes { get; private set; }

    /// <summary>Keeps a line that is being sent.</summary>
    /// <exception cref="ArgumentException">The line does not carry <see cref="NextSeq"/>.</exception>
    public void Add(QueuedLine line)
    {
        ArgumentNullException.ThrowIfNull(line);
        if (line.Seq != NextSeq)
        {
            throw new ArgumentException($"The line carries number {line.Seq}; {NextSeq} was expected.", nameof(line));
        }

        _lines.Add(line);
        Characters += line.Text.Length;
    }

    /// <summary>The agent processed every line up to <paramref name="seq"/>: they are freed. Returns how many.</summary>
    public int Acknowledge(long seq)
    {
        var count = 0;
        while (_head < _lines.Count && _lines[_head].Seq <= seq)
        {
            Characters -= _lines[_head].Text.Length;
            _head++;
            FirstSeq++;
            count++;
        }

        if (_head > 1024 && _head * 2 > _lines.Count)
        {
            _lines.RemoveRange(0, _head);
            _head = 0;
        }

        return count;
    }

    /// <summary>
    /// The lines from <paramref name="from"/> to <paramref name="to"/> that are still kept,
    /// in order, marked as sent again. Lines already acknowledged, or never sent, are left
    /// out: the agent already has the first ones, and cannot miss the others.
    /// </summary>
    public IReadOnlyList<QueuedLine> Take(long from, long to)
    {
        Requests++;
        var first = Math.Max(from, FirstSeq);
        var last = Math.Min(to, NextSeq - 1);
        if (first > last)
        {
            return [];
        }

        var lines = new QueuedLine[last - first + 1];
        for (var i = 0; i < lines.Length; i++)
        {
            lines[i] = _lines[_head + (int)(first - FirstSeq) + i] with { IsResend = true };
        }

        ResentLines += lines.Length;
        return lines;
    }

    /// <summary>The newest line kept, marked as sent again, or null when every line was acknowledged.</summary>
    public QueuedLine? TakeNewest()
    {
        if (Count == 0)
        {
            return null;
        }

        Probes++;
        return _lines[^1] with { IsResend = true };
    }
}
