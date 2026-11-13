using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using UltimateRDP.Core.Protocol;
using UltimateRDP.Core.Transfer;

namespace UltimateRDP.Core.Direct;

/// <summary>
/// The type byte of a frame of the direct connection, version 2. The client sends
/// <see cref="Token"/> on every connection, then on connection 0 <see cref="Begin"/>,
/// <see cref="FileHeader"/>, <see cref="ListEnd"/>, <see cref="FileTrailer"/> and
/// <see cref="Cancel"/>, and on every connection <see cref="Data"/> and <see cref="End"/>.
/// The agent sends <see cref="Ready"/> on every connection, then on connection 0 only
/// <see cref="Go"/>, <see cref="Ack"/> and <see cref="Final"/>.
/// </summary>
public enum DirectFrameType : byte
{
    /// <summary>
    /// The first frame of the client on each connection: the protocol version (1 byte), the
    /// index of the connection (1 byte), then the proof of the token for that index (32 bytes).
    /// </summary>
    Token = 1,

    /// <summary>The agent accepted the token of this connection; empty.</summary>
    Ready = 2,

    /// <summary>The files and bytes of the transfer: two signed little endian integers of 4 and 8 bytes.</summary>
    Begin = 3,

    /// <summary>
    /// One file of the transfer, all before any data: its index (4 bytes), its size (8
    /// bytes), its last write time in UTC as ticks (8 bytes, 0 when unknown), then its
    /// relative path in UTF-8.
    /// </summary>
    FileHeader = 4,

    /// <summary>
    /// One block of a file: the file index (4 bytes), the offset (8 bytes, a multiple of
    /// <see cref="DirectFrames.BlockSize"/>), then 1 to <see cref="DirectFrames.BlockSize"/>
    /// raw bytes, exactly the rest of the file when it is shorter than a block.
    /// </summary>
    Data = 5,

    /// <summary>The client read the whole file: its index (4 bytes) and its SHA-256 (32 bytes).</summary>
    FileTrailer = 6,

    /// <summary>The client stops the transfer; the rest is the reason in UTF-8, in English, for the log.</summary>
    Cancel = 7,

    /// <summary>
    /// The bytes received and written: the index of the file being checked (4 bytes), the
    /// bytes of that file checked so far (8 bytes) and the bytes of the whole transfer
    /// received and written (8 bytes).
    /// </summary>
    Ack = 8,

    /// <summary>
    /// The end of the transfer: the status (1 byte, as <see cref="DropStatus"/>), the failure
    /// (1 byte, as <see cref="DirectFailure"/>), the bytes received (8 bytes), the time of the
    /// agent in milliseconds (8 bytes), then a text for the user in UTF-8.
    /// </summary>
    Final = 9,

    /// <summary>Every file header was sent; empty.</summary>
    ListEnd = 10,

    /// <summary>The agent checked the list and created the files: the data may flow; empty.</summary>
    Go = 11,

    /// <summary>The client sent its last data frame on this connection: the number of data frames it sent on it (8 bytes).</summary>
    End = 12,
}

/// <summary>Why a direct transfer did not succeed.</summary>
public enum DirectFailure : byte
{
    /// <summary>No failure.</summary>
    None = 0,

    /// <summary>A frame is malformed, unknown, too long, out of order, or a block is misaligned, out of range or came twice.</summary>
    Protocol = 1,

    /// <summary>The first frame was not a token of the offer, or not of this protocol version.</summary>
    Token = 2,

    /// <summary>The staging folder lacks the space for the announced bytes.</summary>
    NoSpace = 3,

    /// <summary>A relative path is not allowed, or names a file a second time.</summary>
    BadName = 4,

    /// <summary>The files do not add up to the bytes of the begin frame.</summary>
    SizeMismatch = 5,

    /// <summary>The SHA-256 of a file does not match its trailer.</summary>
    HashMismatch = 6,

    /// <summary>Writing or reading back a file failed.</summary>
    WriteFailed = 7,

    /// <summary>The transfer ended without some of the dropped files, or without some blocks of a file.</summary>
    Incomplete = 8,

