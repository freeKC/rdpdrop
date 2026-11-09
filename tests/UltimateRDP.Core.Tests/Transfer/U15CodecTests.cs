using UltimateRDP.Core.Protocol;
using UltimateRDP.Core.Transfer;

namespace UltimateRDP.Core.Tests.Transfer;

public class U15CodecTests
{
    private static byte[] RandomBytes(int count, int seed)
    {
        var data = new byte[count];
        new Random(seed).NextBytes(data);
        return data;
    }

    private static byte[] Decode(string units, int byteCount)
    {
        var data = new byte[byteCount];
        Assert.True(U15Codec.TryDecode(units, byteCount, data));
        return data;
    }

    [Fact]
    public void Nothing_encodes_to_nothing()
    {
        Assert.Equal(string.Empty, U15Codec.Encode([]));
        Assert.True(U15Codec.TryDecode(string.Empty, 0, []));
        Assert.False(U15Codec.TryDecode([U15Codec.FirstUnit], 0, []));
    }

    public static TheoryData<int> SmallCounts => [.. Enumerable.Range(1, 16)];

    [Theory]
    [MemberData(nameof(SmallCounts))]
    public void One_to_sixteen_bytes_round_trip(int count)
    {
        foreach (var seed in Enumerable.Range(0, 50))
        {
            var data = RandomBytes(count, seed);
            var units = U15Codec.Encode(data);

            Assert.Equal(U15Codec.EncodedLength(count), units.Length);
            Assert.Equal(data, Decode(units, count));
        }

        Assert.Equal(new byte[count], Decode(U15Codec.Encode(new byte[count]), count));
        var ones = Enumerable.Repeat((byte)0xFF, count).ToArray();
        Assert.Equal(ones, Decode(U15Codec.Encode(ones), count));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(15, 8)]
    [InlineData(16, 9)]
    [InlineData(30, 16)]
    [InlineData(32 * 1024, 17_477)]
    [InlineData(131_072, 69_906)]
    [InlineData(TransferSender.MaxChunkSize, 139_811)]
    public void The_length_is_the_bits_rounded_up_to_whole_units(int bytes, int units)
    {
        Assert.Equal(units, U15Codec.EncodedLength(bytes));
        Assert.Equal(units, U15Codec.Encode(new byte[bytes]).Length);
    }

    [Fact]
    public void A_chunk_of_128_KiB_round_trips()
    {
        var data = RandomBytes(131_072, 42);

        var units = U15Codec.Encode(data);

        Assert.Equal(69_906, units.Length);
        Assert.Equal(data, Decode(units, data.Length));
    }

    [Fact]
    public void Random_lengths_round_trip()
    {
        var random = new Random(3);
        for (var i = 0; i < 300; i++)
        {
            var count = random.Next(0, 5000);
            var data = RandomBytes(count, i);

            Assert.Equal(data, Decode(U15Codec.Encode(data), count));
        }
    }

    /// <summary>The high bit of the first byte goes first, and the last unit is padded with zero bits.</summary>
    [Fact]
    public void The_bits_go_in_order_and_the_tail_is_padded_with_zeros()
    {
        var units = U15Codec.Encode([0x80, 0x01]);

        // 1000 0000 0000 000|1 + 14 zero bits of padding.
        Assert.Equal(2, units.Length);
        Assert.Equal(U15Codec.FirstUnit + 0x4000, units[0]);
        Assert.Equal(U15Codec.FirstUnit + 0x4000, units[1]);
    }

    [Fact]
    public void Fifteen_bytes_fill_eight_units_without_padding()
    {
        var units = U15Codec.Encode(Enumerable.Repeat((byte)0xFF, 15).ToArray());

        Assert.Equal(new string(U15Codec.LastUnit, 8), units);
    }

    [Fact]
    public void Padding_bits_that_are_not_zero_are_refused()
    {
        var units = U15Codec.Encode([0xAB]).ToCharArray();

        // One byte leaves seven bits of padding in the only unit.
        units[0] = (char)(units[0] | 1);

        Assert.False(U15Codec.TryDecode(units, 1, new byte[1]));
    }

    [Fact]
    public void A_unit_count_that_does_not_match_the_byte_count_is_refused()
    {
        var units = U15Codec.Encode(RandomBytes(100, 1));

        Assert.Equal(54, units.Length);
        Assert.False(U15Codec.TryDecode(units, 99, new byte[100]));
        Assert.False(U15Codec.TryDecode(units, 102, new byte[102]));
        Assert.False(U15Codec.TryDecode(units.AsSpan(0, units.Length - 1), 100, new byte[100]));
        Assert.False(U15Codec.TryDecode(units, 100, new byte[99]));
        Assert.False(U15Codec.TryDecode(units, -1, new byte[100]));
    }

    /// <summary>
    /// 100 and 101 bytes both take 54 units, and the ten bits of padding of 100 bytes read
    /// as a zero byte: the units alone do not tell the length, which is why the line gives
    /// it. A wrong length is then caught by the size and the checksum of the file.
    /// </summary>
    [Fact]
    public void The_length_comes_from_the_line_and_not_from_the_units()
    {
        var data = RandomBytes(100, 1);
        var units = U15Codec.Encode(data);
        var longer = new byte[101];

        Assert.Equal(U15Codec.EncodedLength(101), units.Length);
        Assert.True(U15Codec.TryDecode(units, 101, longer));
        Assert.Equal([.. data, 0], longer);
    }

