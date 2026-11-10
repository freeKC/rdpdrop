using System.Text;
using System.Text.Json;
using UltimateRDP.Core.Protocol;
using UltimateRDP.Core.Transfer;

namespace UltimateRDP.Core.Tests.Protocol;

public class TransferMessageTests
{
    private const string Hash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private static Message RoundTrip(Message message) =>
        MessageSerializer.Deserialize(MessageSerializer.Serialize(message))
        ?? throw new InvalidOperationException("Deserialize returned null.");

    private static JsonElement ToJson(Message message) =>
        JsonDocument.Parse(MessageSerializer.Serialize(message)).RootElement;

    private static Message? Parse(string json) => MessageSerializer.Deserialize(Encoding.UTF8.GetBytes(json));

    public static TheoryData<Message> Samples =>
    [
        new TransferBeginMessage("t1", 3, 123_456_789_012),
        new FileBeginMessage("t1", 0, @"Photos\2026\a.jpg", 42, new DateTime(2026, 11, 9, 10, 30, 0, DateTimeKind.Utc)),
        new FileBeginMessage("t1", 1, "b.bin", 0),
        new FileChunkMessage("t1", 2, 262_144, "AAEC/+8="),
        new FileEndMessage("t1", 2, Hash),
        new TransferCancelMessage("t1", "The user cancelled the transfer."),
        new TransferCancelMessage("t1"),
        new FileAckMessage("t1", 2, 2_097_152),
        new TransferResultMessage("t1", DropStatus.Ok, "Done", 5_000_000_000, 12.5),
        new TransferResultMessage("t1", DropStatus.Cancelled),
        new DropMessage("d1", 10, 20, KeyStates.None, Deferred: true),
        new DragAckMessage("d1", true, Transfer: true, TransferId: "t1"),
        new DragEnterMessage("d1", 1, 2, [new DropItem(@"C:\a.bin", false, 50_000_000), new DropItem(@"C:\Folder", true, 0)]),
    ];

    [Theory]
    [MemberData(nameof(Samples))]
    public void Messages_round_trip(Message message)
    {
        Assert.Equal(message, RoundTrip(message));
    }

    [Theory]
    [MemberData(nameof(Samples))]
    public void Messages_encode_as_one_ascii_line(Message message)
    {
        var line = LineFraming.EncodeLine(message);

        Assert.Equal('\n', line[^1]);
        Assert.All(line, c => Assert.True(c < 0x80));
        Assert.Equal(1, line.Count(c => c == '\n'));
    }

    [Fact]
    public void Type_names_match_the_spec()
    {
        Assert.Equal("transferBegin", new TransferBeginMessage("t", 0, 0).Type);
        Assert.Equal("fileBegin", new FileBeginMessage("t", 0, "a", 0).Type);
        Assert.Equal("fileChunk", new FileChunkMessage("t", 0, 0, "").Type);
        Assert.Equal("fileEnd", new FileEndMessage("t", 0, Hash).Type);
        Assert.Equal("transferCancel", new TransferCancelMessage("t").Type);
        Assert.Equal("fileAck", new FileAckMessage("t", 0, 0).Type);
        Assert.Equal("transferResult", new TransferResultMessage("t", DropStatus.Ok).Type);
    }

    [Fact]
    public void File_begin_json_has_spec_shape()
    {
        var json = ToJson(new FileBeginMessage("t1", 4, @"Photos\a.jpg", 42, new DateTime(2026, 11, 9, 10, 30, 0, DateTimeKind.Utc)));

        Assert.Equal("fileBegin", json.GetProperty("type").GetString());
        Assert.Equal("t1", json.GetProperty("transferId").GetString());
        Assert.Equal(4, json.GetProperty("fileIndex").GetInt32());
        Assert.Equal(@"Photos\a.jpg", json.GetProperty("relativePath").GetString());
        Assert.Equal(42, json.GetProperty("size").GetInt64());
        Assert.Equal("2026-11-09T10:30:00Z", json.GetProperty("modified").GetString());
    }

    [Fact]
    public void Transfer_result_json_has_spec_shape()
    {
        var json = ToJson(new TransferResultMessage("t1", DropStatus.Error, "Disk full", 10, 1.5));

        Assert.Equal("transferResult", json.GetProperty("type").GetString());
        Assert.Equal("error", json.GetProperty("status").GetString());
        Assert.Equal("Disk full", json.GetProperty("message").GetString());
        Assert.Equal(10, json.GetProperty("bytes").GetInt64());
        Assert.Equal(1.5, json.GetProperty("seconds").GetDouble());
    }

