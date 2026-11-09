using System.Text;
using UltimateRDP.Core.Protocol;
using UltimateRDP.Core.Transfer;

namespace UltimateRDP.Core.Tests.Transfer;

public class TransferLinesTests
{
    private const string Id = "0123456789abcdef0123456789abcdef";

    private static byte[] RandomBytes(int count, int seed)
    {
        var data = new byte[count];
        new Random(seed).NextBytes(data);
        return data;
    }

    /// <summary>What the agent does with a line: the hand parser first, then the JSON reader on its ASCII bytes.</summary>
    private static Message? ReadAsAgent(string line) =>
        TransferLines.TryParseChunk(line, out var chunk) ? chunk : MessageSerializer.Deserialize(Encoding.ASCII.GetBytes(line));

    private static byte[] DecodeU15(FileChunkMessage chunk)
    {
        Assert.Equal(ChunkEncodings.U15, chunk.Enc);
        var length = Assert.IsType<int>(chunk.Length);
        var data = new byte[length];
        Assert.True(U15Codec.TryDecode(chunk.Data, length, data));
        return data;
    }

    [Fact]
    public void A_u15_line_has_the_ascii_fields_first_and_the_units_last()
    {
        var data = RandomBytes(1000, 1);

        var line = TransferLines.EncodeChunk(Id, 2, 262_144, data, ChunkEncodings.U15);

        var prefix = $"{{\"type\":\"fileChunk\",\"transferId\":\"{Id}\",\"fileIndex\":2,\"offset\":262144,\"length\":1000,\"enc\":\"u15\",\"data\":\"";
        Assert.StartsWith(prefix, line, StringComparison.Ordinal);
        Assert.EndsWith("\"}\n", line, StringComparison.Ordinal);
        var units = line[prefix.Length..^3];
        Assert.Equal(U15Codec.EncodedLength(1000), units.Length);
        Assert.All(units, unit => Assert.True(U15Codec.IsUnit(unit)));
        Assert.Equal(1, line.Count(c => c == '\n'));
        Assert.True(prefix.All(c => c < 0x80));
    }

    [Fact]
    public void A_base64_line_keeps_the_form_of_0_4()
    {
        var line = TransferLines.EncodeChunk(Id, 0, 0, [1, 2, 3]);

        Assert.Equal($"{{\"type\":\"fileChunk\",\"transferId\":\"{Id}\",\"fileIndex\":0,\"offset\":0,\"data\":\"AQID\"}}\n", line);
        Assert.Equal(line, TransferLines.EncodeChunk(Id, 0, 0, [1, 2, 3], ChunkEncodings.Base64));
    }

    [Fact]
    public void An_unknown_encoding_is_refused()
    {
        Assert.Throws<ArgumentException>(() => TransferLines.EncodeChunk(Id, 0, 0, [1], "l8"));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(15)]
    [InlineData(16)]
    [InlineData(131_072)]
    [InlineData(TransferSender.MaxChunkSize)]
    public void The_hand_parser_reads_back_what_the_line_says(int size)
    {
        var data = RandomBytes(size, size);
        var line = TransferLines.EncodeChunk(Id, 7, 9_000_000_000, data, ChunkEncodings.U15);

        Assert.True(TransferLines.TryParseChunk(line[..^1], out var chunk));

        Assert.Equal(Id, chunk.TransferId);
        Assert.Equal(7, chunk.FileIndex);
        Assert.Equal(9_000_000_000, chunk.Offset);
        Assert.Equal(size, chunk.Length);
        Assert.Equal(data, DecodeU15(chunk));
    }

    [Fact]
    public void The_hand_parser_leaves_every_other_line_to_the_json_reader()
    {
        var base64 = TransferLines.EncodeChunk(Id, 0, 0, [1, 2, 3])[..^1];
        var hello = LineFraming.EncodeLine(HelloMessage.FromClient("0.5.0", "fr"))[..^1];
        var begin = LineFraming.EncodeLine(new TransferBeginMessage(Id, 1, 3, ChunkEncodings.U15))[..^1];

        Assert.False(TransferLines.TryParseChunk(base64, out _));
        Assert.False(TransferLines.TryParseChunk(hello, out _));
        Assert.False(TransferLines.TryParseChunk(begin, out _));
        Assert.Equal(new FileChunkMessage(Id, 0, 0, "AQID"), ReadAsAgent(base64));
        Assert.IsType<HelloMessage>(ReadAsAgent(hello));
        Assert.Equal(new TransferBeginMessage(Id, 1, 3, ChunkEncodings.U15), ReadAsAgent(begin));
    }

