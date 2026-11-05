using RdpDrop.Core.Protocol;

namespace RdpDrop.Core.Tests.Protocol;

public class FramingTests
{
    [Fact]
    public void Frame_prefixes_little_endian_length()
    {
        var frame = Framing.Frame([1, 2, 3]);

        Assert.Equal([3, 0, 0, 0, 1, 2, 3], frame);
    }

    [Fact]
    public void Frame_writes_multi_byte_length()
    {
        var frame = Framing.Frame(new byte[0x0102]);

        Assert.Equal(0x02, frame[0]);
        Assert.Equal(0x01, frame[1]);
        Assert.Equal(0, frame[2]);
        Assert.Equal(0, frame[3]);
        Assert.Equal(4 + 0x0102, frame.Length);
    }

    [Fact]
    public void Frame_accepts_empty_payload()
    {
        Assert.Equal([0, 0, 0, 0], Framing.Frame([]));
    }

    [Fact]
    public void Frame_accepts_maximum_size()
    {
        var frame = Framing.Frame(new byte[Framing.MaxFrameSize]);

        Assert.Equal(Framing.HeaderSize + Framing.MaxFrameSize, frame.Length);
    }

    [Fact]
    public void Frame_rejects_oversize_payload()
    {
        Assert.Throws<ArgumentException>(() => Framing.Frame(new byte[Framing.MaxFrameSize + 1]));
    }

    [Fact]
    public void Frame_rejects_null()
    {
        Assert.Throws<ArgumentNullException>(() => Framing.Frame((byte[])null!));
    }

    [Fact]
    public void Max_frame_size_is_one_mebibyte()
    {
        Assert.Equal(1_048_576, Framing.MaxFrameSize);
    }

    [Fact]
    public void Frame_message_serializes_then_frames()
    {
        var message = HelloMessage.FromClient("0.1.0");
        var payload = MessageSerializer.Serialize(message);

        Assert.Equal(Framing.Frame(payload), Framing.Frame(message));
    }
}
