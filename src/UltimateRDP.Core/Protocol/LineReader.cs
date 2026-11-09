namespace UltimateRDP.Core.Protocol;

/// <summary>
/// Collects text of any length and returns complete lines.
/// </summary>
/// <remarks>
/// <para>
/// Call <see cref="Append"/> for every piece of text received, then call
/// <see cref="TryReadLine"/> until it returns false.
/// </para>
/// <para>
/// A line ends with <c>\n</c>. A trailing <c>\r</c> and trailing spaces are removed;
/// lines that are empty after that are skipped.
/// </para>
/// <para>
/// A line longer than <see cref="LineFraming.MaxLineLength"/> characters, or buffered
/// text that passes that length without a <c>\n</c>, is a protocol error:
/// <see cref="TryReadLine"/> drops every buffered character, increments
/// <see cref="ProtocolErrorCount"/>, raises <see cref="ProtocolError"/> and returns
/// false. Lines returned before the error are not affected.
/// </para>
/// <para>This class is not thread safe.</para>
/// </remarks>
public sealed class LineReader
{
    private char[] _buffer = new char[4096];
    private int _start;
    private int _count;

    // Number of characters from _start already known to hold no '\n'.
    private int _scanned;

    /// <summary>
    /// Raised after the reader dropped its buffer because a line is too long.
    /// </summary>
    public event EventHandler? ProtocolError;

    /// <summary>Number of protocol errors seen since the reader was created.</summary>
    public int ProtocolErrorCount { get; private set; }

    /// <summary>Number of characters received but not yet returned as a line.</summary>
    public int BufferedLength => _count;

    /// <summary>
    /// Adds received text.
    /// </summary>
    public void Append(ReadOnlySpan<char> text)
    {
        if (text.IsEmpty)
        {
            return;
        }

        EnsureSpace(text.Length);
        text.CopyTo(_buffer.AsSpan(_start + _count));
        _count += text.Length;
    }

    /// <summary>
    /// Returns the next complete, non blank line, if one is buffered.
    /// </summary>
    /// <returns>True when <paramref name="line"/> holds a line, without its line break.</returns>
    public bool TryReadLine(out string line)
    {
        while (true)
        {
            line = string.Empty;
            var pending = _buffer.AsSpan(_start + _scanned, _count - _scanned);
            var index = pending.IndexOf('\n');
            if (index < 0)
            {
                _scanned = _count;
                if (_count > LineFraming.MaxLineLength)
                {
                    Fail();
                }

                return false;
            }

            var length = _scanned + index;
            var content = _buffer.AsSpan(_start, length).TrimEnd([' ', '\r']);
            var tooLong = length > LineFraming.MaxLineLength;
            var text = tooLong || content.IsEmpty ? null : content.ToString();

            _start += length + 1;
            _count -= length + 1;
            _scanned = 0;
            if (_count == 0)
            {
                _start = 0;
            }

            if (tooLong)
            {
                Fail();
                return false;
            }

            if (text is not null)
            {
                line = text;
                return true;
            }
        }
    }

    /// <summary>
    /// Drops every buffered character.
    /// </summary>
    public void Reset()
    {
        _start = 0;
        _count = 0;
        _scanned = 0;
    }

    private void Fail()
    {
        Reset();
        ProtocolErrorCount++;
        ProtocolError?.Invoke(this, EventArgs.Empty);
    }

    private void EnsureSpace(int extra)
    {
        if (_start + _count + extra <= _buffer.Length)
        {
            return;
        }

        var needed = _count + extra;
        if (needed <= _buffer.Length)
        {
            // Enough room once the unread characters are moved to the front.
            _buffer.AsSpan(_start, _count).CopyTo(_buffer);
        }
        else
        {
            var bigger = new char[Math.Max(needed, _buffer.Length * 2)];
            _buffer.AsSpan(_start, _count).CopyTo(bigger);
            _buffer = bigger;
        }

        _start = 0;
    }
}
