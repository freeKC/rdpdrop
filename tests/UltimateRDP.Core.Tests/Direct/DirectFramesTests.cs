using System.Text;
using UltimateRDP.Core.Agent;
using UltimateRDP.Core.Direct;
using UltimateRDP.Core.Protocol;
using UltimateRDP.Core.Transfer;

namespace UltimateRDP.Core.Tests.Direct;

public class DirectFramesTests
{
    [Fact]
    public void A_frame_is_a_little_endian_length_of_its_payload_then_its_type()
    {
        using var stream = new MemoryStream();
        DirectFrames.Write(stream, DirectFrameType.Begin, DirectFrames.BeginPayload(3, 0x0102030405));

        Assert.Equal(
            [12, 0, 0, 0, 3, 3, 0, 0, 0, 5, 4, 3, 2, 1, 0, 0, 0],
            stream.ToArray());
    }

    [Fact]
    public void Frames_read_back_one_after_the_other_even_from_reads_of_one_byte()
    {
        using var stream = new MemoryStream();
        DirectFrames.Write(stream, DirectFrameType.Ready, []);
        DirectFrames.Write(stream, DirectFrameType.Data, [1, 2, 3]);
        DirectFrames.Write(stream, DirectFrameType.Ack, DirectFrames.AckPayload(new DirectAck(2, 10, 20)));
        var reader = new DirectFrameReader(new OneByteStream(stream.ToArray()));

        Assert.True(reader.TryRead(out var ready, out var empty));
        Assert.Equal(DirectFrameType.Ready, ready);
        Assert.Equal(0, empty.Length);
        Assert.True(reader.TryRead(out var data, out var bytes));
        Assert.Equal(DirectFrameType.Data, data);
        Assert.Equal([1, 2, 3], bytes.ToArray());
        Assert.True(reader.TryRead(out var ack, out var payload));
        Assert.Equal(DirectFrameType.Ack, ack);
        Assert.True(DirectFrames.TryParseAck(payload.Span, out var parsed));
        Assert.Equal(new DirectAck(2, 10, 20), parsed);
        Assert.False(reader.TryRead(out _, out _));
    }

    [Fact]
    public void A_stream_that_ends_inside_a_frame_is_an_error()
    {
        using var stream = new MemoryStream();
        DirectFrames.Write(stream, DirectFrameType.Data, [1, 2, 3]);
        var cut = stream.ToArray()[..^1];

        Assert.Throws<EndOfStreamException>(() => new DirectFrameReader(new MemoryStream(cut)).TryRead(out _, out _));
        Assert.Throws<EndOfStreamException>(() => new DirectFrameReader(new MemoryStream(cut[..3])).TryRead(out _, out _));
    }

    [Fact]
    public void An_unknown_type_or_a_length_beyond_the_limit_is_a_protocol_error()
    {
        byte[] unknown = [0, 0, 0, 0, 42];
        byte[] negative = [0xFF, 0xFF, 0xFF, 0xFF, 5];
        var tooLong = new byte[5];
        DirectFrames.WriteHeader(tooLong, DirectFrameType.Data, DirectFrames.MaxPayloadLength + 1);

        Assert.Throws<DirectProtocolException>(() => new DirectFrameReader(new MemoryStream(unknown)).TryRead(out _, out _));
        Assert.Throws<DirectProtocolException>(() => new DirectFrameReader(new MemoryStream(negative)).TryRead(out _, out _));
        Assert.Throws<DirectProtocolException>(() => new DirectFrameReader(new MemoryStream(tooLong)).TryRead(out _, out _));
    }

    [Fact]
    public void A_file_header_keeps_the_index_the_size_the_time_and_the_path()
    {
        var modified = new DateTime(2026, 11, 10, 9, 15, 30, DateTimeKind.Utc);
        var payload = DirectFrames.FileHeaderPayload(7, new TransferFile(@"Dossier\été\ü.txt", 123456789012, modified));

        Assert.True(DirectFrames.TryParseFileHeader(payload, out var index, out var file));
        Assert.Equal(7, index);
        Assert.Equal(new TransferFile(@"Dossier\été\ü.txt", 123456789012, modified), file);
        Assert.Equal(DateTimeKind.Utc, file.Modified!.Value.Kind);

        Assert.True(DirectFrames.TryParseFileHeader(DirectFrames.FileHeaderPayload(0, new TransferFile("a", 0)), out _, out var undated));
        Assert.Null(undated.Modified);
    }

    [Fact]
    public void A_malformed_file_header_is_refused()
    {
        var good = DirectFrames.FileHeaderPayload(0, new TransferFile("a", 1));
        var badUtf8 = good.ToArray();
        badUtf8[^1] = 0xFF;
        var negativeSize = good.ToArray();
        negativeSize[11] = 0x80;

        Assert.False(DirectFrames.TryParseFileHeader(good[..20], out _, out _));
        Assert.False(DirectFrames.TryParseFileHeader(badUtf8, out _, out _));
        Assert.False(DirectFrames.TryParseFileHeader(negativeSize, out _, out _));
    }