    public static TheoryData<string> BrokenLines
    {
        get
        {
            var good = TransferLines.EncodeChunk(Id, 1, 2, [9, 8, 7], ChunkEncodings.U15)[..^1];
            return
            [
                good[..^1],
                good.Replace("\"fileIndex\":1", "\"fileIndex\":-1", StringComparison.Ordinal),
                good.Replace("\"offset\":2", "\"offset\":", StringComparison.Ordinal),
                good.Replace("\"length\":3", "\"length\":99999999999", StringComparison.Ordinal),
                good.Replace(",\"length\":3", string.Empty, StringComparison.Ordinal),
                good.Replace("\"enc\":\"u15\"", "\"enc\":\"l8\"", StringComparison.Ordinal),
                good.Replace(Id, "a b", StringComparison.Ordinal),
                good.Replace(Id, string.Empty, StringComparison.Ordinal),
                "{\"type\":\"fileChunk\",\"transferId\":\"" + Id + "\"}",
            ];
        }
    }

    [Theory]
    [MemberData(nameof(BrokenLines))]
    public void Lines_that_differ_from_the_written_form_are_not_read_by_hand(string line)
    {
        Assert.False(TransferLines.TryParseChunk(line, out var chunk));
        Assert.Null(chunk);
    }

    /// <summary>
    /// The serializer writes the units of a u15 chunk as JSON escapes: such a line is pure
    /// ASCII, goes to the JSON reader, and gives the same chunk.
    /// </summary>
    [Fact]
    public void A_serialized_u15_chunk_goes_to_the_json_reader_and_reads_the_same()
    {
        var data = RandomBytes(100, 5);
        var message = new FileChunkMessage(Id, 0, 0, U15Codec.Encode(data), data.Length, ChunkEncodings.U15);

        var line = LineFraming.EncodeLine(message)[..^1];

        Assert.True(line.All(c => c < 0x80));
        Assert.False(TransferLines.TryParseChunk(line, out _));
        var read = Assert.IsType<FileChunkMessage>(ReadAsAgent(line));
        Assert.Equal(message, read);
        Assert.Equal(data, DecodeU15(read));
    }

    [Fact]
    public void An_id_that_json_would_escape_falls_back_to_the_serializer()
    {
        var data = RandomBytes(10, 6);

        var line = TransferLines.EncodeChunk("a\"b", 0, 0, data, ChunkEncodings.U15);

        Assert.True(line.All(c => c < 0x80));
        var read = Assert.IsType<FileChunkMessage>(ReadAsAgent(line[..^1]));
        Assert.Equal("a\"b", read.TransferId);
        Assert.Equal(data, DecodeU15(read));
    }

    /// <summary>
    /// The conversions of the agent path leave a u15 line as it was: UTF-16LE bytes back to
    /// text, the trims of the line reader and of the agent. Only the client to control
    /// conversion of 0.4, which the client never applies to its own lines, refuses it.
    /// </summary>
    [Fact]
    public void A_u15_line_survives_the_conversions_of_the_agent_path()
    {
        var line = TransferLines.EncodeChunk(Id, 0, 0, RandomBytes(4096, 7), ChunkEncodings.U15);
        var bytes = ChannelTextCodec.StringToUtf16LittleEndian(line);

        Assert.Equal(2 * line.Length, bytes.Length);
        Assert.Equal(ChannelByteModel.Utf16, ChannelTextCodec.DetectFromBytes(bytes));
        Assert.Equal(line.Length * 2, ChannelTextCodec.UsableLength(bytes, ChannelByteModel.Utf16));
        Assert.Equal(line, ChannelTextCodec.BytesToText(bytes, ChannelByteModel.Utf16));

        var body = line[..^1];
        Assert.Same(body, body.Trim(['\0', ' ']));
        Assert.Equal(body, body.AsSpan().TrimEnd([' ', '\r']).ToString());

        // The line is not ASCII: the ASCII conversion that the other lines take would ruin it.
        Assert.Contains((byte)'?', Encoding.ASCII.GetBytes(body));
        Assert.Throws<ArgumentException>(() => ChannelTextCodec.TextToControlString(line, ChannelByteModel.Utf16));
    }

