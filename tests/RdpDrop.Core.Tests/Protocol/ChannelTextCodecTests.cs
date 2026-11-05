using System.Text;
using RdpDrop.Core.Protocol;

namespace RdpDrop.Core.Tests.Protocol;

public class ChannelTextCodecTests
{
    private const string Line = "{\"type\":\"hello\",\"version\":1}\n";

    [Theory]
    [InlineData(new byte[] { 0x7B, 0x00 }, ChannelByteModel.Utf16)]
    [InlineData(new byte[] { 0x7B, 0x00, 0x22, 0x00 }, ChannelByteModel.Utf16)]
    [InlineData(new byte[] { 0x7B, 0x22 }, ChannelByteModel.OneBytePerChar)]
    [InlineData(new byte[] { 0x7B, 0x7B }, ChannelByteModel.OneBytePerChar)]
    [InlineData(new byte[] { 0x7B, 0xFF }, ChannelByteModel.OneBytePerChar)]
    [InlineData(new byte[] { 0x7B }, ChannelByteModel.Unknown)]
    [InlineData(new byte[0], ChannelByteModel.Unknown)]
    [InlineData(new byte[] { 0x22, 0x00 }, ChannelByteModel.Unknown)]
    [InlineData(new byte[] { 0x00, 0x7B }, ChannelByteModel.Unknown)]
    public void DetectFromBytes(byte[] first, ChannelByteModel expected)
    {
        Assert.Equal(expected, ChannelTextCodec.DetectFromBytes(first));
    }

    [Fact]
    public void DetectFromBytes_on_real_encodings()
    {
        Assert.Equal(ChannelByteModel.Utf16, ChannelTextCodec.DetectFromBytes(Encoding.Unicode.GetBytes(Line)));
        Assert.Equal(ChannelByteModel.OneBytePerChar, ChannelTextCodec.DetectFromBytes(Encoding.ASCII.GetBytes(Line)));
    }

    [Theory]
    [InlineData(0, ChannelByteModel.Utf16, 0)]
    [InlineData(1, ChannelByteModel.Utf16, 0)]
    [InlineData(2, ChannelByteModel.Utf16, 2)]
    [InlineData(7, ChannelByteModel.Utf16, 6)]
    [InlineData(7, ChannelByteModel.OneBytePerChar, 7)]
    [InlineData(0, ChannelByteModel.OneBytePerChar, 0)]
    public void UsableLength(int length, ChannelByteModel model, int expected)
    {
        Assert.Equal(expected, ChannelTextCodec.UsableLength(new byte[length], model));
    }

