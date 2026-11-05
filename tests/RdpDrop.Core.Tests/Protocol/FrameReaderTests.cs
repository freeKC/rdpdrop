using RdpDrop.Core.Protocol;

namespace RdpDrop.Core.Tests.Protocol;

public class FrameReaderTests
{
    private static List<byte[]> ReadAll(FrameReader reader)
    {
        var frames = new List<byte[]>();
        while (reader.TryReadFrame(out var payload))
        {
            frames.Add(payload);
        }

        return frames;
    }

    private static byte[] Header(uint length) => BitConverter.GetBytes(length);

    [Fact]
    public void Empty_reader_has_no_frame()
    {
        var reader = new FrameReader();

        Assert.False(reader.TryReadFrame(out var payload));
        Assert.Empty(payload);
        Assert.Equal(0, reader.BufferedByteCount);
    }

    [Fact]
    public void Single_frame_round_trips()
    {
        var reader = new FrameReader();

        reader.Append(Framing.Frame([10, 20, 30]));

        Assert.True(reader.TryReadFrame(out var payload));
        Assert.Equal([10, 20, 30], payload);
        Assert.False(reader.TryReadFrame(out _));
        Assert.Equal(0, reader.BufferedByteCount);
    }

    [Fact]
    public void Message_round_trips_through_frame_and_reader()
    {
        var drop = new DropMessage("id", 412, 300, [new DropItem(@"C:\Users\me\a.txt", false)]);
        var reader = new FrameReader();

        reader.Append(Framing.Frame(drop));

        Assert.True(reader.TryReadFrame(out var payload));
        Assert.Equal(drop, MessageSerializer.Deserialize(payload));
    }

    [Fact]
    public void Empty_payload_frame_is_returned()
    {
        var reader = new FrameReader();

        reader.Append(Framing.Frame([]));

        Assert.True(reader.TryReadFrame(out var payload));
        Assert.Empty(payload);
    }

    [Fact]
    public void Frame_split_byte_by_byte_is_returned_once_complete()
    {
        var frame = Framing.Frame([1, 2, 3, 4, 5]);
        var reader = new FrameReader();

        for (var i = 0; i < frame.Length - 1; i++)
        {
            reader.Append(frame.AsSpan(i, 1));
            Assert.False(reader.TryReadFrame(out _));
        }

        reader.Append(frame.AsSpan(frame.Length - 1, 1));
        Assert.True(reader.TryReadFrame(out var payload));
        Assert.Equal([1, 2, 3, 4, 5], payload);
    }

    [Fact]
    public void Header_split_across_chunks_is_handled()
    {
        var frame = Framing.Frame([7, 8]);
        var reader = new FrameReader();

        reader.Append(frame.AsSpan(0, 2));
        Assert.False(reader.TryReadFrame(out _));
        reader.Append(frame.AsSpan(2));

        Assert.True(reader.TryReadFrame(out var payload));
        Assert.Equal([7, 8], payload);
    }

    [Fact]
    public void Merged_frames_in_one_chunk_are_all_returned()
    {
        byte[] chunk = [.. Framing.Frame([1]), .. Framing.Frame([]), .. Framing.Frame([2, 3])];
        var reader = new FrameReader();

        reader.Append(chunk);

        var frames = ReadAll(reader);
        Assert.Equal(3, frames.Count);
        Assert.Equal([1], frames[0]);
        Assert.Empty(frames[1]);
        Assert.Equal([2, 3], frames[2]);
    }

    [Fact]
    public void Chunk_ending_inside_second_frame_keeps_remainder()
    {
        var first = Framing.Frame([1, 2]);
        var second = Framing.Frame([3, 4, 5]);
        var reader = new FrameReader();

        reader.Append([.. first, .. second.AsSpan(0, 5)]);
        Assert.True(reader.TryReadFrame(out var payload));
        Assert.Equal([1, 2], payload);
        Assert.False(reader.TryReadFrame(out _));
        Assert.Equal(5, reader.BufferedByteCount);

        reader.Append(second.AsSpan(5));
        Assert.True(reader.TryReadFrame(out payload));
        Assert.Equal([3, 4, 5], payload);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(7)]
    [InlineData(1600)]
    [InlineData(100_000)]
    public void Many_frames_in_arbitrary_chunks_round_trip(int chunkSize)
    {
        var random = new Random(chunkSize);
        var payloads = Enumerable.Range(0, 50)
            .Select(i =>
            {
                var payload = new byte[random.Next(0, i == 0 ? 20_000 : 3_000)];
                random.NextBytes(payload);
                return payload;
            })
            .ToList();
        var stream = payloads.SelectMany(Framing.Frame).ToArray();
        var reader = new FrameReader();
        var received = new List<byte[]>();

        for (var offset = 0; offset < stream.Length; offset += chunkSize)
        {
            reader.Append(stream.AsSpan(offset, Math.Min(chunkSize, stream.Length - offset)));
            received.AddRange(ReadAll(reader));
        }

        Assert.Equal(payloads.Count, received.Count);
        for (var i = 0; i < payloads.Count; i++)
        {
            Assert.Equal(payloads[i], received[i]);
        }

        Assert.Equal(0, reader.BufferedByteCount);
        Assert.Equal(0, reader.ProtocolErrorCount);
    }