    [Fact]
    public void Optional_fields_are_not_written_when_absent()
    {
        var drop = ToJson(new DropMessage("d1", 1, 2));
        var ack = ToJson(new DragAckMessage("d1", true));
        var item = ToJson(new DragEnterMessage("d1", 1, 2, [new DropItem(@"C:\a", false)])).GetProperty("items")[0];
        var begin = ToJson(new FileBeginMessage("t1", 0, "a", 0));

        Assert.False(drop.TryGetProperty("deferred", out _));
        Assert.False(ack.TryGetProperty("transfer", out _));
        Assert.False(ack.TryGetProperty("transferId", out _));
        Assert.False(item.TryGetProperty("size", out _));
        Assert.False(begin.TryGetProperty("modified", out _));
    }

    [Fact]
    public void A_drop_of_version_0_3_reads_as_not_deferred()
    {
        var drop = Assert.IsType<DropMessage>(Parse("""{"type":"drop","id":"d1","x":1,"y":2,"keyState":0}"""));

        Assert.Null(drop.Deferred);
    }

    [Theory]
    [InlineData("""{"type":"transferBegin","transferId":"","files":1,"bytes":1}""")]
    [InlineData("""{"type":"transferBegin","transferId":"t","files":-1,"bytes":1}""")]
    [InlineData("""{"type":"transferBegin","transferId":"t","files":1,"bytes":-1}""")]
    [InlineData("""{"type":"fileBegin","transferId":"t","fileIndex":-1,"relativePath":"a","size":1}""")]
    [InlineData("""{"type":"fileBegin","transferId":"t","fileIndex":0,"relativePath":"","size":1}""")]
    [InlineData("""{"type":"fileBegin","transferId":"t","fileIndex":0,"relativePath":"a","size":-1}""")]
    [InlineData("""{"type":"fileBegin","transferId":"t","fileIndex":0,"size":1}""")]
    [InlineData("""{"type":"fileChunk","transferId":"t","fileIndex":0,"offset":-5,"data":"AA=="}""")]
    [InlineData("""{"type":"fileChunk","transferId":"t","fileIndex":0,"offset":0}""")]
    [InlineData("""{"type":"fileEnd","transferId":"t","fileIndex":0,"sha256":"ABC"}""")]
    [InlineData("""{"type":"fileEnd","transferId":"t","fileIndex":0,"sha256":"0123456789ABCDEF0123456789abcdef0123456789abcdef0123456789abcdef"}""")]
    [InlineData("""{"type":"fileAck","transferId":"t","fileIndex":0,"received":-1}""")]
    [InlineData("""{"type":"transferResult","transferId":"t","status":"done"}""")]
    [InlineData("""{"type":"transferResult","transferId":"t","status":"ok","seconds":-1}""")]
    [InlineData("""{"type":"transferCancel"}""")]
    [InlineData("""{"type":"dragAck","id":"d","accepted":true,"transfer":true}""")]
    [InlineData("""{"type":"dragEnter","id":"d","x":1,"y":2,"items":[{"clientPath":"C:\\a","isDirectory":false,"size":-1}]}""")]
    public void Malformed_transfer_messages_are_rejected(string json)
    {
        Assert.Null(Parse(json));
    }

    [Fact]
    public void The_direct_chunk_line_reads_as_the_serialized_message()
    {
        var data = new byte[1000];
        new Random(3).NextBytes(data);
        var expected = new FileChunkMessage("0123456789abcdef", 7, 524_288, Convert.ToBase64String(data));

        var line = TransferLines.EncodeChunk("0123456789abcdef", 7, 524_288, data);

        Assert.Equal('\n', line[^1]);
        Assert.Equal(expected, MessageSerializer.Deserialize(Encoding.ASCII.GetBytes(line.TrimEnd('\n'))));
        Assert.Equal(LineFraming.EncodeLine(expected).Replace("\\u002B", "+", StringComparison.Ordinal), line);
    }

    [Fact]
    public void An_id_that_json_would_escape_falls_back_to_the_serializer()
    {
        var line = TransferLines.EncodeChunk("a\"b", 0, 0, [1, 2, 3]);

        Assert.Equal(new FileChunkMessage("a\"b", 0, 0, "AQID"), MessageSerializer.Deserialize(Encoding.ASCII.GetBytes(line.TrimEnd('\n'))));
    }

