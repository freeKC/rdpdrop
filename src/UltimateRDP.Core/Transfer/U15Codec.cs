namespace UltimateRDP.Core.Transfer;

/// <summary>
/// The dense encoding of file data for a channel that carries each character as a UTF-16
/// code unit: every unit carries 15 bits of data, value v (0 to 32767) written as the
/// unit <c>0x1000 + v</c>. The bits are taken from the bytes in order, the high bit of
/// each byte first; the last unit is padded with zero bits, and the byte count, given
/// apart, tells where the data ends.
/// </summary>
/// <remarks>
/// The units 0x1000 to 0x8FFF hold no surrogate, no control character, no space, no line
/// break, no quote and no backslash, so they travel inside a JSON string without escape,
/// and nothing that trims a line or splits it can touch them. On the wire this costs 16
/// bits per 15 bits of data, against 32 bits per 24 for base64 sent as UTF-16.
/// </remarks>
public static class U15Codec
{
    /// <summary>The unit that carries the value zero.</summary>
    public const char FirstUnit = (char)0x1000;

    /// <summary>The unit that carries the value 32767.</summary>
    public const char LastUnit = (char)0x8FFF;

    /// <summary>Bits of data per unit.</summary>
    public const int BitsPerUnit = 15;

    private const int ValueMask = (1 << BitsPerUnit) - 1;

    /// <summary>Units needed for <paramref name="byteCount"/> bytes: the bits rounded up to whole units.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="byteCount"/> is negative.</exception>
    public static int EncodedLength(int byteCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(byteCount);
        return (int)(((8L * byteCount) + BitsPerUnit - 1) / BitsPerUnit);
    }

    /// <summary>True when <paramref name="unit"/> belongs to the alphabet of the encoding.</summary>
    public static bool IsUnit(char unit) => unit is >= FirstUnit and <= LastUnit;

    /// <summary>Encodes <paramref name="data"/> into the first <see cref="EncodedLength"/> characters of <paramref name="destination"/>.</summary>
    /// <returns>The number of units written.</returns>
    /// <exception cref="ArgumentException"><paramref name="destination"/> is too short.</exception>
    public static int Encode(ReadOnlySpan<byte> data, Span<char> destination)
    {
        var length = EncodedLength(data.Length);
        if (destination.Length < length)
        {
            throw new ArgumentException($"{length} units are needed and {destination.Length} were given.", nameof(destination));
        }

        // Fewer than 15 bits wait in the low bits of the accumulator between two bytes.
        uint pending = 0;
        var bits = 0;
        var written = 0;
        foreach (var b in data)
        {
            pending = (pending << 8) | b;
            bits += 8;
            if (bits >= BitsPerUnit)
            {
                bits -= BitsPerUnit;
                destination[written++] = (char)(FirstUnit + ((pending >> bits) & ValueMask));
                pending &= (1u << bits) - 1;
            }
        }

        if (bits > 0)
        {
            destination[written++] = (char)(FirstUnit + ((pending << (BitsPerUnit - bits)) & ValueMask));
        }

        return written;
    }

    /// <summary>Encodes <paramref name="data"/> into a new string.</summary>
    public static string Encode(ReadOnlySpan<byte> data)
    {
        var units = new char[EncodedLength(data.Length)];
        Encode(data, units);
        return new string(units);
    }

    /// <summary>
    /// Decodes <paramref name="byteCount"/> bytes from <paramref name="units"/> into the
    /// start of <paramref name="destination"/>. Returns false, with the destination in an
    /// undefined state, when the unit count does not match the byte count, a unit is
    /// outside the alphabet, the padding bits are not zero, or the destination is too short.
    /// </summary>
    public static bool TryDecode(ReadOnlySpan<char> units, int byteCount, Span<byte> destination)
    {
        if (byteCount < 0 || destination.Length < byteCount || units.Length != EncodedLength(byteCount))
        {
            return false;
        }

        uint pending = 0;
        var bits = 0;
        var written = 0;
        foreach (var unit in units)
        {
            var value = unit - FirstUnit;
            if ((uint)value > ValueMask)
            {
                return false;
            }

            pending = (pending << BitsPerUnit) | (uint)value;
            bits += BitsPerUnit;
            while (bits >= 8 && written < byteCount)
            {
                bits -= 8;
                destination[written++] = (byte)(pending >> bits);
            }

            pending &= (1u << bits) - 1;
        }

        // What is left is the padding of the last unit, which the encoder writes as zeros.
        return written == byteCount && pending == 0;
    }
}
