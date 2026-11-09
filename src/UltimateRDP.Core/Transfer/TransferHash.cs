namespace UltimateRDP.Core.Transfer;

/// <summary>
/// The checksum of a transferred file: SHA-256, written as 64 hexadecimal digits in lower
/// case.
/// </summary>
public static class TransferHash
{
    /// <summary>Number of hexadecimal digits of a SHA-256.</summary>
    public const int HexLength = 64;

    /// <summary>The digest as lower case hexadecimal digits.</summary>
    public static string ToHex(ReadOnlySpan<byte> digest) => Convert.ToHexStringLower(digest);

    /// <summary>True for exactly <see cref="HexLength"/> digits 0 to 9 and a to f.</summary>
    public static bool IsValidHex(string? value)
    {
        if (value is null || value.Length != HexLength)
        {
            return false;
        }

        foreach (var c in value)
        {
            if (!char.IsAsciiDigit(c) && c is not (>= 'a' and <= 'f'))
            {
                return false;
            }
        }

        return true;
    }
}