    [Fact]
    public void A_chunk_of_the_largest_size_fits_in_a_line()
    {
        var line = TransferLines.EncodeChunk("0123456789abcdef0123456789abcdef", int.MaxValue, long.MaxValue, new byte[TransferSender.MaxChunkSize]);

        Assert.True(line.Length - 1 <= LineFraming.MaxLineLength);
    }

    [Theory]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef", true)]
    [InlineData("0123456789ABCDEF0123456789abcdef0123456789abcdef0123456789abcdef", false)]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcde", false)]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdeg", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Checksums_are_64_lower_case_hexadecimal_digits(string? value, bool valid)
    {
        Assert.Equal(valid, TransferHash.IsValidHex(value));
    }

    [Fact]
    public void The_hello_of_the_agent_says_the_model_of_the_data_of_the_client()
    {
        var utf16 = ToJson(HelloMessage.FromAgent("0.5.0", true, ChannelByteModel.Utf16));
        var oneByte = ToJson(HelloMessage.FromAgent("0.5.0", true, ChannelByteModel.OneBytePerChar));
        var unknown = ToJson(HelloMessage.FromAgent("0.5.0", true));

        Assert.Equal("utf16", utf16.GetProperty("clientToAgentModel").GetString());
        Assert.Equal("oneBytePerChar", oneByte.GetProperty("clientToAgentModel").GetString());
        Assert.False(unknown.TryGetProperty("clientToAgentModel", out _));
    }

    [Theory]
    [InlineData("""{"type":"hello","version":1,"agentVersion":"0.5.0","clientToAgentModel":"utf16"}""", ChannelByteModel.Utf16)]
    [InlineData("""{"type":"hello","version":1,"agentVersion":"0.5.0","clientToAgentModel":"oneBytePerChar"}""", ChannelByteModel.OneBytePerChar)]
    [InlineData("""{"type":"hello","version":1,"agentVersion":"0.4.4","driveRedirectionAvailable":true}""", ChannelByteModel.Unknown)]
    [InlineData("""{"type":"hello","version":1,"agentVersion":"0.6.0","clientToAgentModel":"other"}""", ChannelByteModel.Unknown)]
    public void The_client_reads_the_model_and_an_older_agent_gets_base64(string json, ChannelByteModel model)
    {
        var hello = Assert.IsType<HelloMessage>(Parse(json));
        var read = ChannelByteModelNames.FromName(hello.ClientToAgentModel);

        Assert.Equal(model, read);
        Assert.Equal(model == ChannelByteModel.Utf16 ? ChunkEncodings.U15 : ChunkEncodings.Base64, ChunkEncodings.Choose(read));
    }

    [Fact]
    public void TransferBegin_announces_the_chunk_encoding()
    {
        var json = ToJson(new TransferBeginMessage("t1", 1, 2, ChunkEncodings.U15));

        Assert.Equal("u15", json.GetProperty("chunkEncoding").GetString());
        Assert.False(ToJson(new TransferBeginMessage("t1", 1, 2)).TryGetProperty("chunkEncoding", out _));
        Assert.Null(Assert.IsType<TransferBeginMessage>(Parse("""{"type":"transferBegin","transferId":"t1","files":1,"bytes":2}""")).ChunkEncoding);
    }

    [Fact]
    public void A_provisional_dragAck_says_so_and_a_final_one_does_not()
    {
        var provisional = ToJson(new DragAckMessage("d1", true, "Starting...", Provisional: true));

        Assert.True(provisional.GetProperty("provisional").GetBoolean());
        Assert.False(ToJson(new DragAckMessage("d1", true)).TryGetProperty("provisional", out _));
        Assert.Null(Assert.IsType<DragAckMessage>(Parse("""{"type":"dragAck","id":"d1","accepted":true}""")).Provisional);
        Assert.True(Assert.IsType<DragAckMessage>(Parse("""{"type":"dragAck","id":"d1","accepted":true,"provisional":true}""")).Provisional);
    }

    [Fact]
    public void A_serialized_chunk_writes_its_data_last()
    {
        var u15 = LineFraming.EncodeLine(new FileChunkMessage("t1", 0, 5, "AAAA", 3, ChunkEncodings.U15));
        var base64 = LineFraming.EncodeLine(new FileChunkMessage("t1", 0, 5, "AAAA"));

        Assert.Equal("""{"type":"fileChunk","transferId":"t1","fileIndex":0,"offset":5,"length":3,"enc":"u15","data":"AAAA"}""" + "\n", u15);
        Assert.Equal("""{"type":"fileChunk","transferId":"t1","fileIndex":0,"offset":5,"data":"AAAA"}""" + "\n", base64);
    }

