using System.Buffers.Binary;
using System.Text;
using UltimateRDP.Core.Protocol;

namespace UltimateRDP.Core.Tests.Protocol;

public class ChannelPduFramingTests
{
    private const uint First = ChannelPduFraming.FlagFirst;
    private const uint Last = ChannelPduFraming.FlagLast;

    /// <summary>A message of the client: lines that start with "{", as the agent receives them.</summary>
    private static byte[] Message(int length, byte seed = 0)
    {
        var bytes = new byte[length];
        bytes[0] = (byte)'{';
        for (var i = 1; i < length; i++)
        {
            bytes[i] = (byte)('a' + ((i + seed) % 26));
        }

        return bytes;
    }

    private static byte[] Header(int length, uint flags)
    {
        var header = new byte[ChannelPduFraming.HeaderLength];
        BinaryPrimitives.WriteUInt32LittleEndian(header, (uint)length);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), flags);
        return header;
    }

    /// <summary>The chunks of a message as a sender cuts it, each with its header.</summary>
    private static List<byte[]> Chunks(byte[] message, int chunkLength = ChannelPduFraming.DefaultChunkLength, uint extraFlags = 0)
    {
        var chunks = new List<byte[]>();
        for (var offset = 0; offset < message.Length; offset += chunkLength)
        {
            var count = Math.Min(chunkLength, message.Length - offset);
            var flags = extraFlags | (offset == 0 ? First : 0) | (offset + count == message.Length ? Last : 0);
            chunks.Add([.. Header(message.Length, flags), .. message.AsSpan(offset, count)]);
        }

        return chunks;
    }

    private static byte[] Unwrap(ChannelPduFraming framing, byte[] block, out int headers)
    {
        var payload = new byte[block.Length];
        Assert.True(framing.TryUnwrap(block, payload, out var written, out headers), framing.Error);
        return payload[..written];
    }

    private static string Fails(ChannelPduFraming framing, byte[] block)
    {
        var payload = new byte[block.Length];
        Assert.False(framing.TryUnwrap(block, payload, out var written, out var headers));
        Assert.Equal(0, written);
        Assert.Equal(0, headers);
        Assert.NotNull(framing.Error);
        return framing.Error;
    }

    [Fact]
    public void Data_without_headers_passes_through()
    {
        var framing = new ChannelPduFraming();
        var first = Encoding.ASCII.GetBytes("{\"type\":\"hello\",\"version\":1}\n{\"ty");
        var second = Encoding.ASCII.GetBytes("pe\":\"dragLeave\"}\n");

        Assert.Equal(first, Unwrap(framing, first, out var headers));
        Assert.Equal(0, headers);
        Assert.Equal(ChannelPduLayout.Raw, framing.Layout);

        // Later blocks are not checked: they may start anywhere in a line.
        Assert.Equal(second, Unwrap(framing, second, out _));
        Assert.Equal(0, framing.Headers);
    }

    [Fact]
    public void Utf16_data_without_headers_passes_through()
    {
        var framing = new ChannelPduFraming();
        var block = Encoding.Unicode.GetBytes("{\"type\":\"hello\"}\n");

        Assert.Equal(block, Unwrap(framing, block, out _));
        Assert.Equal(ChannelPduLayout.Raw, framing.Layout);
    }

    [Fact]
    public void A_header_in_front_of_the_first_block_is_found_and_taken_out()
    {
        var framing = new ChannelPduFraming();
        var message = Message(120);
        var block = Chunks(message).Single();

        Assert.Equal(message, Unwrap(framing, block, out var headers));
        Assert.Equal(1, headers);
        Assert.Equal(ChannelPduLayout.Headers, framing.Layout);
        Assert.Equal(1, framing.Headers);
    }

    [Fact]
    public void A_message_of_several_chunks_is_put_back_together_one_read_per_chunk()
    {
        var framing = new ChannelPduFraming();
        var message = Message(5000, 3);
        var chunks = Chunks(message);
        Assert.Equal(4, chunks.Count);

        var output = new List<byte>();
        foreach (var chunk in chunks)
        {
            output.AddRange(Unwrap(framing, chunk, out var headers));
            Assert.Equal(1, headers);
        }

        Assert.Equal(message, output);
        Assert.Equal(4, framing.Headers);
    }

    [Fact]
    public void Several_chunks_in_one_read_are_split_at_the_chunk_length()
    {
        var framing = new ChannelPduFraming();
        var message = Message(5000, 4);
        var block = Chunks(message).SelectMany(chunk => chunk).ToArray();

        Assert.Equal(message, Unwrap(framing, block, out var headers));
        Assert.Equal(4, headers);
    }

    [Fact]
    public void Two_chunks_in_one_read_then_the_rest_put_back_together()
    {
        var framing = new ChannelPduFraming();
        var message = Message(5000, 5);
        var chunks = Chunks(message);

        var output = new List<byte>();
        output.AddRange(Unwrap(framing, [.. chunks[0], .. chunks[1]], out var headers));
        Assert.Equal(2, headers);
        output.AddRange(Unwrap(framing, [.. chunks[2], .. chunks[3]], out headers));
        Assert.Equal(2, headers);

        Assert.Equal(message, output);
    }

    [Fact]
    public void A_last_chunk_may_be_followed_by_the_next_message()
    {
        var framing = new ChannelPduFraming();
        var first = Message(100, 1);
        var second = Message(2000, 2);
        var secondChunks = Chunks(second);

        var output = Unwrap(framing, [.. Chunks(first).Single(), .. secondChunks[0]], out var headers);
        Assert.Equal(2, headers);
        output = [.. output, .. Unwrap(framing, secondChunks[1], out _)];

        Assert.Equal([.. first, .. second], output);
    }

    [Fact]
    public void Chunks_larger_than_the_usual_length_work_one_read_per_chunk()
    {
        var framing = new ChannelPduFraming();
        var message = Message(40_000, 6);

        var output = new List<byte>();
        foreach (var chunk in Chunks(message, chunkLength: 16_256))
        {
            output.AddRange(Unwrap(framing, chunk, out _));
        }

        Assert.Equal(message, output);
    }

    [Fact]
    public void The_show_protocol_flag_is_accepted()
    {
        var framing = new ChannelPduFraming();
        var message = Message(3000, 7);

        var output = new List<byte>();
        foreach (var chunk in Chunks(message, extraFlags: ChannelPduFraming.FlagShowProtocol))
        {
            output.AddRange(Unwrap(framing, chunk, out _));
        }

        Assert.Equal(message, output);
    }

    [Fact]
    public void A_first_block_that_is_neither_data_nor_a_header_fails()
    {
        var framing = new ChannelPduFraming();

        Assert.Contains("neither", Fails(framing, [0x00, 0x7B, 0x22, 0x74]));
        Assert.Equal(ChannelPduLayout.Unknown, framing.Layout);
    }

    [Fact]
    public void A_header_whose_data_does_not_start_a_line_is_not_taken_for_a_header()
    {
        var framing = new ChannelPduFraming();

        Fails(framing, [.. Header(5, First | Last), .. "hello"u8]);
        Assert.Equal(ChannelPduLayout.Unknown, framing.Layout);
    }

    [Fact]
    public void Unhandled_flags_fail()
    {
        var framing = new ChannelPduFraming();
        var message = Message(3000);
        var chunks = Chunks(message);
        Unwrap(framing, chunks[0], out _);

        // CHANNEL_FLAG_SUSPEND, then a compressed chunk.
        Assert.Contains("flags", Fails(framing, [.. Header(3000, 0x20), .. message.AsSpan(1600, 1400)]));
        Assert.Contains("flags", Fails(framing, [.. Header(3000, Last | 0x00200000), .. message.AsSpan(1600, 1400)]));

        // The state did not change: the real last chunk still completes the message.
        Assert.Equal(message.AsSpan(1600).ToArray(), Unwrap(framing, chunks[1], out _));
    }

    [Fact]
    public void A_length_that_changes_inside_a_message_fails()
    {
        var framing = new ChannelPduFraming();
        var message = Message(3000);
        Unwrap(framing, Chunks(message)[0], out _);

        Assert.Contains("inside a message of 3000 bytes", Fails(framing, [.. Header(2999, Last), .. message.AsSpan(1600, 1400)]));
    }

    [Fact]
    public void A_first_chunk_inside_a_message_fails()
    {
        var framing = new ChannelPduFraming();
        Unwrap(framing, Chunks(Message(3000))[0], out _);

        Assert.Contains("previous message", Fails(framing, Chunks(Message(100)).Single()));
    }

    [Fact]
    public void A_chunk_without_the_first_flag_outside_a_message_fails()
    {
        var framing = new ChannelPduFraming();
        Unwrap(framing, Chunks(Message(100)).Single(), out _);

        Assert.Contains("outside a message", Fails(framing, [.. Header(100, Last), .. Message(100)]));
    }

    [Fact]
    public void A_last_chunk_too_short_fails()
    {
        var framing = new ChannelPduFraming();
        var message = Message(3000);
        Unwrap(framing, Chunks(message)[0], out _);

        Assert.Contains("1400 were missing", Fails(framing, [.. Header(3000, Last), .. message.AsSpan(1600, 1000)]));
    }

    [Fact]
    public void A_chunk_without_the_last_flag_that_completes_its_message_fails()
    {
        var framing = new ChannelPduFraming();
        var message = Message(3000);
        Unwrap(framing, Chunks(message)[0], out _);

        Assert.Contains("without the last flag", Fails(framing, [.. Header(3000, 0), .. message.AsSpan(1600, 1400)]));
    }

    [Fact]
    public void A_block_that_ends_inside_a_header_fails_and_writes_nothing()
    {
        var framing = new ChannelPduFraming();
        var first = Chunks(Message(100)).Single();

        Assert.Contains("inside a channel header", Fails(framing, [.. first, .. Header(200, First).AsSpan(0, 5)]));

        // Nothing of the block was taken: the same chunk alone still reads.
        Assert.Equal(Message(100), Unwrap(framing, first, out _));
    }

    [Fact]
    public void An_empty_block_writes_nothing()
    {
        var framing = new ChannelPduFraming();

        Assert.True(framing.TryUnwrap([], new byte[1], out var written, out var headers));
        Assert.Equal((0, 0), (written, headers));
        Assert.Equal(ChannelPduLayout.Unknown, framing.Layout);
    }

    [Fact]
    public void Lines_come_out_of_the_line_reader_through_the_headers()
    {
        var framing = new ChannelPduFraming();
        var lines = string.Concat(Enumerable.Range(0, 40).Select(i => $"{{\"type\":\"dragMove\",\"id\":\"d{i}\",\"x\":{i},\"y\":{i}}}\n"));
        var message = Encoding.ASCII.GetBytes(lines);
        var decoder = new IncomingTextDecoder();
        var reader = new LineReader();
        foreach (var chunk in Chunks(message))
        {
            var data = Unwrap(framing, chunk, out _);
            reader.Append(decoder.Decode(data, out var rejected));
            Assert.False(rejected);
        }

        var count = 0;
        while (reader.TryReadLine(out var line))
        {
            Assert.Equal($"{{\"type\":\"dragMove\",\"id\":\"d{count}\",\"x\":{count},\"y\":{count}}}", line);
            count++;
        }

        Assert.Equal(40, count);
    }

    [Fact]
    public void The_payload_buffer_must_hold_the_block()
    {
        var framing = new ChannelPduFraming();

        Assert.Throws<ArgumentException>(() => framing.TryUnwrap(new byte[10], new byte[9], out _, out _));
    }
}