    [Fact]
    public void Maximum_size_frame_is_accepted()
    {
        var payload = new byte[Framing.MaxFrameSize];
        payload[^1] = 42;
        var reader = new FrameReader();

        reader.Append(Framing.Frame(payload));

        Assert.True(reader.TryReadFrame(out var result));
        Assert.Equal(Framing.MaxFrameSize, result.Length);
        Assert.Equal(42, result[^1]);
        Assert.Equal(0, reader.ProtocolErrorCount);
    }

    [Theory]
    [InlineData(1_048_577u)]
    [InlineData(0x7FFF_FFFFu)]
    [InlineData(0x8000_0000u)]
    [InlineData(uint.MaxValue)]
    public void Oversize_length_resets_reader_and_raises_error(uint length)
    {
        var reader = new FrameReader();
        FrameProtocolErrorEventArgs? raised = null;
        reader.ProtocolError += (sender, args) =>
        {
            Assert.Same(reader, sender);
            raised = args;
        };

        reader.Append([.. Header(length), 1, 2, 3, 4, 5]);

        Assert.False(reader.TryReadFrame(out var payload));
        Assert.Empty(payload);
        Assert.NotNull(raised);
        Assert.Equal(length, raised.DeclaredLength);
        Assert.Equal(1, reader.ProtocolErrorCount);
        Assert.Equal(0, reader.BufferedByteCount);
    }

    [Fact]
    public void Reader_works_again_after_protocol_error()
    {
        var reader = new FrameReader();
        reader.Append(Header((uint)Framing.MaxFrameSize + 1));
        Assert.False(reader.TryReadFrame(out _));

        reader.Append(Framing.Frame([9, 9]));

        Assert.True(reader.TryReadFrame(out var payload));
        Assert.Equal([9, 9], payload);
        Assert.Equal(1, reader.ProtocolErrorCount);
    }

    [Fact]
    public void Frames_before_oversize_header_are_returned_and_rest_is_dropped()
    {
        var reader = new FrameReader();
        var errors = 0;
        reader.ProtocolError += (_, _) => errors++;

        reader.Append([.. Framing.Frame([1]), .. Header(uint.MaxValue), .. Framing.Frame([2])]);

        Assert.True(reader.TryReadFrame(out var payload));
        Assert.Equal([1], payload);
        Assert.False(reader.TryReadFrame(out _));
        Assert.Equal(1, errors);
        Assert.False(reader.TryReadFrame(out _));
        Assert.Equal(1, errors);
        Assert.Equal(0, reader.BufferedByteCount);
    }

    [Fact]
    public void Protocol_error_count_accumulates()
    {
        var reader = new FrameReader();

        for (var i = 0; i < 3; i++)
        {
            reader.Append(Header(uint.MaxValue));
            Assert.False(reader.TryReadFrame(out _));
        }

        Assert.Equal(3, reader.ProtocolErrorCount);
    }

    [Fact]
    public void Reset_drops_buffered_bytes()
    {
        var reader = new FrameReader();
        reader.Append(Framing.Frame([1, 2, 3]).AsSpan(0, 5));
        Assert.Equal(5, reader.BufferedByteCount);

        reader.Reset();

        Assert.Equal(0, reader.BufferedByteCount);
        reader.Append(Framing.Frame([4]));
        Assert.True(reader.TryReadFrame(out var payload));
        Assert.Equal([4], payload);
        Assert.Equal(0, reader.ProtocolErrorCount);
    }

    [Fact]
    public void Append_empty_chunk_does_nothing()
    {
        var reader = new FrameReader();

        reader.Append([]);

        Assert.Equal(0, reader.BufferedByteCount);
        Assert.False(reader.TryReadFrame(out _));
    }

    [Fact]
    public void Returned_payload_is_a_copy()
    {
        var reader = new FrameReader();
        reader.Append(Framing.Frame([1, 2]));
        Assert.True(reader.TryReadFrame(out var first));
        first[0] = 99;

        reader.Append(Framing.Frame([3, 4]));

        Assert.True(reader.TryReadFrame(out var second));
        Assert.Equal([3, 4], second);
    }
}