    /// <summary>A connection ended or failed before the transfer was over, or the agent closed it.</summary>
    Broken = 9,

    /// <summary>The client could not read a file, or a file changed while it was sent.</summary>
    LocalRead = 10,
}

/// <summary>An acknowledgement of the agent on the direct connection.</summary>
/// <param name="FileIndex">The file being checked.</param>
/// <param name="FileBytes">Bytes of that file checked so far.</param>
/// <param name="TotalBytes">Bytes of the whole transfer received and written.</param>
public readonly record struct DirectAck(int FileIndex, long FileBytes, long TotalBytes);

/// <summary>The final frame of the agent.</summary>
/// <param name="Status">Ok when every file arrived and was checked.</param>
/// <param name="Failure">Why it did not succeed, for the log.</param>
/// <param name="Bytes">Bytes received.</param>
/// <param name="Milliseconds">Time from the begin frame to the end, measured by the agent.</param>
/// <param name="Message">Text for the user in the language of the client, or empty.</param>
public sealed record DirectResult(DropStatus Status, DirectFailure Failure, long Bytes, long Milliseconds, string Message);

/// <summary>A frame that breaks the rules of the direct connection.</summary>
public sealed class DirectProtocolException : IOException
{
    /// <summary>Creates the exception with its reason, in English.</summary>
    public DirectProtocolException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// Framing of the direct connection: every frame is a length of 4 bytes, little endian,
/// that counts the payload only, one type byte (<see cref="DirectFrameType"/>), then the
/// payload. Numbers in a payload are little endian; texts are UTF-8 without a length,
/// at the end of their payload. Nothing is base64 or JSON.
/// </summary>
public static class DirectFrames
{
    /// <summary>Version of the protocol of the direct connection, the first byte of the token frame.</summary>
    public const byte ProtocolVersion = 2;

    /// <summary>Most connections of one transfer.</summary>
    public const int MaxConnections = 16;

    /// <summary>Bytes of the length and the type in front of every payload.</summary>
    public const int HeaderLength = 5;

    /// <summary>Bytes in front of the raw bytes of a data frame: the file index and the offset.</summary>
    public const int DataPrefixLength = 12;

    /// <summary>Bytes in front of the raw bytes of a data frame, header included.</summary>
    public const int DataFrameOverhead = HeaderLength + DataPrefixLength;

    /// <summary>Size of a block: the files go by blocks of 1 MiB at offsets that are multiples of it.</summary>
    public const int BlockSize = 1024 * 1024;

    /// <summary>Longest payload accepted; anything longer breaks the connection.</summary>
    public const int MaxPayloadLength = DataPrefixLength + BlockSize;

    /// <summary>The agent acknowledges at least every this many bytes, and at the end of each file.</summary>
    public const long AckIntervalBytes = 8L * 1024 * 1024;

    /// <summary>Bytes of the random token of an offer.</summary>
    public const int TokenLength = 32;

    /// <summary>Bytes of a SHA-256, and of the proof of a token.</summary>
    public const int HashLength = 32;

    /// <summary>Bytes of the payload of a token frame of version 2.</summary>
    public const int TokenPayloadLength = 2 + HashLength;

    /// <summary>Longest text of a cancel or final frame, in bytes; longer texts are cut.</summary>
    public const int MaxTextBytes = 4096;

    /// <summary>The text the proof of a connection signs, followed by the index byte.</summary>
    private static readonly byte[] ProofLabel = Encoding.ASCII.GetBytes("UltimateRDP direct v2");

    /// <summary>Number of blocks of a file of <paramref name="size"/> bytes; 0 for an empty file.</summary>
    public static long BlockCount(long size) => (size + BlockSize - 1) / BlockSize;

    /// <summary>Bytes of the block at <paramref name="offset"/> of a file of <paramref name="size"/> bytes.</summary>
    public static int BlockLength(long size, long offset) => (int)Math.Min(BlockSize, size - offset);

