using System.Buffers.Binary;

namespace UltimateRDP.Core.Protocol;

/// <summary>What the blocks read from the file handle of a channel hold, as <see cref="ChannelPduFraming"/> found it.</summary>
public enum ChannelPduLayout
{
    /// <summary>Nothing was read yet.</summary>
    Unknown,

    /// <summary>The data of the client itself, without any header.</summary>
    Raw,

    /// <summary>Each chunk of the data comes after a CHANNEL_PDU_HEADER of 8 bytes.</summary>
    Headers,
}

/// <summary>
/// Takes the data of the client out of the blocks read from the file handle of a static
/// virtual channel. Such a read may put a CHANNEL_PDU_HEADER in front of each chunk:
/// 4 bytes of length, the total length of the message the chunk belongs to, then 4 bytes
/// of flags, little endian, with <see cref="FlagFirst"/> on the first chunk of a message
/// and <see cref="FlagLast"/> on its last one.
/// </summary>
/// <remarks>
/// <para>
/// The layout is found on the first block of a connection, which starts with the first
/// line of the client and so with <c>{</c>: a block that starts with <c>{</c> is
/// <see cref="ChannelPduLayout.Raw"/> and passes through unchanged from then on; a block
/// whose first 8 bytes read as a header of a first chunk, followed by <c>{</c>, is
/// <see cref="ChannelPduLayout.Headers"/>.
/// </para>
/// <para>
/// With headers, a block may hold one chunk or several. A chunk that is not the last of
/// its message ends with the block, unless a header of the same message follows exactly
/// <see cref="ChunkLength"/> bytes after its own: the sender fills every chunk but the
/// last one. A last chunk takes the bytes its message still misses; a header of the next
/// message may follow it.
/// </para>
/// <para>
/// Any inconsistency (unknown or unsupported flags, a length that changes inside a
/// message, a first chunk inside a message, a last chunk too short, a block that ends
/// inside a header) makes <see cref="TryUnwrap"/> return false with <see cref="Error"/>,
/// write nothing and change nothing. The caller then stops using the file handle.
/// </para>
/// <para>This class is not thread safe.</para>
/// </remarks>
public sealed class ChannelPduFraming
{
    /// <summary>Length of a CHANNEL_PDU_HEADER.</summary>
    public const int HeaderLength = 8;

    /// <summary>CHANNEL_CHUNK_LENGTH: the usual size of a chunk of a static channel.</summary>
    public const int DefaultChunkLength = 1600;

    /// <summary>CHANNEL_FLAG_FIRST: the first chunk of a message.</summary>
    public const uint FlagFirst = 0x01;

    /// <summary>CHANNEL_FLAG_LAST: the last chunk of a message.</summary>
    public const uint FlagLast = 0x02;

    /// <summary>CHANNEL_FLAG_SHOW_PROTOCOL: the header is shown to the reader.</summary>
    public const uint FlagShowProtocol = 0x10;

    /// <summary>CHANNEL_FLAG_SHADOW_PERSISTENT: the channel stays across a shadow session.</summary>
    public const uint FlagShadowPersistent = 0x80;

    /// <summary>
    /// The flags a chunk may carry here. The others (suspend, resume, compression) mean
    /// something this reader does not handle.
    /// </summary>
    public const uint AcceptedFlags = FlagFirst | FlagLast | FlagShowProtocol | FlagShadowPersistent;

    private const byte OpeningBrace = (byte)'{';

    private readonly List<(int Offset, int Length)> _segments = [];
    private bool _inMessage;
    private long _messageLength;
    private long _remaining;