    [Fact]
    public void UsableLength_rejects_unknown_model()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ChannelTextCodec.UsableLength(new byte[2], ChannelByteModel.Unknown));
    }

    [Fact]
    public void BytesToText_one_byte_maps_each_byte_to_a_char()
    {
        Assert.Equal(Line, ChannelTextCodec.BytesToText(Encoding.ASCII.GetBytes(Line), ChannelByteModel.OneBytePerChar));
        Assert.Equal("\u00FF\u0080", ChannelTextCodec.BytesToText(new byte[] { 0xFF, 0x80 }, ChannelByteModel.OneBytePerChar));
    }

    [Fact]
    public void BytesToText_utf16_reads_little_endian_pairs()
    {
        Assert.Equal(Line, ChannelTextCodec.BytesToText(Encoding.Unicode.GetBytes(Line), ChannelByteModel.Utf16));
        Assert.Equal("\u0201", ChannelTextCodec.BytesToText(new byte[] { 0x01, 0x02 }, ChannelByteModel.Utf16));
    }

    [Fact]
    public void BytesToText_utf16_rejects_odd_length()
    {
        Assert.Throws<ArgumentException>(() => ChannelTextCodec.BytesToText(new byte[] { 0x7B, 0x00, 0x22 }, ChannelByteModel.Utf16));
    }

    [Fact]
    public void BytesToText_rejects_unknown_model()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ChannelTextCodec.BytesToText(new byte[2], ChannelByteModel.Unknown));
    }

    [Fact]
    public void BytesToText_utf16_with_pending_odd_byte_reassembles()
    {
        var bytes = Encoding.Unicode.GetBytes(Line);
        var text = new StringBuilder();
        var pending = new List<byte>();
        foreach (var chunk in bytes.Chunk(5))
        {
            pending.AddRange(chunk);
            var all = pending.ToArray();
            var usable = ChannelTextCodec.UsableLength(all, ChannelByteModel.Utf16);
            text.Append(ChannelTextCodec.BytesToText(all.AsSpan(0, usable), ChannelByteModel.Utf16));
            pending = [.. all[usable..]];
        }

        Assert.Equal(Line, text.ToString());
        Assert.Empty(pending);
    }

    [Theory]
    [InlineData("{\"type\":1}", ChannelByteModel.OneBytePerChar)]
    [InlineData("{", ChannelByteModel.OneBytePerChar)]
    [InlineData("\u227B", ChannelByteModel.Utf16)]
    [InlineData("x", ChannelByteModel.Utf16)]
    [InlineData("", ChannelByteModel.Unknown)]
    public void DetectFromControlString(string s, ChannelByteModel expected)
    {
        Assert.Equal(expected, ChannelTextCodec.DetectFromControlString(s));
    }

    [Fact]
    public void DetectFromControlString_rejects_null_as_unknown()
    {
        Assert.Equal(ChannelByteModel.Unknown, ChannelTextCodec.DetectFromControlString(null!));
    }

    [Fact]
    public void ControlStringToText_one_byte_is_identity()
    {
        Assert.Equal(Line, ChannelTextCodec.ControlStringToText(Line, ChannelByteModel.OneBytePerChar));
        Assert.Equal("\u00FF", ChannelTextCodec.ControlStringToText("\u00FF", ChannelByteModel.OneBytePerChar));
    }

    [Fact]
    public void ControlStringToText_one_byte_rejects_wide_chars()
    {
        Assert.Throws<ArgumentException>(() => ChannelTextCodec.ControlStringToText("{\u0100}", ChannelByteModel.OneBytePerChar));
    }

    [Fact]
    public void ControlStringToText_utf16_unpacks_two_bytes_per_char()
    {
        // The agent wrote padded ASCII bytes; the control paired them into characters.
        var bytes = LineFraming.EncodeBytes(HelloMessage.FromAgent("0.1.0", true), ChannelByteModel.OneBytePerChar);
        var controlString = Encoding.Unicode.GetString(bytes);
        Assert.Equal(ChannelByteModel.Utf16, ChannelTextCodec.DetectFromControlString(controlString));

        var text = ChannelTextCodec.ControlStringToText(controlString, ChannelByteModel.Utf16);

        Assert.Equal(Encoding.ASCII.GetString(bytes), text);
        Assert.Equal("{\u0022", ChannelTextCodec.ControlStringToText("\u227B", ChannelByteModel.Utf16));
    }

    [Fact]
    public void ControlStringToText_rejects_unknown_model()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ChannelTextCodec.ControlStringToText("{", ChannelByteModel.Unknown));
    }

    [Fact]
    public void TextToControlString_one_byte_is_identity()
    {
        Assert.Equal(Line, ChannelTextCodec.TextToControlString(Line, ChannelByteModel.OneBytePerChar));
    }

    [Fact]
    public void TextToControlString_utf16_packs_even_line()
    {
        var even = "{}\n\n";

        var packed = ChannelTextCodec.TextToControlString(even, ChannelByteModel.Utf16);

        Assert.Equal("\u7D7B\u0A0A", packed);
    }

    [Fact]
    public void TextToControlString_utf16_pads_odd_line_before_newline()
    {
        var packed = ChannelTextCodec.TextToControlString("{}\n", ChannelByteModel.Utf16);

        Assert.Equal("\u7D7B\u0A20", packed);
    }

    [Fact]
    public void TextToControlString_utf16_pads_odd_text_without_newline_at_the_end()
    {
        Assert.Equal("\u207B", ChannelTextCodec.TextToControlString("{", ChannelByteModel.Utf16));
    }

    [Fact]
    public void TextToControlString_utf16_bytes_are_the_ascii_line()
    {
        var line = LineFraming.EncodeLine(new DropMessage("abc", 1, 2, [new DropItem(@"C:\é.txt", false)]));

        var packed = ChannelTextCodec.TextToControlString(line, ChannelByteModel.Utf16);
        var bytes = Encoding.Unicode.GetBytes(packed);

        // What the agent receives when the control sends the raw string bytes.
        Assert.Equal(ChannelByteModel.OneBytePerChar, ChannelTextCodec.DetectFromBytes(bytes));
        var text = ChannelTextCodec.BytesToText(bytes, ChannelByteModel.OneBytePerChar);
        var expected = line.Length % 2 == 0 ? line : line[..^1] + " \n";
        Assert.Equal(expected, text);
    }

    [Fact]
    public void TextToControlString_rejects_non_ascii()
    {
        Assert.Throws<ArgumentException>(() => ChannelTextCodec.TextToControlString("é\n", ChannelByteModel.OneBytePerChar));
        Assert.Throws<ArgumentException>(() => ChannelTextCodec.TextToControlString("é\n", ChannelByteModel.Utf16));
    }

    [Fact]
    public void TextToControlString_rejects_unknown_model()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ChannelTextCodec.TextToControlString("{}\n", ChannelByteModel.Unknown));
    }

    [Fact]
    public void Full_round_trip_through_both_models()
    {
        var message = new DropMessage("id", 5, 6, [new DropItem(@"C:\Users\Zoë\😀.txt", true)]);

        // Agent receives one byte per char, client sees UTF-16 strings.
        var agentBytes = LineFraming.EncodeBytes(message, ChannelByteModel.OneBytePerChar);
        var clientString = Encoding.Unicode.GetString(agentBytes);
        var clientText = ChannelTextCodec.ControlStringToText(clientString, ChannelTextCodec.DetectFromControlString(clientString));
        var reader = new LineReader();
        reader.Append(clientText);
        Assert.True(reader.TryReadLine(out var line));
        Assert.Equal(message, MessageSerializer.Deserialize(Encoding.ASCII.GetBytes(line)));

        // Agent receives UTF-16LE, client sees one byte per char strings.
        var utf16Bytes = LineFraming.EncodeBytes(message, ChannelByteModel.Utf16);
        Assert.Equal(ChannelByteModel.Utf16, ChannelTextCodec.DetectFromBytes(utf16Bytes));
        var agentText = ChannelTextCodec.BytesToText(utf16Bytes, ChannelByteModel.Utf16);
        reader.Append(agentText);
        Assert.True(reader.TryReadLine(out line));
        Assert.Equal(message, MessageSerializer.Deserialize(Encoding.ASCII.GetBytes(line)));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Line_sent_as_is_by_the_client_round_trips_with_either_control(bool rawControl)
    {
        // The client hands the ASCII line itself to the control. A raw control sends the
        // UTF-16LE bytes of the string and builds strings from raw bytes; the other kind
        // maps each character to one byte and back.
        var controlEncoding = rawControl ? Encoding.Unicode : Encoding.Latin1;
        Func<string, byte[]> send = controlEncoding.GetBytes;
        Func<byte[], string> deliver = controlEncoding.GetString;
        var drop = new DropMessage("id", 5, 6, [new DropItem(@"C:\Users\Zoë\a.txt", false)]);

        var agentBytes = send(LineFraming.EncodeLine(drop));
        var agentModel = ChannelTextCodec.DetectFromBytes(agentBytes);
        Assert.Equal(rawControl ? ChannelByteModel.Utf16 : ChannelByteModel.OneBytePerChar, agentModel);
        var agentText = ChannelTextCodec.BytesToText(agentBytes, agentModel);
        Assert.Equal(LineFraming.EncodeLine(drop), agentText);

        var reply = new DropResultMessage("id", DropStatus.Ok, @"C:\Users\me\Desktop", "Copied a.txt");
        var clientString = deliver(LineFraming.EncodeBytes(reply, agentModel));
        var clientModel = ChannelTextCodec.DetectFromControlString(clientString);
        Assert.Equal(ChannelByteModel.OneBytePerChar, clientModel);
        var reader = new LineReader();
        reader.Append(ChannelTextCodec.ControlStringToText(clientString, clientModel));
        Assert.True(reader.TryReadLine(out var line));
        Assert.Equal(reply, MessageSerializer.Deserialize(Encoding.ASCII.GetBytes(line)));
    }
}