    [Theory]
    [InlineData(0x0FFF)]
    [InlineData(0x9000)]
    [InlineData(0xD800)]
    [InlineData(0x0041)]
    [InlineData(0x0022)]
    [InlineData(0x005C)]
    [InlineData(0x000A)]
    public void A_unit_outside_the_alphabet_is_refused(int unit)
    {
        var units = U15Codec.Encode(RandomBytes(30, 2)).ToCharArray();
        units[7] = (char)unit;

        Assert.False(U15Codec.IsUnit((char)unit));
        Assert.False(U15Codec.TryDecode(units, 30, new byte[30]));
    }

    /// <summary>
    /// No surrogate, no control character and nothing of ASCII: JSON takes the units as
    /// they are, UTF-16 carries each one as two bytes, and the line reader, which splits on
    /// U+000A and trims spaces and U+000D, never touches them.
    /// </summary>
    [Fact]
    public void The_alphabet_holds_no_character_that_a_line_or_a_json_string_treats_apart()
    {
        Assert.Equal(0x1000, U15Codec.FirstUnit);
        Assert.Equal(0x8FFF, U15Codec.LastUnit);
        Assert.Equal(1 << U15Codec.BitsPerUnit, U15Codec.LastUnit - U15Codec.FirstUnit + 1);
        for (int c = U15Codec.FirstUnit; c <= U15Codec.LastUnit; c++)
        {
            var unit = (char)c;
            Assert.True(U15Codec.IsUnit(unit));
            Assert.False(char.IsSurrogate(unit));
            Assert.False(char.IsControl(unit));
            Assert.True(c > 0x7F);
        }

        Assert.False(U15Codec.IsUnit((char)(U15Codec.FirstUnit - 1)));
        Assert.False(U15Codec.IsUnit((char)(U15Codec.LastUnit + 1)));
    }

    /// <summary>
    /// The range holds Unicode separators such as U+2028 and U+3000. Only an argumentless
    /// trim would take them; the line reader and the agent trim given characters, and the
    /// units never sit at the ends of a line.
    /// </summary>
    [Fact]
    public void Unicode_separators_of_the_range_stay_inside_their_line()
    {
        char[] separators = [(char)0x1680, (char)0x2000, (char)0x2028, (char)0x2029, (char)0x3000];
        var text = "{\"data\":\"" + new string(separators) + "\"}";
        var reader = new LineReader();
        reader.Append(text + "\n" + text + " \r\n");

        Assert.All(separators, separator => Assert.True(U15Codec.IsUnit(separator)));
        Assert.True(reader.TryReadLine(out var first));
        Assert.True(reader.TryReadLine(out var second));
        Assert.Equal(text, first);
        Assert.Equal(text, second);
        Assert.Equal(text, first.Trim(['\0', ' ']));
    }

    [Fact]
    public void Every_value_maps_to_its_own_unit()
    {
        // Two bytes per value, 15 bits of it used: value v sits in the high bits.
        for (var v = 0; v < 1 << U15Codec.BitsPerUnit; v += 97)
        {
            var bits = v << 1;
            var units = U15Codec.Encode([(byte)(bits >> 8), (byte)bits]);

            Assert.Equal(U15Codec.FirstUnit + v, units[0]);
        }
    }

    [Fact]
    public void The_encoder_needs_room_for_every_unit()
    {
        Assert.Throws<ArgumentException>(() => U15Codec.Encode(new byte[16], new char[8]));
        Assert.Equal(9, U15Codec.Encode(new byte[16], new char[9]));
    }

    [Fact]
    public void The_client_chooses_u15_only_when_its_data_arrives_as_utf16()
    {
        Assert.Equal(ChunkEncodings.U15, ChunkEncodings.Choose(ChannelByteModel.Utf16));
        Assert.Equal(ChunkEncodings.Base64, ChunkEncodings.Choose(ChannelByteModel.OneBytePerChar));
        Assert.Equal(ChunkEncodings.Base64, ChunkEncodings.Choose(ChannelByteModel.Unknown));
    }

    [Fact]
    public void The_wire_budget_per_file_byte_follows_the_encoding_and_the_model()
    {
        Assert.Equal(4.0 / 3, ChunkEncodings.WireBytesPerFileByte(ChunkEncodings.Base64, ChannelByteModel.OneBytePerChar), 9);
        Assert.Equal(8.0 / 3, ChunkEncodings.WireBytesPerFileByte(ChunkEncodings.Base64, ChannelByteModel.Utf16), 9);
        Assert.Equal(16.0 / 15, ChunkEncodings.WireBytesPerFileByte(ChunkEncodings.U15, ChannelByteModel.Utf16), 9);
        Assert.Throws<ArgumentException>(() => ChunkEncodings.WireBytesPerFileByte(ChunkEncodings.U15, ChannelByteModel.OneBytePerChar));
        Assert.Throws<ArgumentException>(() => ChunkEncodings.WireBytesPerFileByte(ChunkEncodings.Base64, ChannelByteModel.Unknown));

        // The gain in the UTF-16 model: 2.5 times the file data on the same wire.
        Assert.Equal(2.5, ChunkEncodings.WireBytesPerFileByte(ChunkEncodings.Base64, ChannelByteModel.Utf16) / ChunkEncodings.WireBytesPerFileByte(ChunkEncodings.U15, ChannelByteModel.Utf16), 9);
    }

    [Fact]
    public void Encodings_are_known_by_name()
    {
        Assert.True(ChunkEncodings.IsKnown(null));
        Assert.True(ChunkEncodings.IsKnown("base64"));
        Assert.True(ChunkEncodings.IsKnown("u15"));
        Assert.False(ChunkEncodings.IsKnown("l8"));
        Assert.Equal("base64", ChunkEncodings.OrDefault(null));
    }
}
