using System.Buffers.Binary;

namespace RdpDrop.Core.Protocol;

/// <summary>
/// Collects byte chunks of any size and returns complete frame payloads.
/// </summary>
/// <remarks>
/// <para>
/// Call <see cref="Append"/> for every chunk received, then call
/// <see cref="TryReadFrame"/> until it returns false.
/// </para>
/// <para>
/// When a length prefix announces more than <see cref="Framing.MaxFrameSize"/> bytes,
/// the stream can no longer be trusted. <see cref="TryReadFrame"/> then drops every
/// buffered byte, increments <see cref="ProtocolErrorCount"/>, raises
/// <see cref="ProtocolError"/> and returns false. Frames returned before the error
/// are not affected. The next byte appended is read as the start of a new frame.
/// </para>
/// <para>This class is not thread safe.</para>
/// </remarks>
public sealed class FrameReader
{
    private byte[] _buffer = new byte[4096];
    private int _start;
    private int _count;

    /// <summary>
    /// Raised when a length prefix is larger than <see cref="Framing.MaxFrameSize"/>,
    /// after the reader has been reset.
    /// </summary>
    public event EventHandler<FrameProtocolErrorEventArgs>? ProtocolError;

    /// <summary>Number of protocol errors seen since the reader was created.</summary>
    public int ProtocolErrorCount { get; private set; }

    /// <summary>Number of bytes received but not yet returned as a frame.</summary>
    public int BufferedByteCount => _count;

    /// <summary>
    /// Adds a chunk of received bytes.
    /// </summary>
    public void Append(ReadOnlySpan<byte> chunk)
    {
        if (chunk.IsEmpty)
        {
            return;
        }

        EnsureSpace(chunk.Length);
        chunk.CopyTo(_buffer.AsSpan(_start + _count));
        _count += chunk.Length;
    }

    /// <summary>
    /// Returns the next complete payload, if one is buffered.
    /// </summary>
    /// <returns>True when <paramref name="payload"/> holds a frame payload.</returns>
    public bool TryReadFrame(out byte[] payload)
    {
        payload = [];
        if (_count < Framing.HeaderSize)
        {
            return false;
        }

        var length = BinaryPrimitives.ReadUInt32LittleEndian(_buffer.AsSpan(_start, Framing.HeaderSize));
        if (length > Framing.MaxFrameSize)
        {
            Reset();
            ProtocolErrorCount++;
            ProtocolError?.Invoke(this, new FrameProtocolErrorEventArgs(length));
            return false;
        }

        var frameSize = Framing.HeaderSize + (int)length;
        if (_count < frameSize)
        {
            return false;
        }

        payload = _buffer.AsSpan(_start + Framing.HeaderSize, (int)length).ToArray();
        _start += frameSize;
        _count -= frameSize;
        if (_count == 0)
        {
            _start = 0;
        }

        return true;
    }

    /// <summary>
    /// Drops every buffered byte.
    /// </summary>
    public void Reset()
    {
        _start = 0;
        _count = 0;
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
            // Enough room once the unread bytes are moved to the front.
            _buffer.AsSpan(_start, _count).CopyTo(_buffer);
        }
        else
        {
            var bigger = new byte[Math.Max(needed, _buffer.Length * 2)];
            _buffer.AsSpan(_start, _count).CopyTo(bigger);
            _buffer = bigger;
        }

        _start = 0;
    }
}

/// <summary>
/// Details of a frame whose announced length is too large.
/// </summary>
public sealed class FrameProtocolErrorEventArgs(uint declaredLength) : EventArgs
{
    /// <summary>Length read from the prefix.</summary>
    public uint DeclaredLength { get; } = declaredLength;
}