    public static TheoryData<int> ReadSizes => [1600, 1599];

    /// <summary>
    /// u15 lines with the other lines of a transfer, in the UTF-16 model, through the reads
    /// of the agent: the decoder, the line reader, the trims and the hand parser. A read of
    /// 1599 bytes cuts every unit in two at some point.
    /// </summary>
    [Theory]
    [MemberData(nameof(ReadSizes))]
    public void U15_lines_go_through_the_reads_of_the_agent_in_the_utf16_model(int readSize)
    {
        var files = new[] { RandomBytes(131_072, 10), RandomBytes(1, 11), RandomBytes(70_001, 12) };
        var sent = new List<string> { LineFraming.EncodeLine(HelloMessage.FromClient("0.5.0", "en")) };
        for (var i = 0; i < files.Length; i++)
        {
            sent.Add(LineFraming.EncodeLine(new FileBeginMessage(Id, i, "f" + i, files[i].Length)));
            sent.Add(TransferLines.EncodeChunk(Id, i, 0, files[i], ChunkEncodings.U15));
        }

        var received = ReadThroughAgent(ChannelTextCodec.StringToUtf16LittleEndian(string.Concat(sent)), readSize, ChannelByteModel.Utf16);

        Assert.Equal(sent.Count, received.Count);
        Assert.IsType<HelloMessage>(received[0]);
        for (var i = 0; i < files.Length; i++)
        {
            Assert.IsType<FileBeginMessage>(received[1 + (2 * i)]);
            var chunk = Assert.IsType<FileChunkMessage>(received[2 + (2 * i)]);
            Assert.Equal(i, chunk.FileIndex);
            Assert.Equal(files[i], DecodeU15(chunk));
        }
    }

    /// <summary>In the one byte per character model the chunks stay in base64 and take the JSON path, as in 0.4.</summary>
    [Theory]
    [MemberData(nameof(ReadSizes))]
    public void Base64_lines_go_through_the_reads_of_the_agent_in_the_one_byte_model(int readSize)
    {
        var data = RandomBytes(131_072, 13);
        var sent = new List<string>
        {
            LineFraming.EncodeLine(HelloMessage.FromClient("0.5.0", "en")),
            TransferLines.EncodeChunk(Id, 0, 0, data),
            TransferLines.EncodeChunk(Id, 0, data.Length, data.AsSpan(0, 5)),
        };

        var received = ReadThroughAgent(Encoding.ASCII.GetBytes(string.Concat(sent)), readSize, ChannelByteModel.OneBytePerChar);

        Assert.Equal(3, received.Count);
        var first = Assert.IsType<FileChunkMessage>(received[1]);
        Assert.Null(first.Enc);
        Assert.Equal(data, Convert.FromBase64String(first.Data));
        Assert.Equal(data.AsSpan(0, 5).ToArray(), Convert.FromBase64String(Assert.IsType<FileChunkMessage>(received[2]).Data));
    }

    /// <summary>The read path of the agent: decoder, line reader, the trims of the agent, then <see cref="ReadAsAgent"/>.</summary>
    private static List<Message> ReadThroughAgent(byte[] wire, int readSize, ChannelByteModel expected)
    {
        var decoder = new IncomingTextDecoder();
        var reader = new LineReader();
        var messages = new List<Message>();
        for (var start = 0; start < wire.Length; start += readSize)
        {
            reader.Append(decoder.Decode(wire.AsSpan(start, Math.Min(readSize, wire.Length - start)), out var rejected));
            Assert.False(rejected);
            while (reader.TryReadLine(out var line))
            {
                messages.Add(ReadAsAgent(line.Trim(['\0', ' '])) ?? throw new InvalidOperationException("A line did not read back."));
            }
        }

        Assert.Equal(expected, decoder.Model);
        Assert.Equal(0, reader.BufferedLength);
        Assert.Equal(0, reader.ProtocolErrorCount);
        return messages;
    }
}
