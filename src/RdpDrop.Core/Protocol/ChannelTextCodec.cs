using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace RdpDrop.Core.Protocol;

/// <summary>
/// Detects the byte model of the channel and converts between channel bytes, the string
/// of the RDP ActiveX control and plain text.
/// </summary>
/// <remarks>
/// Only ASCII travels on the channel, so text here means one character per byte. Bytes
/// above 0x7F are kept as the character with the same code; the JSON reader rejects
/// them later.
/// </remarks>
public static class ChannelTextCodec
{
    private const byte OpenBrace = (byte)'{';

    /// <summary>
    /// Detects the model from the first bytes received on a connection (agent side).
    /// </summary>
    /// <returns>
    /// <see cref="ChannelByteModel.Utf16"/> for <c>0x7B 0x00</c>,
    /// <see cref="ChannelByteModel.OneBytePerChar"/> for <c>0x7B</c> followed by any other
    /// byte, and <see cref="ChannelByteModel.Unknown"/> when fewer than two bytes are given
    /// or the first byte is not <c>{</c>.
    /// </returns>
    public static ChannelByteModel DetectFromBytes(ReadOnlySpan<byte> first)
    {
        if (first.Length < 2 || first[0] != OpenBrace)
        {
            return ChannelByteModel.Unknown;
        }

        return first[1] == 0 ? ChannelByteModel.Utf16 : ChannelByteModel.OneBytePerChar;
    }

    /// <summary>
    /// Number of leading bytes that <see cref="BytesToText"/> can convert now: the whole
    /// span for <see cref="ChannelByteModel.OneBytePerChar"/>, the largest even count for
    /// <see cref="ChannelByteModel.Utf16"/>. The caller keeps the remaining byte for the
    /// next read.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="model"/> is <see cref="ChannelByteModel.Unknown"/> or not defined.</exception>
    public static int UsableLength(ReadOnlySpan<byte> bytes, ChannelByteModel model) => model switch
    {
        ChannelByteModel.OneBytePerChar => bytes.Length,
        ChannelByteModel.Utf16 => bytes.Length & ~1,
        _ => throw UnknownModel(model),
    };

    /// <summary>
    /// Converts bytes read from the channel to text (agent side).
    /// </summary>
    /// <exception cref="ArgumentException">The model is <see cref="ChannelByteModel.Utf16"/> and the byte count is odd; pass only <see cref="UsableLength"/> bytes.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="model"/> is <see cref="ChannelByteModel.Unknown"/> or not defined.</exception>
    public static string BytesToText(ReadOnlySpan<byte> bytes, ChannelByteModel model)
    {
        switch (model)
        {
            case ChannelByteModel.OneBytePerChar:
                return BytesAsChars(bytes);

            case ChannelByteModel.Utf16:
                if (bytes.Length % 2 != 0)
                {
                    throw new ArgumentException("UTF-16 data must have an even byte count.", nameof(bytes));
                }

                return Utf16LittleEndianToString(bytes);

            default:
                throw UnknownModel(model);
        }
    }

    /// <summary>
    /// Detects the model from the first string delivered by the control (client side).
    /// </summary>
    /// <returns>
    /// <see cref="ChannelByteModel.OneBytePerChar"/> when the first character is <c>{</c>,
    /// <see cref="ChannelByteModel.Utf16"/> for any other first character, and
    /// <see cref="ChannelByteModel.Unknown"/> for an empty string.
    /// </returns>
    public static ChannelByteModel DetectFromControlString(string s)
    {
        if (string.IsNullOrEmpty(s))
        {
            return ChannelByteModel.Unknown;
        }

        return s[0] == '{' ? ChannelByteModel.OneBytePerChar : ChannelByteModel.Utf16;
    }

