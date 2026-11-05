using System.Text;

namespace RdpDrop.Core.RdpFiles;

/// <summary>
/// Turns the bytes of a <c>.rdp</c> file into text.
/// </summary>
internal static class RdpFileEncoding
{
    private static ReadOnlySpan<byte> Utf16LeBom => [0xFF, 0xFE];

    private static ReadOnlySpan<byte> Utf16BeBom => [0xFE, 0xFF];

    private static ReadOnlySpan<byte> Utf8Bom => [0xEF, 0xBB, 0xBF];

    /// <summary>
    /// Uses the byte order mark when there is one. Without one, text whose odd bytes are
    /// mostly zero is read as UTF-16 LE, anything else as UTF-8.
    /// </summary>
    public static string Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.StartsWith(Utf16LeBom))
        {
            return Encoding.Unicode.GetString(bytes[Utf16LeBom.Length..]);
        }

        if (bytes.StartsWith(Utf16BeBom))
        {
            return Encoding.BigEndianUnicode.GetString(bytes[Utf16BeBom.Length..]);
        }

        if (bytes.StartsWith(Utf8Bom))
        {
            return Encoding.UTF8.GetString(bytes[Utf8Bom.Length..]);
        }

        return LooksLikeUtf16LittleEndian(bytes)
            ? Encoding.Unicode.GetString(bytes)
            : Encoding.UTF8.GetString(bytes);
    }

    private static bool LooksLikeUtf16LittleEndian(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 4 || bytes.Length % 2 != 0)
        {
            return false;
        }

        var zeros = 0;
        for (var i = 1; i < bytes.Length; i += 2)
        {
            if (bytes[i] == 0)
            {
                zeros++;
            }
        }

        // At least nine characters out of ten have a zero high byte.
        return zeros * 10 >= bytes.Length / 2 * 9;
    }
}
