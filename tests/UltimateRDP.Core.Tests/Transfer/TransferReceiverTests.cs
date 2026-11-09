using System.Security.Cryptography;
using UltimateRDP.Core.Protocol;
using UltimateRDP.Core.Transfer;

namespace UltimateRDP.Core.Tests.Transfer;

public class TransferReceiverTests
{
    private static readonly TransferItem[] Items =
    [
        new TransferItem("a.txt", false),
        new TransferItem("Folder", true),
    ];

    private readonly MemoryTransferStorage _storage = new();
    private long _now = 1000;

    private TransferReceiver Create() => new("t1", Items, _storage, () => _now);

    private static string Hash(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

    private static FileChunkMessage Chunk(int index, long offset, byte[] data) => new("t1", index, offset, Convert.ToBase64String(data));

    [Fact]
    public void A_transfer_writes_and_checks_every_file()
    {
        using var receiver = Create();
        byte[] a = [1, 2, 3];
        byte[] b = [4, 5];

        receiver.Handle(new TransferBeginMessage("t1", 2, 5));
        receiver.Handle(new FileBeginMessage("t1", 0, "a.txt", 3));
        receiver.Handle(Chunk(0, 0, a));
        var firstAck = receiver.Handle(new FileEndMessage("t1", 0, Hash(a)));
        receiver.Handle(new FileBeginMessage("t1", 1, @"Folder\sub\b.bin", 2));
        receiver.Handle(Chunk(1, 0, b));
        _now += 2500;
        var lastAck = receiver.Handle(new FileEndMessage("t1", 1, Hash(b)));

        Assert.Equal(new FileAckMessage("t1", 0, 3), firstAck);
        Assert.Equal(new FileAckMessage("t1", 1, 2), lastAck);
        Assert.Equal(TransferState.Succeeded, receiver.State);
        Assert.True(receiver.IsFinished);
        Assert.Equal(5, receiver.ReceivedBytes);
        Assert.Equal(2.5, receiver.Seconds);
        Assert.Equal(a, _storage.Content("a.txt"));
        Assert.Equal(b, _storage.Content(@"Folder\sub\b.bin"));
        Assert.Equal(["Folder"], _storage.Directories);
    }

    [Fact]
    public void An_ack_goes_out_every_two_mebibytes()
    {
        using var receiver = Create();
        var chunk = new byte[TransferSender.ChunkSize];
        receiver.Handle(new TransferBeginMessage("t1", 1, 16L * TransferSender.ChunkSize));
        receiver.Handle(new FileBeginMessage("t1", 0, "a.txt", 16L * TransferSender.ChunkSize));

        var acks = new List<FileAckMessage>();
        for (var i = 0; i < 16; i++)
        {
            if (receiver.Handle(Chunk(0, (long)i * chunk.Length, chunk)) is { } ack)
            {
                acks.Add(ack);
            }
        }

        Assert.Equal(
            [new FileAckMessage("t1", 0, 2L * 1024 * 1024), new FileAckMessage("t1", 0, 4L * 1024 * 1024)],
            acks);
    }

    [Fact]
    public void An_empty_drop_of_folders_succeeds_at_once()
    {
        using var receiver = new TransferReceiver("t1", [new TransferItem("Empty", true)], _storage, () => _now);

        receiver.Handle(new TransferBeginMessage("t1", 0, 0));

        Assert.Equal(TransferState.Succeeded, receiver.State);
        Assert.Equal(["Empty"], _storage.Directories);
    }

    [Fact]
    public void A_missing_offset_is_data_lost()
    {
        using var receiver = Create();
        receiver.Handle(new TransferBeginMessage("t1", 1, 10));
        receiver.Handle(new FileBeginMessage("t1", 0, "a.txt", 10));
        receiver.Handle(Chunk(0, 0, [1, 2, 3]));

        receiver.Handle(Chunk(0, 6, [7, 8, 9, 10]));

        Assert.Equal(TransferState.Failed, receiver.State);
        Assert.Equal(TransferFailure.DataLost, receiver.Failure);
        Assert.Equal("a.txt", receiver.FailurePath);
        Assert.Equal(1, _storage.DeleteAllCalls);
    }

    [Fact]
    public void A_wrong_checksum_refuses_the_file()
    {
        using var receiver = Create();
        receiver.Handle(new TransferBeginMessage("t1", 1, 3));
        receiver.Handle(new FileBeginMessage("t1", 0, "a.txt", 3));
        receiver.Handle(Chunk(0, 0, [1, 2, 3]));

        var ack = receiver.Handle(new FileEndMessage("t1", 0, Hash([1, 2, 4])));

        Assert.Null(ack);
        Assert.Equal(TransferFailure.HashMismatch, receiver.Failure);
        Assert.Empty(_storage.Files);
    }

    [Fact]
    public void A_short_file_is_refused_at_its_end()
    {
        using var receiver = Create();
        receiver.Handle(new TransferBeginMessage("t1", 1, 3));
        receiver.Handle(new FileBeginMessage("t1", 0, "a.txt", 3));
        receiver.Handle(Chunk(0, 0, [1, 2]));

        receiver.Handle(new FileEndMessage("t1", 0, Hash([1, 2])));

        Assert.Equal(TransferFailure.SizeMismatch, receiver.Failure);
    }

    [Fact]
    public void A_file_longer_than_announced_is_refused()
    {
        using var receiver = Create();
        receiver.Handle(new TransferBeginMessage("t1", 1, 3));
        receiver.Handle(new FileBeginMessage("t1", 0, "a.txt", 3));

        receiver.Handle(Chunk(0, 0, [1, 2, 3, 4]));

        Assert.Equal(TransferFailure.SizeMismatch, receiver.Failure);
    }

    [Fact]
    public void Files_larger_than_the_transfer_are_refused_before_writing()
    {
        using var receiver = Create();
        receiver.Handle(new TransferBeginMessage("t1", 1, 3));

        receiver.Handle(new FileBeginMessage("t1", 0, "a.txt", 4));

        Assert.Equal(TransferFailure.SizeMismatch, receiver.Failure);
        Assert.Empty(_storage.Files);
    }

    [Theory]
    [InlineData("not base64!")]
    [InlineData("")]
    public void Bad_data_is_refused(string data)
    {
        using var receiver = Create();
        receiver.Handle(new TransferBeginMessage("t1", 1, 3));
        receiver.Handle(new FileBeginMessage("t1", 0, "a.txt", 3));

        receiver.Handle(new FileChunkMessage("t1", 0, 0, data));

        Assert.Equal(TransferFailure.BadData, receiver.Failure);
    }

    [Fact]
    public void A_chunk_above_the_chunk_size_is_refused()
    {
        using var receiver = Create();
        receiver.Handle(new TransferBeginMessage("t1", 1, TransferSender.ChunkSize + 3));
        receiver.Handle(new FileBeginMessage("t1", 0, "a.txt", TransferSender.ChunkSize + 3));

        receiver.Handle(Chunk(0, 0, new byte[TransferSender.ChunkSize + 3]));

        Assert.Equal(TransferFailure.BadData, receiver.Failure);
    }

    [Theory]
    [InlineData(@"..\a.txt")]
    [InlineData(@"Other\a.txt")]
    [InlineData(@"Folder\NUL")]
    [InlineData("Folder")]
    public void Paths_outside_the_items_are_refused(string relativePath)
    {
        using var receiver = Create();
        receiver.Handle(new TransferBeginMessage("t1", 1, 3));

        receiver.Handle(new FileBeginMessage("t1", 0, relativePath, 3));

        Assert.Equal(TransferFailure.BadName, receiver.Failure);
        Assert.Empty(_storage.Files);
    }

    [Fact]
    public void A_path_received_twice_is_refused()
    {
        using var receiver = Create();
        receiver.Handle(new TransferBeginMessage("t1", 2, 0));
        receiver.Handle(new FileBeginMessage("t1", 0, @"Folder\x", 0));
        receiver.Handle(new FileEndMessage("t1", 0, Hash([])));

        receiver.Handle(new FileBeginMessage("t1", 1, @"FOLDER\X", 0));

        Assert.Equal(TransferFailure.BadName, receiver.Failure);
    }

    [Fact]
    public void Messages_out_of_order_fail()
    {
        using var receiver = Create();

        receiver.Handle(new FileBeginMessage("t1", 0, "a.txt", 3));

        Assert.Equal(TransferFailure.OutOfOrder, receiver.Failure);
    }

    [Fact]
    public void A_skipped_file_index_fails()
    {
        using var receiver = Create();
        receiver.Handle(new TransferBeginMessage("t1", 2, 0));

        receiver.Handle(new FileBeginMessage("t1", 1, "a.txt", 0));

        Assert.Equal(TransferFailure.OutOfOrder, receiver.Failure);
    }

    [Fact]
    public void A_dropped_file_that_never_came_fails_the_transfer()
    {
        using var receiver = Create();
        receiver.Handle(new TransferBeginMessage("t1", 1, 0));
        receiver.Handle(new FileBeginMessage("t1", 0, @"Folder\x", 0));

        receiver.Handle(new FileEndMessage("t1", 0, Hash([])));

        Assert.Equal(TransferFailure.Incomplete, receiver.Failure);
        Assert.Equal("a.txt", receiver.FailurePath);
    }

    [Fact]
    public void A_cancel_deletes_the_files()
    {
        using var receiver = Create();
        receiver.Handle(new TransferBeginMessage("t1", 1, 3));
        receiver.Handle(new FileBeginMessage("t1", 0, "a.txt", 3));
        receiver.Handle(Chunk(0, 0, [1]));

        receiver.Handle(new TransferCancelMessage("t1", "The user cancelled."));

        Assert.Equal(TransferState.Cancelled, receiver.State);
        Assert.Equal(1, receiver.ReceivedBytes);
        Assert.Equal(1, _storage.DeleteAllCalls);
        Assert.Empty(_storage.Files);
    }

    [Fact]
    public void A_write_failure_fails_the_transfer()
    {
        using var receiver = Create();
        _storage.FailOnCreate = true;
        receiver.Handle(new TransferBeginMessage("t1", 1, 3));

        receiver.Handle(new FileBeginMessage("t1", 0, "a.txt", 3));

        Assert.Equal(TransferFailure.WriteFailed, receiver.Failure);
        Assert.Equal("The disk is full.", receiver.FailureDetail);
    }

    [Fact]
    public void Other_transfers_and_late_messages_are_ignored()
    {
        using var receiver = Create();

        Assert.Null(receiver.Handle(new TransferBeginMessage("t2", 1, 3)));
        Assert.Equal(TransferState.Waiting, receiver.State);

        receiver.Handle(new TransferBeginMessage("t1", 0, 0));
        Assert.Equal(TransferFailure.Incomplete, receiver.Failure);
        Assert.Null(receiver.Handle(new FileBeginMessage("t1", 0, "a.txt", 3)));
        Assert.Equal(TransferFailure.Incomplete, receiver.Failure);
    }

    [Fact]
    public void Disposing_an_unfinished_transfer_cancels_it()
    {
        var receiver = Create();
        receiver.Handle(new TransferBeginMessage("t1", 1, 3));
        receiver.Handle(new FileBeginMessage("t1", 0, "a.txt", 3));

        receiver.Dispose();

        Assert.Equal(TransferState.Cancelled, receiver.State);
        Assert.Equal(1, _storage.DeleteAllCalls);
    }
}
