using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using UltimateRDP.Core.Protocol;

namespace UltimateRDP.Core.Transfer;

/// <summary>
/// Builds the <c>fileChunk</c> line straight from the bytes, without going through the
/// JSON serializer: the data is written once into the final string, and reads the lines of
/// <see cref="ChunkEncodings.U15"/> back the same way on the agent.
/// </summary>
/// <remarks>
/// <para>
/// base64: <c>{"type":"fileChunk","transferId":"...","fileIndex":0,"offset":0,"data":"..."}</c>.
/// The line reads back as the same <see cref="FileChunkMessage"/> as the serialized form;
/// it only differs in that the <c>+</c> of base64 is written as is, where the serializer
/// escapes it.
/// </para>
/// <para>
/// u15: <c>{"type":"fileChunk","transferId":"...","fileIndex":0,"offset":0,"length":131072,"enc":"u15","data":"..."}</c>,
/// the ASCII fields first and the units of <see cref="U15Codec"/> last. The units are
/// valid in a JSON string as they are, so the line is still one JSON object, but it is not
/// ASCII: the agent reads it with <see cref="TryParseChunk"/> from the string the line
/// reader gives, never through an ASCII conversion, which would turn every unit into a
/// question mark.
/// </para>
/// </remarks>
public static class TransferLines
{
    private const string Suffix = "\"}\n";
    private const string TypeAndId = "{\"type\":\"" + MessageTypes.FileChunk + "\",\"transferId\":\"";
    private const string FileIndexField = "\",\"fileIndex\":";
    private const string OffsetField = ",\"offset\":";
    private const string LengthField = ",\"length\":";
    private const string U15DataField = ",\"enc\":\"" + ChunkEncodings.U15 + "\",\"data\":\"";
    private const string End = "\"}";

    /// <summary>
    /// The line for a chunk, followed by <c>\n</c>. Falls back to the serializer when the
    /// transfer id holds a character that JSON would escape.
    /// </summary>
    /// <param name="transferId">Identifier of the transfer.</param>
    /// <param name="fileIndex">Position of the file in the transfer.</param>
    /// <param name="offset">Position of the first byte in the file.</param>
    /// <param name="data">The bytes.</param>
    /// <param name="encoding">One of <see cref="ChunkEncodings"/>.</param>
    /// <exception cref="ArgumentException">The line would be longer than <see cref="LineFraming.MaxLineLength"/>, or the encoding is unknown.</exception>
    public static string EncodeChunk(string transferId, int fileIndex, long offset, ReadOnlySpan<byte> data, string encoding = ChunkEncodings.Base64)
    {
        ArgumentNullException.ThrowIfNull(transferId);
        ArgumentNullException.ThrowIfNull(encoding);
        var u15 = encoding switch
        {
            ChunkEncodings.Base64 => false,
            ChunkEncodings.U15 => true,
            _ => throw new ArgumentException($"The chunk encoding \"{encoding}\" is unknown.", nameof(encoding)),
        };

        if (!IsPlainId(transferId))
        {
            var message = u15
                ? new FileChunkMessage(transferId, fileIndex, offset, U15Codec.Encode(data), data.Length, ChunkEncodings.U15)
                : new FileChunkMessage(transferId, fileIndex, offset, Convert.ToBase64String(data));
            return LineFraming.EncodeLine(message);
        }

        var prefix = u15
            ? string.Create(CultureInfo.InvariantCulture, $"{TypeAndId}{transferId}{FileIndexField}{fileIndex}{OffsetField}{offset}{LengthField}{data.Length}{U15DataField}")
            : string.Create(CultureInfo.InvariantCulture, $"{TypeAndId}{transferId}{FileIndexField}{fileIndex}{OffsetField}{offset},\"data\":\"");
        var dataLength = u15 ? U15Codec.EncodedLength(data.Length) : checked((data.Length + 2) / 3 * 4);
        var length = prefix.Length + dataLength + Suffix.Length;
        if (length - 1 > LineFraming.MaxLineLength)
        {
            throw new ArgumentException($"The chunk line would be {length - 1} characters long; the maximum is {LineFraming.MaxLineLength}.", nameof(data));
        }

        return string.Create(length, new ChunkParts(prefix, data, dataLength, u15), static (chars, parts) =>
        {
            parts.Prefix.CopyTo(chars);
            var target = chars.Slice(parts.Prefix.Length, parts.DataLength);
            var written = parts.U15 ? U15Codec.Encode(parts.Data, target) : Convert.TryToBase64Chars(parts.Data, target, out var count) ? count : -1;
            if (written != parts.DataLength)
            {
                throw new InvalidOperationException("The encoding of the chunk did not fill the expected length.");
            }

            Suffix.CopyTo(chars[(parts.Prefix.Length + parts.DataLength)..]);
        });
    }

