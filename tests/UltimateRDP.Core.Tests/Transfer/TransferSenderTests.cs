using System.Text;
using UltimateRDP.Core.Protocol;
using UltimateRDP.Core.Transfer;

namespace UltimateRDP.Core.Tests.Transfer;

public class TransferSenderTests
{
    private static TransferSender TwoFiles() => new("t1", [new TransferFile("a.bin", 3 * TransferSender.ChunkSize), new TransferFile("b.bin", 10)]);

    private static FileChunkMessage Read(string line) =>
        Assert.IsType<FileChunkMessage>(MessageSerializer.Deserialize(Encoding.ASCII.GetBytes(line.TrimEnd('\n'))));

    [Fact]
    public void The_messages_follow_the_files()
    {
        using var sender = TwoFiles();

        Assert.Equal(new TransferBeginMessage("t1", 2, (3 * TransferSender.ChunkSize) + 10), sender.Begin());
        Assert.Equal(new FileBeginMessage("t1", 0, "a.bin", 3 * TransferSender.ChunkSize), sender.BeginFile());
        var first = Read(sender.EncodeChunk(new byte[TransferSender.ChunkSize]));
        var second = Read(sender.EncodeChunk(new byte[TransferSender.ChunkSize]));
        Assert.Equal((0, 0L), (first.FileIndex, first.Offset));
        Assert.Equal((0, (long)TransferSender.ChunkSize), (second.FileIndex, second.Offset));
    }

    [Fact]
    public void The_window_closes_at_four_mebibytes_and_reopens_with_acks()
    {
        using var sender = new TransferSender("t1", [new TransferFile("a.bin", 32L * 1024 * 1024)]);
        sender.Begin();
        sender.BeginFile();
        var chunk = new byte[TransferSender.ChunkSize];
        var sent = 0;
        while (sender.CanSend(chunk.Length))
        {
            sender.EncodeChunk(chunk);
            sent++;
        }

        Assert.Equal(16, sent);
        Assert.Equal(TransferSender.WindowBytes, sender.InFlightBytes);

        Assert.True(sender.OnAck(new FileAckMessage("t1", 0, 2L * 1024 * 1024)));

        Assert.Equal(2L * 1024 * 1024, sender.InFlightBytes);
        Assert.True(sender.CanSend(chunk.Length));
    }

    [Fact]
    public void Any_chunk_fits_when_nothing_is_in_flight()
    {
        using var sender = new TransferSender("t1", [new TransferFile("a.bin", 1)]);

        Assert.True(sender.CanSend(int.MaxValue));
    }

    [Fact]
    public void Acks_count_the_bytes_of_the_files_before()
    {
        using var sender = TwoFiles();
        sender.Begin();
        sender.BeginFile();
        for (var i = 0; i < 3; i++)
        {
            sender.EncodeChunk(new byte[TransferSender.ChunkSize]);
        }

        sender.EndFile();
        sender.BeginFile();
        sender.EncodeChunk(new byte[10]);
        sender.EndFile();

        Assert.True(sender.OnAck(new FileAckMessage("t1", 1, 10)));
        Assert.Equal(sender.TotalBytes, sender.AcknowledgedBytes);
        Assert.True(sender.IsAcknowledged);
    }

    [Fact]
    public void An_older_ack_does_not_move_back()
    {
        using var sender = TwoFiles();
        sender.Begin();
        sender.BeginFile();
        sender.EncodeChunk(new byte[TransferSender.ChunkSize]);
        sender.EncodeChunk(new byte[TransferSender.ChunkSize]);
        sender.OnAck(new FileAckMessage("t1", 0, 2 * TransferSender.ChunkSize));

        Assert.True(sender.OnAck(new FileAckMessage("t1", 0, TransferSender.ChunkSize)));
        Assert.Equal(2 * TransferSender.ChunkSize, sender.AcknowledgedBytes);
    }

    [Theory]
    [InlineData("other", 0, 10)]
    [InlineData("t1", 1, 0)]
    [InlineData("t1", 0, (2 * 262144) + 1)]
    [InlineData("t1", 0, 3 * 262144)]
    [InlineData("t1", -1, 0)]
    public void Wrong_acks_are_refused(string transferId, int fileIndex, long received)
    {
        using var sender = TwoFiles();
        sender.Begin();
        sender.BeginFile();
        sender.EncodeChunk(new byte[TransferSender.ChunkSize]);
        sender.EncodeChunk(new byte[TransferSender.ChunkSize]);

        Assert.False(sender.OnAck(new FileAckMessage(transferId, fileIndex, received)));
        Assert.Equal(0, sender.AcknowledgedBytes);
    }

    [Fact]
    public void The_order_is_enforced()
    {
        using var sender = TwoFiles();

        Assert.Throws<InvalidOperationException>(() => sender.BeginFile());
        sender.Begin();
        Assert.Throws<InvalidOperationException>(() => sender.Begin());
        Assert.Throws<InvalidOperationException>(() => sender.EncodeChunk([1]));
        sender.BeginFile();
        Assert.Throws<InvalidOperationException>(() => sender.BeginFile());
        Assert.Throws<InvalidOperationException>(() => sender.EndFile());
    }

    [Fact]
    public void Chunks_must_fit_the_file_and_the_chunk_size()
    {
        using var sender = new TransferSender("t1", [new TransferFile("a.bin", TransferSender.ChunkSize + 1)]);
        sender.Begin();
        sender.BeginFile();

        Assert.Throws<ArgumentException>(() => sender.EncodeChunk([]));
        Assert.Throws<ArgumentException>(() => sender.EncodeChunk(new byte[TransferSender.ChunkSize + 1]));
        sender.EncodeChunk(new byte[TransferSender.ChunkSize]);
        Assert.Throws<ArgumentException>(() => sender.EncodeChunk(new byte[2]));
    }

    [Fact]
    public void An_empty_file_ends_at_once_with_the_checksum_of_nothing()
    {
        using var sender = new TransferSender("t1", [new TransferFile("empty.txt", 0)]);
        sender.Begin();
        sender.BeginFile();

        var end = sender.EndFile();

        Assert.Equal("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", end.Sha256);
        Assert.True(sender.IsSent);
    }

    [Fact]
    public void Negative_sizes_are_refused()
    {
        Assert.Throws<ArgumentException>(() => new TransferSender("t1", [new TransferFile("a", -1)]));
    }
}
