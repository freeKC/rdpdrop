using System.Globalization;
using UltimateRDP.Core.Protocol;

namespace UltimateRDP.Core.Transfer;

/// <summary>
/// Builds the <c>fileChunk</c> line straight from the bytes, without going through the
/// JSON serializer: the base64 is written once into the final string. The result reads
/// back as the same <see cref="FileChunkMessage"/> as the serialized form; it only differs
/// in that the <c>+</c> of base64 is written as is, where the serializer escapes it.
/// </summary>
public static class TransferLines
{
    private const string Suffix = "\"}\n";

    /// <summary>
    /// The line for a chunk, followed by <c>\n</c>. Falls back to the serializer when the
    /// transfer id holds a character that JSON would escape.
    /// </summary>
    /// <exception cref="ArgumentException">The line would be longer than <see cref="LineFraming.MaxLineLength"/>.</exception>
    public static string EncodeChunk(string transferId, int fileIndex, long offset, ReadOnlySpan<byte> data)
    {
        ArgumentNullException.ThrowIfNull(transferId);
        if (!IsPlainId(transferId))
        {
            return LineFraming.EncodeLine(new FileChunkMessage(transferId, fileIndex, offset, Convert.ToBase64String(data)));
        }

        var prefix = string.Create(
            CultureInfo.InvariantCulture,
            $"{{\"type\":\"{MessageTypes.FileChunk}\",\"transferId\":\"{transferId}\",\"fileIndex\":{fileIndex},\"offset\":{offset},\"data\":\"");
        var base64Length = checked((data.Length + 2) / 3 * 4);
        var length = prefix.Length + base64Length + Suffix.Length;
        if (length - 1 > LineFraming.MaxLineLength)
        {
            throw new ArgumentException($"The chunk line would be {length - 1} characters long; the maximum is {LineFraming.MaxLineLength}.", nameof(data));
        }

        return string.Create(length, new ChunkParts(prefix, data, base64Length), static (chars, parts) =>
        {
            parts.Prefix.CopyTo(chars);
            if (!Convert.TryToBase64Chars(parts.Data, chars.Slice(parts.Prefix.Length, parts.Base64Length), out var written) || written != parts.Base64Length)
            {
                throw new InvalidOperationException("The base64 encoding did not fill the expected length.");
            }

            Suffix.CopyTo(chars[(parts.Prefix.Length + parts.Base64Length)..]);
        });
    }

    /// <summary>The pieces of a chunk line, handed to the string builder without a copy.</summary>
    private readonly ref struct ChunkParts(string prefix, ReadOnlySpan<byte> data, int base64Length)
    {
        public readonly string Prefix = prefix;
        public readonly ReadOnlySpan<byte> Data = data;
        public readonly int Base64Length = base64Length;
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
