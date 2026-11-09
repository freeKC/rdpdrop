using System.Text;
using UltimateRDP.Core.Protocol;

namespace UltimateRDP.Core.Tests.Protocol;

public class LineFramingTests
{
    private static readonly DragEnterMessage AccentedEnter = new(
        "id-1",
        10,
        20,
        [new DropItem(@"C:\Users\élodie\Données\photo 😀.jpg", false)]);

    private static bool IsAscii(string text) => text.All(c => c <= 0x7F);

    [Fact]
    public void Max_line_length_is_one_mebibyte()
    {
        Assert.Equal(1_048_576, LineFraming.MaxLineLength);
    }

    [Fact]
    public void EncodeLine_is_json_followed_by_one_newline()
    {
        var line = LineFraming.EncodeLine(HelloMessage.FromClient("0.2.1"));

        Assert.EndsWith("\n", line);
        Assert.Equal(1, line.Count(c => c == '\n'));
        Assert.DoesNotContain('\r', line);
        Assert.StartsWith("{\"type\":\"hello\"", line);
    }

    [Fact]
    public void EncodeLine_matches_serializer_output()
    {
        var message = new DropResultMessage("x", DropStatus.Cancelled, "Stopped", DropEffect.None);

        var line = LineFraming.EncodeLine(message);

        Assert.Equal(Encoding.UTF8.GetString(MessageSerializer.Serialize(message)) + "\n", line);
    }

    [Fact]
    public void EncodeLine_escapes_accents_and_emoji_as_ascii()
    {
        var line = LineFraming.EncodeLine(AccentedEnter);

        Assert.True(IsAscii(line));
        Assert.Contains(@"\u00E9", line, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(@"\uD83D\uDE00", line, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EncodeLine_round_trips_accents_and_emoji()
    {
        var line = LineFraming.EncodeLine(AccentedEnter);

        var parsed = MessageSerializer.Deserialize(Encoding.ASCII.GetBytes(line.TrimEnd('\n')));

        Assert.Equal(AccentedEnter, parsed);
    }

    [Fact]
    public void EncodeLine_escapes_line_breaks_in_strings()
    {
        var line = LineFraming.EncodeLine(new DropResultMessage("x", DropStatus.Error, Message: "first\nsecond\r\nthird"));

        Assert.Equal(1, line.Count(c => c == '\n'));
        Assert.DoesNotContain('\r', line);
    }

    [Fact]
    public void EncodeLine_rejects_null()
    {
        Assert.Throws<ArgumentNullException>(() => LineFraming.EncodeLine(null!));
    }

    [Fact]
    public void EncodeLine_rejects_oversize_message()
    {
        var message = new DropResultMessage("x", DropStatus.Error, Message: new string('a', LineFraming.MaxLineLength));

        Assert.Throws<ArgumentException>(() => LineFraming.EncodeLine(message));
    }

    [Fact]
    public void EncodeBytes_utf16_is_little_endian_two_bytes_per_char()
    {
        var message = HelloMessage.FromAgent("0.2.1", true);
        var line = LineFraming.EncodeLine(message);

        var bytes = LineFraming.EncodeBytes(message, ChannelByteModel.Utf16);

        Assert.Equal(line.Length * 2, bytes.Length);
        Assert.Equal((byte)'{', bytes[0]);
        Assert.Equal(0, bytes[1]);
        Assert.Equal((byte)'\n', bytes[^2]);
        Assert.Equal(0, bytes[^1]);
        Assert.Equal(line, Encoding.Unicode.GetString(bytes));
    }

    [Theory]
    [InlineData("a")]
    [InlineData("ab")]
    [InlineData("abc")]
    [InlineData("abcd")]
    public void EncodeBytes_one_byte_is_even_and_padded_before_newline(string id)
    {
        var message = new DropResultMessage(id, DropStatus.Ok);
        var line = LineFraming.EncodeLine(message);

        var bytes = LineFraming.EncodeBytes(message, ChannelByteModel.OneBytePerChar);

        Assert.Equal(0, bytes.Length % 2);
        Assert.Equal((byte)'\n', bytes[^1]);
        var text = Encoding.ASCII.GetString(bytes);
        if (line.Length % 2 == 0)
        {
            Assert.Equal(line, text);
        }
        else
        {
            Assert.Equal(line[..^1] + " \n", text);
        }
    }

    [Fact]
    public void EncodeBytes_one_byte_keeps_accents_escaped()
    {
        var bytes = LineFraming.EncodeBytes(AccentedEnter, ChannelByteModel.OneBytePerChar);

        Assert.All(bytes, b => Assert.True(b <= 0x7F));
    }

    [Theory]
    [InlineData(ChannelByteModel.Unknown)]
    [InlineData((ChannelByteModel)42)]
    public void EncodeBytes_rejects_unknown_model(ChannelByteModel model)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => LineFraming.EncodeBytes(HelloMessage.FromClient("1"), model));
    }
}
