using RdpDrop.Core.Protocol;

namespace RdpDrop.Core.Tests.Protocol;

public class LineReaderTests
{
    private static List<string> ReadAll(LineReader reader)
    {
        var lines = new List<string>();
        while (reader.TryReadLine(out var line))
        {
            lines.Add(line);
        }

        return lines;
    }

    [Fact]
    public void Empty_reader_has_no_line()
    {
        var reader = new LineReader();

        Assert.False(reader.TryReadLine(out var line));
        Assert.Equal(string.Empty, line);
        Assert.Equal(0, reader.BufferedLength);
    }

    [Fact]
    public void Reads_one_line()
    {
        var reader = new LineReader();
        reader.Append("{\"a\":1}\n");

        Assert.Equal(["{\"a\":1}"], ReadAll(reader));
        Assert.Equal(0, reader.BufferedLength);
    }

    [Fact]
    public void Waits_for_the_newline()
    {
        var reader = new LineReader();
        reader.Append("{\"a\":");

        Assert.False(reader.TryReadLine(out _));
        Assert.Equal(5, reader.BufferedLength);

        reader.Append("1}\n");
        Assert.Equal(["{\"a\":1}"], ReadAll(reader));
    }

    [Fact]
    public void Reads_a_line_split_into_single_characters()
    {
        var reader = new LineReader();
        var lines = new List<string>();
        foreach (var c in "{\"x\":true}\n{\"y\":2}\n")
        {
            reader.Append([c]);
            lines.AddRange(ReadAll(reader));
        }

        Assert.Equal(["{\"x\":true}", "{\"y\":2}"], lines);
    }

    [Fact]
    public void Reads_merged_lines_and_keeps_the_partial_one()
    {
        var reader = new LineReader();
        reader.Append("one\ntwo\nthr");

        Assert.Equal(["one", "two"], ReadAll(reader));
        Assert.Equal(3, reader.BufferedLength);

        reader.Append("ee\n");
        Assert.Equal(["three"], ReadAll(reader));
    }

    [Fact]
    public void Strips_carriage_return_and_trailing_spaces()
    {
        var reader = new LineReader();
        reader.Append("a\r\nb \nc  \r\n d\n");

        Assert.Equal(["a", "b", "c", " d"], ReadAll(reader));
    }

    [Fact]
    public void Skips_blank_lines()
    {
        var reader = new LineReader();
        reader.Append("\n\r\n   \n \r\nx\n\n");

        Assert.Equal(["x"], ReadAll(reader));
        Assert.Equal(0, reader.BufferedLength);
    }

    [Fact]
    public void Reset_drops_buffered_text()
    {
        var reader = new LineReader();
        reader.Append("partial");
        reader.Reset();
        reader.Append("x\n");

        Assert.Equal(["x"], ReadAll(reader));
    }

    [Fact]
    public void Grows_for_long_lines()
    {
        var reader = new LineReader();
        var payload = new string('z', 100_000);
        reader.Append(payload);
        reader.Append("\n");

        Assert.Equal([payload], ReadAll(reader));
    }

    [Fact]
    public void Line_of_maximum_length_is_accepted()
    {
        var reader = new LineReader();
        var errors = 0;
        reader.ProtocolError += (_, _) => errors++;
        var payload = new string('m', LineFraming.MaxLineLength);

        reader.Append(payload);
        Assert.False(reader.TryReadLine(out _));
        reader.Append("\n");

        Assert.Equal([payload], ReadAll(reader));
        Assert.Equal(0, errors);
    }

    [Fact]
    public void Partial_line_past_the_limit_drops_the_buffer()
    {
        var reader = new LineReader();
        object? sender = null;
        reader.ProtocolError += (s, _) => sender = s;

        reader.Append(new string('o', LineFraming.MaxLineLength + 1));

        Assert.False(reader.TryReadLine(out _));
        Assert.Same(reader, sender);
        Assert.Equal(1, reader.ProtocolErrorCount);
        Assert.Equal(0, reader.BufferedLength);
    }

    [Fact]
    public void Complete_line_past_the_limit_is_an_error_and_drops_the_buffer()
    {
        var reader = new LineReader();
        reader.Append("ok\n");
        reader.Append(new string('o', LineFraming.MaxLineLength + 1));
        reader.Append("\nnext\n");

        Assert.True(reader.TryReadLine(out var first));
        Assert.Equal("ok", first);
        Assert.False(reader.TryReadLine(out _));
        Assert.Equal(1, reader.ProtocolErrorCount);
        Assert.Equal(0, reader.BufferedLength);
    }

    [Fact]
    public void Recovers_after_a_protocol_error()
    {
        var reader = new LineReader();
        reader.Append(new string('o', LineFraming.MaxLineLength + 10));
        Assert.False(reader.TryReadLine(out _));

        reader.Append("{\"type\":\"hello\"}\n");

        Assert.Equal(["{\"type\":\"hello\"}"], ReadAll(reader));
        Assert.Equal(1, reader.ProtocolErrorCount);
    }

    [Fact]
    public void Reads_lines_produced_by_line_framing()
    {
        var reader = new LineReader();
        var hello = HelloMessage.FromClient("0.1.0");
        var drop = new DropMessage("d", 1, 2, [new DropItem(@"C:\é.txt", false)]);
        reader.Append(LineFraming.EncodeLine(hello) + LineFraming.EncodeLine(drop));

        var lines = ReadAll(reader);

        Assert.Equal(2, lines.Count);
        Assert.Equal(hello, MessageSerializer.Deserialize(System.Text.Encoding.ASCII.GetBytes(lines[0])));
        Assert.Equal(drop, MessageSerializer.Deserialize(System.Text.Encoding.ASCII.GetBytes(lines[1])));
    }
}