    /// <summary>
    /// Reads a <see cref="ChunkEncodings.U15"/> chunk line, as <see cref="EncodeChunk"/>
    /// writes it, from the line without its <c>\n</c>. Returns false for any other line,
    /// which then goes to the serializer: a base64 chunk, another message, a line whose
    /// fields are not in the order written here, or a line whose data holds JSON escapes,
    /// as the serializer writes it. The units are not checked here: the receiver decodes
    /// them and fails the transfer on a bad unit.
    /// </summary>
    public static bool TryParseChunk(string line, [NotNullWhen(true)] out FileChunkMessage? chunk)
    {
        ArgumentNullException.ThrowIfNull(line);
        chunk = null;
        var rest = line.AsSpan();
        if (!rest.StartsWith(TypeAndId, StringComparison.Ordinal) || !rest.EndsWith(End, StringComparison.Ordinal))
        {
            return false;
        }

        rest = rest[TypeAndId.Length..];
        var idEnd = rest.IndexOf('"');
        if (idEnd <= 0)
        {
            return false;
        }

        var transferId = rest[..idEnd].ToString();
        if (!IsPlainId(transferId))
        {
            return false;
        }

        rest = rest[idEnd..];
        if (!TryField(ref rest, FileIndexField, out var fileIndex) || fileIndex > int.MaxValue
            || !TryField(ref rest, OffsetField, out var offset)
            || !TryField(ref rest, LengthField, out var length) || length > int.MaxValue
            || !rest.StartsWith(U15DataField, StringComparison.Ordinal))
        {
            return false;
        }

        var dataStart = line.Length - rest.Length + U15DataField.Length;
        var dataLength = line.Length - End.Length - dataStart;

        // The serializer writes the units as escapes: such a line goes to the JSON reader.
        if (dataLength < 0 || line.AsSpan(dataStart, dataLength).Contains('\\'))
        {
            return false;
        }

        chunk = new FileChunkMessage(transferId, (int)fileIndex, offset, line.Substring(dataStart, dataLength), (int)length, ChunkEncodings.U15);
        return true;
    }

    /// <summary>Reads <paramref name="name"/> then a number of at most 18 digits, without a sign.</summary>
    private static bool TryField(ref ReadOnlySpan<char> rest, string name, out long value)
    {
        value = 0;
        if (!rest.StartsWith(name, StringComparison.Ordinal))
        {
            return false;
        }

        rest = rest[name.Length..];
        var digits = 0;
        while (digits < rest.Length && char.IsAsciiDigit(rest[digits]))
        {
            digits++;
        }

        if (digits is 0 or > 18 || !long.TryParse(rest[..digits], NumberStyles.None, CultureInfo.InvariantCulture, out value))
        {
            return false;
        }

        rest = rest[digits..];
        return true;
    }

    /// <summary>The pieces of a chunk line, handed to the string builder without a copy.</summary>
    private readonly ref struct ChunkParts(string prefix, ReadOnlySpan<byte> data, int dataLength, bool u15)
    {
        public readonly string Prefix = prefix;
        public readonly ReadOnlySpan<byte> Data = data;
        public readonly int DataLength = dataLength;
        public readonly bool U15 = u15;
    }

    /// <summary>True when the id holds only ASCII letters, digits, dashes and underscores.</summary>
    internal static bool IsPlainId(string id)
    {
        foreach (var c in id)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_'))
            {
                return false;
            }
        }

        return id.Length > 0;
    }
}