    [Fact]
    public void The_final_frame_keeps_its_fields_and_cuts_a_long_text_on_a_character()
    {
        var result = new DirectResult(DropStatus.Error, DirectFailure.HashMismatch, 42, 1234, "Les données sont endommagées.");
        Assert.True(DirectFrames.TryParseFinal(DirectFrames.FinalPayload(result), out var read));
        Assert.Equal(result, read);

        var longText = new string('é', 3000);
        Assert.True(DirectFrames.TryParseFinal(DirectFrames.FinalPayload(result with { Message = longText }), out var cut));
        Assert.Equal(DirectFrames.MaxTextBytes / 2, cut.Message.Length);
        Assert.All(cut.Message, c => Assert.Equal('é', c));
    }

    [Fact]
    public void A_final_frame_with_an_unknown_status_or_failure_is_refused()
    {
        var payload = DirectFrames.FinalPayload(new DirectResult(DropStatus.Ok, DirectFailure.None, 0, 0, string.Empty));
        var status = payload.ToArray();
        status[0] = 9;
        var failure = payload.ToArray();
        failure[1] = 200;

        Assert.False(DirectFrames.TryParseFinal(status, out _));
        Assert.False(DirectFrames.TryParseFinal(failure, out _));
        Assert.False(DirectFrames.TryParseFinal(payload[..17], out _));
    }

    [Fact]
    public void Trailer_begin_and_token_payloads_read_back()
    {
        var hash = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
        Assert.True(DirectFrames.TryParseFileTrailer(DirectFrames.FileTrailerPayload(5, hash), out var index, out var sha));
        Assert.Equal(5, index);
        Assert.Equal(hash, sha.ToArray());
        Assert.Throws<ArgumentException>(() => DirectFrames.FileTrailerPayload(0, hash[..31]));

        Assert.True(DirectFrames.TryParseBegin(DirectFrames.BeginPayload(2, 99), out var files, out var bytes));
        Assert.Equal((2, 99L), (files, bytes));
        Assert.False(DirectFrames.TryParseBegin(DirectFrames.BeginPayload(-1, 99), out _, out _));

        var token = DirectFrames.TokenPayload(hash);
        Assert.Equal(DirectFrames.ProtocolVersion, token[0]);
        Assert.Equal(hash, token[1..]);
        Assert.Equal("stop", Encoding.UTF8.GetString(DirectFrames.CancelPayload("stop")));
    }

    [Theory]
    [InlineData(null, "auto", true)]
    [InlineData("", "auto", true)]
    [InlineData(" Auto ", "auto", true)]
    [InlineData("NEVER", "never", true)]
    [InlineData("always", "auto", false)]
    public void The_direct_transfer_setting_reads_auto_and_never(string? value, string expected, bool recognized)
    {
        var mode = DirectTransferPolicy.Parse(value, out var known);

        Assert.Equal(expected, DirectTransferPolicy.ToSetting(mode));
        Assert.Equal(recognized, known);
    }

    [Theory]
    [InlineData(null, 3400, 3449, true)]
    [InlineData("3400-3449", 3400, 3449, true)]
    [InlineData(" 5000 - 5009 ", 5000, 5009, true)]
    [InlineData("5000", 5000, 5000, true)]
    [InlineData("3449-3400", 3400, 3449, false)]
    [InlineData("80-90", 3400, 3449, false)]
    [InlineData("60000-70000", 3400, 3449, false)]
    [InlineData("2000-4000", 3400, 3449, false)]
    [InlineData("a-b", 3400, 3449, false)]
    [InlineData("1-2-3", 3400, 3449, false)]
    [InlineData("+3400", 3400, 3449, false)]
    public void The_port_range_setting_is_checked(string? value, int first, int last, bool recognized)
    {
        var range = DirectTransferPolicy.ParsePortRange(value, out var known);

        Assert.Equal((first, last), range);
        Assert.Equal(recognized, known);
    }

    [Fact]
    public void The_port_range_is_written_as_the_setting_reads_it()
    {
        Assert.Equal("3400-3449", DirectTransferPolicy.ToSetting((3400, 3449)));
        Assert.Equal("5000", DirectTransferPolicy.ToSetting((5000, 5000)));
        Assert.Equal(DirectTransferPolicy.DefaultPortRange, DirectTransferPolicy.ToSetting((DirectTransferPolicy.DefaultFirstPort, DirectTransferPolicy.DefaultLastPort)));
    }

    /// <summary>A stream that gives one byte per read, as a slow network may.</summary>
    private sealed class OneByteStream(byte[] data) : MemoryStream(data)
    {
        public override int Read(byte[] buffer, int offset, int count) => base.Read(buffer, offset, Math.Min(1, count));

        public override int Read(Span<byte> buffer) => base.Read(buffer[..Math.Min(1, buffer.Length)]);
    }
}
