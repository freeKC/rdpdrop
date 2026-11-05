using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace RdpDrop.Core.Protocol;

/// <summary>
/// Wire form of a message: its JSON in ASCII only, followed by one <c>\n</c>.
/// </summary>
/// <remarks>
/// <see cref="MessageSerializer"/> escapes every character outside ASCII as
/// <c>\uXXXX</c> and never writes a raw line break, so a serialized message is always a
/// single ASCII line.
/// </remarks>
public static class LineFraming
{
    /// <summary>Longest accepted line, without its <c>\n</c>: 1 MiB characters.</summary>
    public const int MaxLineLength = 1024 * 1024;

    /// <summary>
    /// Returns the message as ASCII JSON followed by <c>\n</c>.
    /// </summary>
    /// <exception cref="ArgumentException">The line would be longer than <see cref="MaxLineLength"/>.</exception>
    /// <exception cref="InvalidOperationException">The JSON is not pure ASCII on a single line (never expected).</exception>
    public static string EncodeLine(Message message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var json = MessageSerializer.Serialize(message);
        if (json.Length > MaxLineLength)
        {
            throw new ArgumentException(
                $"The message is {json.Length} characters long; the maximum is {MaxLineLength}.",
                nameof(message));
        }

        foreach (var b in json)
        {
            if (b > 0x7F || b == '\n' || b == '\r')
            {
                throw new InvalidOperationException("The serialized message is not a single ASCII line.");
            }
        }

        return string.Create(json.Length + 1, json, static (chars, bytes) =>
        {
            for (var i = 0; i < bytes.Length; i++)
            {
                chars[i] = (char)bytes[i];
            }

            chars[^1] = '\n';
        });
    }

    /// <summary>
    /// Returns the bytes to write on the channel for <paramref name="model"/>.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item><see cref="ChannelByteModel.Utf16"/>: the line as UTF-16LE, two bytes per character.</item>
    /// <item>
    /// <see cref="ChannelByteModel.OneBytePerChar"/>: the line as ASCII, one byte per
    /// character. When the byte count is odd, a space is inserted before the <c>\n</c> so
    /// that nothing is lost if the receiving end pairs bytes into characters.
    /// </item>
    /// </list>
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="model"/> is <see cref="ChannelByteModel.Unknown"/> or not defined.</exception>
    public static byte[] EncodeBytes(Message message, ChannelByteModel model)
    {
        var line = EncodeLine(message);
        return model switch
        {
            ChannelByteModel.Utf16 => ToUtf16LittleEndian(line),
            ChannelByteModel.OneBytePerChar => ToPaddedAscii(line),
            _ => throw new ArgumentOutOfRangeException(nameof(model), model, "The byte model must be known to encode a message."),
        };
    }

    private static byte[] ToUtf16LittleEndian(string line)
    {
        var bytes = MemoryMarshal.AsBytes(line.AsSpan()).ToArray();
        if (!BitConverter.IsLittleEndian)
        {
            var units = MemoryMarshal.Cast<byte, ushort>(bytes.AsSpan());
            BinaryPrimitives.ReverseEndianness(units, units);
        }

        return bytes;
    }

    private static byte[] ToPaddedAscii(string line)
    {
        // The line ends with '\n'; an odd length gets a space before it.
        var padded = line.Length % 2 != 0;
        var bytes = new byte[padded ? line.Length + 1 : line.Length];
        var body = line.Length - 1;
        for (var i = 0; i < body; i++)
        {
            bytes[i] = (byte)line[i];
        }

        if (padded)
        {
            bytes[body] = (byte)' ';
        }

        bytes[^1] = (byte)'\n';
        return bytes;
    }
}