    [Fact]
    public void Model_names_go_both_ways()
    {
        Assert.Equal("utf16", ChannelByteModelNames.ToName(ChannelByteModel.Utf16));
        Assert.Equal("oneBytePerChar", ChannelByteModelNames.ToName(ChannelByteModel.OneBytePerChar));
        Assert.Null(ChannelByteModelNames.ToName(ChannelByteModel.Unknown));
        Assert.Equal(ChannelByteModel.Unknown, ChannelByteModelNames.FromName(null));
    }

    [Fact]
    public void TransferResend_json_has_spec_shape()
    {
        var json = ToJson(new TransferResendMessage("t1", 1, 2));

        Assert.Equal("transferResend", json.GetProperty("type").GetString());
        Assert.Equal("t1", json.GetProperty("transferId").GetString());
        Assert.Equal(1, json.GetProperty("from").GetInt64());
        Assert.Equal(2, json.GetProperty("to").GetInt64());
        Assert.Equal(new TransferResendMessage("t1", 1, 2), RoundTrip(new TransferResendMessage("t1", 1, 2)));
        Assert.Equal("{\"type\":\"transferResend\",\"transferId\":\"t1\",\"from\":1,\"to\":2}\n", LineFraming.EncodeLine(new TransferResendMessage("t1", 1, 2)));
    }

    [Theory]
    [InlineData("{\"type\":\"transferResend\",\"transferId\":\"t1\",\"from\":2,\"to\":1}")]
    [InlineData("{\"type\":\"transferResend\",\"transferId\":\"t1\",\"from\":-1,\"to\":1}")]
    [InlineData("{\"type\":\"transferResend\",\"from\":0,\"to\":1}")]
    [InlineData("{\"type\":\"fileAck\",\"transferId\":\"t1\",\"fileIndex\":0,\"received\":1,\"seq\":-1}")]
    [InlineData("{\"type\":\"fileChunk\",\"transferId\":\"t1\",\"fileIndex\":0,\"offset\":0,\"seq\":-2,\"data\":\"AA==\"}")]
    public void Malformed_numbers_are_rejected(string json)
    {
        Assert.Null(Parse(json));
    }

    [Fact]
    public void Numbered_lines_and_acknowledgements_round_trip()
    {
        Assert.Equal(new TransferBeginMessage("t1", 1, 2, ChunkEncodings.U15, 0), RoundTrip(new TransferBeginMessage("t1", 1, 2, ChunkEncodings.U15, 0)));
        Assert.Equal(new FileBeginMessage("t1", 0, "a", 2, Seq: 1), RoundTrip(new FileBeginMessage("t1", 0, "a", 2, Seq: 1)));
        Assert.Equal(new FileEndMessage("t1", 0, Hash, 9), RoundTrip(new FileEndMessage("t1", 0, Hash, 9)));
        Assert.Equal(new FileAckMessage("t1", 0, 2, 9), RoundTrip(new FileAckMessage("t1", 0, 2, 9)));
        Assert.Equal(9, ToJson(new FileAckMessage("t1", 0, 2, 9)).GetProperty("seq").GetInt64());
        Assert.False(ToJson(new FileAckMessage("t1", 0, 2)).TryGetProperty("seq", out _));
    }

    /// <summary>An agent 0.5.0 reads numbered lines of base64 and the short lines with the JSON reader, which skips the field.</summary>
    [Fact]
    public void A_reader_that_does_not_know_seq_reads_the_rest_of_the_line()
    {
        var json = "{\"type\":\"fileBegin\",\"transferId\":\"t1\",\"fileIndex\":0,\"relativePath\":\"a\",\"size\":2,\"seq\":1,\"later\":true}";

        Assert.Equal(new FileBeginMessage("t1", 0, "a", 2, Seq: 1), Parse(json));
    }

    [Fact]
    public void The_hello_of_the_agent_says_it_reads_numbered_lines()
    {
        var hello = HelloMessage.FromAgent("0.5.1", true, ChannelByteModel.Utf16, reliableTransfer: true);

        Assert.True(ToJson(hello).GetProperty("reliableTransfer").GetBoolean());
        Assert.Equal(hello, RoundTrip(hello));
        Assert.False(ToJson(HelloMessage.FromAgent("0.5.0", true)).TryGetProperty("reliableTransfer", out _));
        Assert.Null(Assert.IsType<HelloMessage>(Parse("{\"type\":\"hello\",\"version\":1,\"agentVersion\":\"0.5.0\"}")).ReliableTransfer);
    }
}