    /// <param name="chunkLength">Size of the chunks that are not the last of their message, <see cref="DefaultChunkLength"/> by default.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="chunkLength"/> is below 1.</exception>
    public ChannelPduFraming(int chunkLength = DefaultChunkLength)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(chunkLength, 1);
        ChunkLength = chunkLength;
    }

    /// <summary>Size of the chunks that are not the last of their message.</summary>
    public int ChunkLength { get; }

    /// <summary>What the blocks hold, found on the first block.</summary>
    public ChannelPduLayout Layout { get; private set; }

    /// <summary>Headers taken out so far.</summary>
    public long Headers { get; private set; }

    /// <summary>Why the last call of <see cref="TryUnwrap"/> failed, or null.</summary>
    public string? Error { get; private set; }

    /// <summary>
    /// Copies the data of the client held in <paramref name="block"/> to
    /// <paramref name="payload"/>, which must be at least as long as the block. Returns
    /// false, with <see cref="Error"/>, when the block does not fit what came before; then
    /// nothing is written and the state does not change.
    /// </summary>
    /// <param name="block">The bytes of one read.</param>
    /// <param name="payload">Where the data goes.</param>
    /// <param name="written">Bytes written to <paramref name="payload"/>.</param>
    /// <param name="headers">Headers found in the block.</param>
    /// <exception cref="ArgumentException"><paramref name="payload"/> is shorter than <paramref name="block"/>.</exception>
    public bool TryUnwrap(ReadOnlySpan<byte> block, Span<byte> payload, out int written, out int headers)
    {
        if (payload.Length < block.Length)
        {
            throw new ArgumentException("The payload buffer is shorter than the block.", nameof(payload));
        }

        written = 0;
        headers = 0;
        Error = null;
        if (block.IsEmpty)
        {
            return true;
        }

        var layout = Layout;
        if (layout == ChannelPduLayout.Unknown)
        {
            if (IsFirstHeader(block))
            {
                layout = ChannelPduLayout.Headers;
            }
            else if (block[0] == OpeningBrace)
            {
                layout = ChannelPduLayout.Raw;
            }
            else
            {
                return Fail($"the first block starts with 0x{block[0]:X2}, neither with \"{{\" nor with a channel header");
            }
        }

        if (layout == ChannelPduLayout.Raw)
        {
            block.CopyTo(payload);
            written = block.Length;
            Layout = layout;
            return true;
        }

        if (!Unwrap(block, payload, out written, out headers))
        {
            return false;
        }

        Layout = layout;
        return true;
    }

    private bool Unwrap(ReadOnlySpan<byte> block, Span<byte> payload, out int written, out int headers)
    {
        written = 0;
        headers = 0;
        _segments.Clear();
        var inMessage = _inMessage;
        var messageLength = _messageLength;
        var remaining = _remaining;
        var position = 0;
        while (position < block.Length)
        {
            if (block.Length - position < HeaderLength)
            {
                return Fail($"the block of {block.Length} bytes ends inside a channel header at byte {position}");
            }

            var length = BinaryPrimitives.ReadUInt32LittleEndian(block[position..]);
            var flags = BinaryPrimitives.ReadUInt32LittleEndian(block[(position + 4)..]);
            if ((flags & ~AcceptedFlags) != 0)
            {
                return Fail($"a channel header at byte {position} has the flags 0x{flags:X8}, which this reader does not handle");
            }

            var first = (flags & FlagFirst) != 0;
            var last = (flags & FlagLast) != 0;
            if (first)
            {
                if (inMessage)
                {
                    return Fail($"a first chunk came while {remaining} bytes of the previous message were missing");
                }

                if (length == 0)
                {
                    return Fail("a first chunk announces a message of 0 bytes");
                }

                inMessage = true;
                messageLength = length;
                remaining = length;
            }
            else
            {
                if (!inMessage)
                {
                    return Fail($"a chunk without the first flag came outside a message (flags 0x{flags:X8})");
                }

                if (length != messageLength)
                {
                    return Fail($"a chunk announces a message of {length} bytes inside a message of {messageLength} bytes");
                }
            }

            var dataStart = position + HeaderLength;
            var available = block.Length - dataStart;
            int take;
            if (last)
            {
                if (available < remaining)
                {
                    return Fail($"the last chunk of a message holds {available} bytes and {remaining} were missing");
                }

                take = (int)remaining;
            }
            else
            {
                take = available > ChunkLength && IsNextHeader(block[(dataStart + ChunkLength)..], messageLength) ? ChunkLength : available;
                if (take == 0)
                {
                    return Fail("a chunk that is not the last of its message is empty");
                }

                if (take >= remaining)
                {
                    return Fail($"a chunk of {take} bytes without the last flag reaches the end of its message, {remaining} bytes");
                }
            }

            _segments.Add((dataStart, take));
            remaining -= take;
            position = dataStart + take;
            if (last)
            {
                inMessage = false;
                remaining = 0;
            }
        }

        foreach (var (offset, count) in _segments)
        {
            block.Slice(offset, count).CopyTo(payload[written..]);
            written += count;
        }

        headers = _segments.Count;
        _inMessage = inMessage;
        _messageLength = messageLength;
        _remaining = remaining;
        Headers += headers;
        return true;
    }

    /// <summary>True when the block starts with the header of a first chunk followed by <c>{</c>.</summary>
    private static bool IsFirstHeader(ReadOnlySpan<byte> block)
    {
        if (block.Length <= HeaderLength)
        {
            return false;
        }

        var length = BinaryPrimitives.ReadUInt32LittleEndian(block);
        var flags = BinaryPrimitives.ReadUInt32LittleEndian(block[4..]);
        return length > 0
            && (flags & ~AcceptedFlags) == 0
            && (flags & FlagFirst) != 0
            && block[HeaderLength] == OpeningBrace;
    }

    /// <summary>True when <paramref name="data"/> starts with the header of a later chunk of the same message.</summary>
    private static bool IsNextHeader(ReadOnlySpan<byte> data, long messageLength)
    {
        if (data.Length < HeaderLength)
        {
            return false;
        }

        var length = BinaryPrimitives.ReadUInt32LittleEndian(data);
        var flags = BinaryPrimitives.ReadUInt32LittleEndian(data[4..]);
        return length == messageLength && (flags & ~AcceptedFlags) == 0 && (flags & FlagFirst) == 0;
    }

    private bool Fail(string error)
    {
        Error = error;
        return false;
    }
}
