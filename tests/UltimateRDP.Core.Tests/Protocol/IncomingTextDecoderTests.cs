using System.Runtime.InteropServices;
using System.Text;
using UltimateRDP.Core.Protocol;
using UltimateRDP.Core.Transfer;

namespace UltimateRDP.Core.Tests.Protocol;

/// <summary>The reading path of the agent: bytes of the channel, decoder, line reader, messages.</summary>
public class IncomingTextDecoderTests
{
    public static TheoryData<ChannelByteModel, int> ModelsAndReads => new()
    {
        { ChannelByteModel.OneBytePerChar, int.MaxValue },
        { ChannelByteModel.Utf16, int.MaxValue },
        { ChannelByteModel.OneBytePerChar, 1600 },
        { ChannelByteModel.Utf16, 1600 },
        { ChannelByteModel.Utf16, 1599 },
    };

    /// <summary>
    /// What the client of 0.4.3 hands to the control in one call at a drop with transfer:
    /// the drop, transferBegin, fileBegin and the first chunk, one after the other.
    /// </summary>
    [Theory]
    [MemberData(nameof(ModelsAndReads))]
    public void Several_lines_in_one_call_come_out_as_messages_in_their_order(ChannelByteModel model, int readSize)
    {
        var data = new byte[TransferSender.DefaultChunkSize];
        new Random(3).NextBytes(data);
        var sender = new TransferSender("t1", [new TransferFile("a.bin", data.Length)]);
        var text = string.Concat(
            LineFraming.EncodeLine(new DropMessage("d1", 420, 310, 0, Deferred: true)),
            LineFraming.EncodeLine(sender.Begin()),
            LineFraming.EncodeLine(sender.BeginFile()),
            sender.EncodeChunk(data));
        var bytes = model == ChannelByteModel.OneBytePerChar
            ? Encoding.Latin1.GetBytes(text)
            : MemoryMarshal.AsBytes(text.AsSpan()).ToArray();

        var decoder = new IncomingTextDecoder();
        var reader = new LineReader();
        var messages = new List<Message>();
        for (var start = 0; start < bytes.Length; start += readSize)
        {
            var count = (int)Math.Min(readSize, bytes.Length - (long)start);
            reader.Append(decoder.Decode(bytes.AsSpan(start, count), out var rejected));
            Assert.False(rejected);
            while (reader.TryReadLine(out var line))
            {
                messages.Add(MessageSerializer.Deserialize(Encoding.ASCII.GetBytes(line))!);
            }
        }

        Assert.Equal(model, decoder.Model);
        Assert.Equal(
            [MessageTypes.Drop, MessageTypes.TransferBegin, MessageTypes.FileBegin, MessageTypes.FileChunk],
            messages.Select(message => message.Type));
        Assert.Equal(Convert.ToBase64String(data), Assert.IsType<FileChunkMessage>(messages[3]).Data);
        Assert.Equal(0, reader.BufferedLength);
    }
}