    /// <summary>
    /// Converts a string delivered by the control to text (client side).
    /// </summary>
    /// <remarks>
    /// <see cref="ChannelByteModel.OneBytePerChar"/>: the string itself, which must only
    /// hold characters up to 0xFF. <see cref="ChannelByteModel.Utf16"/>: each character is
    /// read as two bytes, low byte first, and each byte becomes one character.
    /// </remarks>
    /// <exception cref="ArgumentException">A character is above 0xFF in the one byte per character model.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="model"/> is <see cref="ChannelByteModel.Unknown"/> or not defined.</exception>
    public static string ControlStringToText(string s, ChannelByteModel model)
    {
        ArgumentNullException.ThrowIfNull(s);
        switch (model)
        {
            case ChannelByteModel.OneBytePerChar:
                foreach (var c in s)
                {
                    if (c > 0xFF)
                    {
                        throw new ArgumentException($"Character U+{(int)c:X4} cannot carry a single byte.", nameof(s));
                    }
                }

                return s;

            case ChannelByteModel.Utf16:
                return BytesAsChars(StringToUtf16LittleEndian(s));

            default:
                throw UnknownModel(model);
        }
    }

    /// <summary>
    /// Converts an ASCII line, as returned by <see cref="LineFraming.EncodeLine"/>, to the
    /// string to hand to the control (client side).
    /// </summary>
    /// <remarks>
    /// <see cref="ChannelByteModel.OneBytePerChar"/>: the line itself.
    /// <see cref="ChannelByteModel.Utf16"/>: two ASCII characters are packed into each
    /// character, the first one in the low byte. An odd length is first made even with a
    /// space inserted before the final <c>\n</c> (or added at the end when the line has
    /// no <c>\n</c>).
    /// </remarks>
    /// <exception cref="ArgumentException">The line holds a character outside ASCII.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="model"/> is <see cref="ChannelByteModel.Unknown"/> or not defined.</exception>
    public static string TextToControlString(string asciiLine, ChannelByteModel model)
    {
        ArgumentNullException.ThrowIfNull(asciiLine);
        foreach (var c in asciiLine)
        {
            if (c > 0x7F)
            {
                throw new ArgumentException($"Character U+{(int)c:X4} is not ASCII.", nameof(asciiLine));
            }
        }

        switch (model)
        {
            case ChannelByteModel.OneBytePerChar:
                return asciiLine;

            case ChannelByteModel.Utf16:
                var text = asciiLine;
                if (text.Length % 2 != 0)
                {
                    text = text.EndsWith('\n') ? string.Concat(text.AsSpan(0, text.Length - 1), " \n") : text + " ";
                }

                var bytes = new byte[text.Length];
                for (var i = 0; i < text.Length; i++)
                {
                    bytes[i] = (byte)text[i];
                }

                return Utf16LittleEndianToString(bytes);

            default:
                throw UnknownModel(model);
        }
    }

    private static string BytesAsChars(ReadOnlySpan<byte> bytes)
    {
        var chars = new char[bytes.Length];
        for (var i = 0; i < bytes.Length; i++)
        {
            chars[i] = (char)bytes[i];
        }

        return new string(chars);
    }

    private static string Utf16LittleEndianToString(ReadOnlySpan<byte> bytes)
    {
        var units = MemoryMarshal.Cast<byte, char>(bytes);
        if (BitConverter.IsLittleEndian)
        {
            return new string(units);
        }

        var swapped = MemoryMarshal.Cast<char, ushort>(units).ToArray();
        BinaryPrimitives.ReverseEndianness(swapped, swapped);
        return new string(MemoryMarshal.Cast<ushort, char>(swapped));
    }

    /// <summary>The UTF-16LE bytes of <paramref name="s"/>, without a byte order mark.</summary>
    internal static byte[] StringToUtf16LittleEndian(string s)
    {
        var bytes = MemoryMarshal.AsBytes(s.AsSpan()).ToArray();
        if (!BitConverter.IsLittleEndian)
        {
            var units = MemoryMarshal.Cast<byte, ushort>(bytes.AsSpan());
            BinaryPrimitives.ReverseEndianness(units, units);
        }

        return bytes;
    }

    private static ArgumentOutOfRangeException UnknownModel(ChannelByteModel model) =>
        new(nameof(model), model, "The byte model must be known.");
}