    /// <summary>Writes the 5 bytes in front of a payload of <paramref name="length"/> bytes.</summary>
    public static void WriteHeader(Span<byte> destination, DirectFrameType type, int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        BinaryPrimitives.WriteInt32LittleEndian(destination, length);
        destination[4] = (byte)type;
    }

    /// <summary>
    /// Writes the <see cref="DataFrameOverhead"/> bytes in front of <paramref name="length"/>
    /// raw bytes of file <paramref name="fileIndex"/> at <paramref name="offset"/>.
    /// </summary>
    public static void WriteDataHeader(Span<byte> destination, int fileIndex, long offset, int length)
    {
        WriteHeader(destination, DirectFrameType.Data, DataPrefixLength + length);
        BinaryPrimitives.WriteInt32LittleEndian(destination[HeaderLength..], fileIndex);
        BinaryPrimitives.WriteInt64LittleEndian(destination[(HeaderLength + 4)..], offset);
    }

    /// <summary>Writes one whole frame to <paramref name="stream"/>, in one write.</summary>
    public static void Write(Stream stream, DirectFrameType type, ReadOnlySpan<byte> payload)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (payload.Length > MaxPayloadLength)
        {
            throw new ArgumentException("The payload is longer than a frame allows.", nameof(payload));
        }

        var frame = new byte[HeaderLength + payload.Length];
        WriteHeader(frame, type, payload.Length);
        payload.CopyTo(frame.AsSpan(HeaderLength));
        stream.Write(frame);
    }

    /// <summary>
    /// The proof of the token for connection <paramref name="index"/>: HMAC-SHA256 keyed with
    /// the token, over "UltimateRDP direct v2" followed by the index byte. The token itself
    /// never goes on a connection, and each connection has a proof of its own.
    /// </summary>
    public static byte[] ConnectionProof(ReadOnlySpan<byte> token, byte index)
    {
        Span<byte> message = stackalloc byte[ProofLabel.Length + 1];
        ProofLabel.CopyTo(message);
        message[^1] = index;
        return HMACSHA256.HashData(token, message);
    }

    /// <summary>The payload of the token frame of connection <paramref name="index"/>.</summary>
    public static byte[] TokenPayload(ReadOnlySpan<byte> token, byte index)
    {
        var payload = new byte[TokenPayloadLength];
        payload[0] = ProtocolVersion;
        payload[1] = index;
        ConnectionProof(token, index).CopyTo(payload.AsSpan(2));
        return payload;
    }

    /// <summary>The payload of the begin frame.</summary>
    public static byte[] BeginPayload(int files, long bytes)
    {
        var payload = new byte[12];
        BinaryPrimitives.WriteInt32LittleEndian(payload, files);
        BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(4), bytes);
        return payload;
    }

    /// <summary>Reads the payload of a begin frame.</summary>
    public static bool TryParseBegin(ReadOnlySpan<byte> payload, out int files, out long bytes)
    {
        files = 0;
        bytes = 0;
        if (payload.Length != 12)
        {
            return false;
        }

        files = BinaryPrimitives.ReadInt32LittleEndian(payload);
        bytes = BinaryPrimitives.ReadInt64LittleEndian(payload[4..]);
        return files >= 0 && bytes >= 0;
    }

    /// <summary>The payload of the header frame of file <paramref name="index"/>.</summary>
    public static byte[] FileHeaderPayload(int index, TransferFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        var path = Encoding.UTF8.GetBytes(file.RelativePath);
        var payload = new byte[20 + path.Length];
        BinaryPrimitives.WriteInt32LittleEndian(payload, index);
        BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(4), file.Size);
        var ticks = file.Modified is { } modified ? DateTime.SpecifyKind(modified, DateTimeKind.Utc).Ticks : 0;
        BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(12), ticks);
        path.CopyTo(payload.AsSpan(20));
        return payload;
    }

    /// <summary>Reads the payload of a header frame. False when it is malformed or its path is not valid UTF-8.</summary>
    public static bool TryParseFileHeader(ReadOnlySpan<byte> payload, out int index, out TransferFile file)
    {
        index = 0;
        file = null!;
        if (payload.Length < 21 || payload.Length > 20 + (TransferPaths.MaxLength * 3))
        {
            return false;
        }

        index = BinaryPrimitives.ReadInt32LittleEndian(payload);
        var size = BinaryPrimitives.ReadInt64LittleEndian(payload[4..]);
        var ticks = BinaryPrimitives.ReadInt64LittleEndian(payload[12..]);
        if (index < 0 || size < 0 || ticks < 0 || ticks > DateTime.MaxValue.Ticks)
        {
            return false;
        }

        string path;
        try
        {
            path = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(payload[20..]);
        }
        catch (ArgumentException)
        {
            return false;
        }

        file = new TransferFile(path, size, ticks == 0 ? null : new DateTime(ticks, DateTimeKind.Utc));
        return true;
    }

    /// <summary>Reads the payload of a data frame: the file, the offset and the raw bytes, at least one.</summary>
    public static bool TryParseData(ReadOnlySpan<byte> payload, out int fileIndex, out long offset, out ReadOnlySpan<byte> data)
    {
        fileIndex = 0;
        offset = 0;
        data = default;
        if (payload.Length <= DataPrefixLength)
        {
            return false;
        }

        fileIndex = BinaryPrimitives.ReadInt32LittleEndian(payload);
        offset = BinaryPrimitives.ReadInt64LittleEndian(payload[4..]);
        data = payload[DataPrefixLength..];
        return fileIndex >= 0 && offset >= 0;
    }

    /// <summary>The payload of the trailer frame of file <paramref name="index"/>.</summary>
    public static byte[] FileTrailerPayload(int index, ReadOnlySpan<byte> sha256)
    {
        if (sha256.Length != HashLength)
        {
            throw new ArgumentException("A SHA-256 has 32 bytes.", nameof(sha256));
        }

        var payload = new byte[4 + HashLength];
        BinaryPrimitives.WriteInt32LittleEndian(payload, index);
        sha256.CopyTo(payload.AsSpan(4));
        return payload;
    }

    /// <summary>Reads the payload of a trailer frame.</summary>
    public static bool TryParseFileTrailer(ReadOnlySpan<byte> payload, out int index, out ReadOnlySpan<byte> sha256)
    {
        index = 0;
        sha256 = default;
        if (payload.Length != 4 + HashLength)
        {
            return false;
        }

        index = BinaryPrimitives.ReadInt32LittleEndian(payload);
        sha256 = payload[4..];
        return index >= 0;
    }

    /// <summary>The payload of the end frame of a connection that carried <paramref name="dataFrames"/> data frames.</summary>
    public static byte[] EndPayload(long dataFrames)
    {
        var payload = new byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(payload, dataFrames);
        return payload;
    }

    /// <summary>Reads the payload of an end frame.</summary>
    public static bool TryParseEnd(ReadOnlySpan<byte> payload, out long dataFrames)
    {
        dataFrames = 0;
        if (payload.Length != 8)
        {
            return false;
        }

        dataFrames = BinaryPrimitives.ReadInt64LittleEndian(payload);
        return dataFrames >= 0;
    }

    /// <summary>The payload of an acknowledgement.</summary>
    public static byte[] AckPayload(DirectAck ack)
    {
        var payload = new byte[20];
        BinaryPrimitives.WriteInt32LittleEndian(payload, ack.FileIndex);
        BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(4), ack.FileBytes);
        BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(12), ack.TotalBytes);
        return payload;
    }

    /// <summary>Reads the payload of an acknowledgement.</summary>
    public static bool TryParseAck(ReadOnlySpan<byte> payload, out DirectAck ack)
    {
        ack = default;
        if (payload.Length != 20)
        {
            return false;
        }

        ack = new DirectAck(
            BinaryPrimitives.ReadInt32LittleEndian(payload),
            BinaryPrimitives.ReadInt64LittleEndian(payload[4..]),
            BinaryPrimitives.ReadInt64LittleEndian(payload[12..]));
        return ack.FileIndex >= 0 && ack.FileBytes >= 0 && ack.TotalBytes >= 0;
    }

    /// <summary>The payload of the final frame.</summary>
    public static byte[] FinalPayload(DirectResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var text = TextBytes(result.Message);
        var payload = new byte[18 + text.Length];
        payload[0] = (byte)result.Status;
        payload[1] = (byte)result.Failure;
        BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(2), result.Bytes);
        BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(10), result.Milliseconds);
        text.CopyTo(payload.AsSpan(18));
        return payload;
    }

    /// <summary>Reads the payload of the final frame.</summary>
    public static bool TryParseFinal(ReadOnlySpan<byte> payload, out DirectResult result)
    {
        result = null!;
        if (payload.Length < 18)
        {
            return false;
        }

        var status = (DropStatus)payload[0];
        var failure = (DirectFailure)payload[1];
        var bytes = BinaryPrimitives.ReadInt64LittleEndian(payload[2..]);
        var milliseconds = BinaryPrimitives.ReadInt64LittleEndian(payload[10..]);
        if (!Enum.IsDefined(status) || !Enum.IsDefined(failure) || bytes < 0 || milliseconds < 0)
        {
            return false;
        }

        result = new DirectResult(status, failure, bytes, milliseconds, Encoding.UTF8.GetString(payload[18..]));
        return true;
    }

    /// <summary>The payload of a cancel frame.</summary>
    public static byte[] CancelPayload(string? reason) => TextBytes(reason);

    /// <summary>A text as UTF-8, cut at <see cref="MaxTextBytes"/> on a character boundary.</summary>
    private static byte[] TextBytes(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return [];
        }

        var bytes = Encoding.UTF8.GetBytes(text);
        if (bytes.Length <= MaxTextBytes)
        {
            return bytes;
        }

        var end = MaxTextBytes;
        while (end > 0 && (bytes[end] & 0xC0) == 0x80)
        {
            end--;
        }

        return bytes[..end];
    }
}

/// <summary>
/// Reads the frames of the direct connection from a stream, one after the other, into a
/// buffer of its own: the payload returned stays valid until the next read.
/// </summary>
/// <remarks>This class is not thread safe: one reader per direction of a connection.</remarks>
public sealed class DirectFrameReader
{
    private readonly Stream _stream;
    private readonly byte[] _header = new byte[DirectFrames.HeaderLength];
    private byte[] _payload = new byte[64 * 1024];

    /// <summary>Reads from <paramref name="stream"/>.</summary>
    public DirectFrameReader(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        _stream = stream;
    }

    /// <summary>
    /// Reads the next frame. Returns false when the stream ends cleanly between two frames.
    /// Throws <see cref="EndOfStreamException"/> when it ends inside a frame, and
    /// <see cref="DirectProtocolException"/> for a length beyond
    /// <see cref="DirectFrames.MaxPayloadLength"/> or an unknown type. The errors of the
    /// stream pass through.
    /// </summary>
    public bool TryRead(out DirectFrameType type, out ReadOnlyMemory<byte> payload)
    {
        type = default;
        payload = default;
        var first = _stream.Read(_header, 0, DirectFrames.HeaderLength);
        if (first == 0)
        {
            return false;
        }

        if (first < DirectFrames.HeaderLength)
        {
            _stream.ReadExactly(_header, first, DirectFrames.HeaderLength - first);
        }

        var length = BinaryPrimitives.ReadInt32LittleEndian(_header);
        if (length < 0 || length > DirectFrames.MaxPayloadLength)
        {
            throw new DirectProtocolException($"A frame announces {length} bytes, more than the {DirectFrames.MaxPayloadLength} allowed.");
        }

        type = (DirectFrameType)_header[4];
        if (!Enum.IsDefined(type))
        {
            throw new DirectProtocolException($"A frame has the unknown type {_header[4]}.");
        }

        if (_payload.Length < length)
        {
            _payload = new byte[Math.Max(length, Math.Min(DirectFrames.MaxPayloadLength, _payload.Length * 2))];
        }

        _stream.ReadExactly(_payload, 0, length);
        payload = _payload.AsMemory(0, length);
        return true;
    }
}
