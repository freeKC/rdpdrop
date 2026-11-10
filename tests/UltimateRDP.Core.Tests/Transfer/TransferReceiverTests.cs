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
    public void An_ack_goes_out_after_the_first_chunk_and_then_every_two_mebibytes()
    {
        using var receiver = Create();
        var chunk = new byte[TransferSender.DefaultChunkSize];
        const int Chunks = 160;
        receiver.Handle(new TransferBeginMessage("t1", 1, (long)Chunks * chunk.Length));
        receiver.Handle(new FileBeginMessage("t1", 0, "a.txt", (long)Chunks * chunk.Length));

        var acks = new List<FileAckMessage>();
        for (var i = 0; i < Chunks; i++)
        {
            if (receiver.Handle(Chunk(0, (long)i * chunk.Length, chunk)) is { } ack)
            {
                acks.Add(ack);
            }
        }

        Assert.Equal(
            [
                new FileAckMessage("t1", 0, chunk.Length),
                new FileAckMessage("t1", 0, (2L * 1024 * 1024) + chunk.Length),
                new FileAckMessage("t1", 0, (4L * 1024 * 1024) + chunk.Length),
            ],
            acks);
    }

    [Fact]
    public void The_first_chunk_of_every_file_is_acknowledged()
    {
        using var receiver = new TransferReceiver("t1", [new TransferItem("a.txt", false), new TransferItem("b.txt", false)], _storage, () => _now);
        receiver.Handle(new TransferBeginMessage("t1", 2, 20));
        var acks = new List<FileAckMessage?>();
        foreach (var (index, name) in new[] { (0, "a.txt"), (1, "b.txt") })
        {
            var data = new byte[10];
            receiver.Handle(new FileBeginMessage("t1", index, name, 10));
            acks.Add(receiver.Handle(Chunk(index, 0, data.AsSpan(0, 4).ToArray())));
            acks.Add(receiver.Handle(Chunk(index, 4, data.AsSpan(4).ToArray())));
            acks.Add(receiver.Handle(new FileEndMessage("t1", index, Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(data)))));
        }

        Assert.Equal(
            [
                new FileAckMessage("t1", 0, 4), null, new FileAckMessage("t1", 0, 10),
                new FileAckMessage("t1", 1, 4), null, new FileAckMessage("t1", 1, 10),
            ],
            acks);
        Assert.Equal(TransferState.Succeeded, receiver.State);
    }

    [Fact]
    public void A_chunk_of_the_largest_size_is_accepted()
    {
        using var receiver = Create();
        receiver.Handle(new TransferBeginMessage("t1", 1, TransferSender.MaxChunkSize));
        receiver.Handle(new FileBeginMessage("t1", 0, "a.txt", TransferSender.MaxChunkSize));

        var ack = receiver.Handle(Chunk(0, 0, new byte[TransferSender.MaxChunkSize]));

        Assert.Equal(new FileAckMessage("t1", 0, TransferSender.MaxChunkSize), ack);
        Assert.Equal(TransferState.Receiving, receiver.State);
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

    /// <summary>What the agent logged in production when the line of transferBegin was lost.</summary>
    [Fact]
    public void A_file_before_transferBegin_says_that_transferBegin_never_arrived()
    {
        using var receiver = Create();

        receiver.Handle(new FileBeginMessage("t1", 0, "a.txt", 10));

        Assert.Equal(TransferFailure.MessageLost, receiver.Failure);
        Assert.Equal("transferBegin", receiver.LostMessage);
        Assert.Null(receiver.LostFileIndex);
        Assert.Equal("fileBegin came before transferBegin: no transferBegin arrived for this transfer.", receiver.FailureDetail);
        Assert.Equal(1, _storage.DeleteAllCalls);
    }

    [Fact]
    public void A_chunk_above_the_chunk_size_is_refused()
    {
        using var receiver = Create();
        receiver.Handle(new TransferBeginMessage("t1", 1, TransferSender.MaxChunkSize + 3));
        receiver.Handle(new FileBeginMessage("t1", 0, "a.txt", TransferSender.MaxChunkSize + 3));

        receiver.Handle(Chunk(0, 0, new byte[TransferSender.MaxChunkSize + 3]));

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
        receiver.Handle(new TransferBeginMessage("t1", 2, 6));
        receiver.Handle(new FileBeginMessage("t1", 0, "a.txt", 3));
        receiver.Handle(Chunk(0, 0, [1, 2, 3]));
        receiver.Handle(new FileEndMessage("t1", 0, Hash([1, 2, 3])));

        receiver.Handle(Chunk(0, 3, [4]));

        Assert.Equal(TransferFailure.OutOfOrder, receiver.Failure);
    }

    /// <summary>What the agent logged in production when the line of fileBegin was lost before the first chunk.</summary>
    [Fact]
    public void A_chunk_whose_fileBegin_never_arrived_names_the_lost_message()
    {
        using var receiver = Create();
        receiver.Handle(new TransferBeginMessage("t1", 1, 3));

        receiver.Handle(Chunk(0, 0, [1, 2, 3]));

        Assert.Equal(TransferFailure.MessageLost, receiver.Failure);
        Assert.Equal("fileBegin", receiver.LostMessage);
        Assert.Equal(0, receiver.LostFileIndex);
        Assert.StartsWith("fileBegin for file 0 never arrived", receiver.FailureDetail, StringComparison.Ordinal);
        Assert.Equal(1, _storage.DeleteAllCalls);
    }

    [Fact]
    public void A_fileEnd_whose_fileBegin_never_arrived_names_the_lost_message()
    {
        using var receiver = Create();
        receiver.Handle(new TransferBeginMessage("t1", 1, 0));

        receiver.Handle(new FileEndMessage("t1", 0, Hash([])));

        Assert.Equal(TransferFailure.MessageLost, receiver.Failure);
        Assert.Equal("fileBegin", receiver.LostMessage);
        Assert.Equal(0, receiver.LostFileIndex);
    }

    [Fact]
    public void The_next_file_while_one_is_open_says_that_its_fileEnd_never_arrived()
    {
        using var receiver = Create();
        receiver.Handle(new TransferBeginMessage("t1", 2, 0));
        receiver.Handle(new FileBeginMessage("t1", 0, @"Folder\x", 0));

        receiver.Handle(new FileBeginMessage("t1", 1, @"Folder\y", 0));

        Assert.Equal(TransferFailure.MessageLost, receiver.Failure);
        Assert.Equal("fileEnd", receiver.LostMessage);
        Assert.Equal(0, receiver.LostFileIndex);
        Assert.Equal(@"Folder\x", receiver.FailurePath);
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

    private static FileChunkMessage U15Chunk(int index, long offset, byte[] data) =>
        new("t1", index, offset, U15Codec.Encode(data), data.Length, ChunkEncodings.U15);

    [Fact]
    public void U15_chunks_are_written_and_checked()
    {
        using var receiver = Create();
        var a = new byte[TransferSender.MaxChunkSize + 17];
        new Random(4).NextBytes(a);

        receiver.Handle(new TransferBeginMessage("t1", 1, a.Length, ChunkEncodings.U15));
        receiver.Handle(new FileBeginMessage("t1", 0, "a.txt", a.Length));
        var first = receiver.Handle(U15Chunk(0, 0, a[..TransferSender.MaxChunkSize]));
        receiver.Handle(U15Chunk(0, TransferSender.MaxChunkSize, a[TransferSender.MaxChunkSize..]));
        var last = receiver.Handle(new FileEndMessage("t1", 0, Hash(a)));

        Assert.Equal(ChunkEncodings.U15, receiver.ChunkEncoding);
        Assert.Equal(new FileAckMessage("t1", 0, TransferSender.MaxChunkSize), first);
        Assert.Equal(new FileAckMessage("t1", 0, a.Length), last);
        Assert.Equal(TransferState.Succeeded, receiver.State);
        Assert.Equal(a, _storage.Content("a.txt"));
    }

    [Fact]
    public void A_transfer_without_an_announced_encoding_reads_base64()
    {
        using var receiver = Create();
        receiver.Handle(new TransferBeginMessage("t1", 1, 3));

        Assert.Equal(ChunkEncodings.Base64, receiver.ChunkEncoding);
    }

    [Fact]
    public void An_unknown_chunk_encoding_fails_at_transferBegin()
    {
        using var receiver = Create();

        receiver.Handle(new TransferBeginMessage("t1", 1, 3, "l8"));

        Assert.Equal(TransferFailure.BadData, receiver.Failure);
        Assert.Contains("\"l8\"", receiver.FailureDetail, StringComparison.Ordinal);
    }

    [Fact]
    public void A_chunk_in_another_encoding_than_announced_fails()
    {
        using var receiver = Create();
        receiver.Handle(new TransferBeginMessage("t1", 1, 3, ChunkEncodings.U15));
        receiver.Handle(new FileBeginMessage("t1", 0, "a.txt", 3));

        receiver.Handle(Chunk(0, 0, [1, 2, 3]));

        Assert.Equal(TransferFailure.BadData, receiver.Failure);
        Assert.Contains("written in \"base64\" while transferBegin announced \"u15\"", receiver.FailureDetail, StringComparison.Ordinal);
        Assert.Empty(_storage.Files);
    }

    public static TheoryData<FileChunkMessage> BadU15Chunks
    {
        get
        {
            var units = U15Codec.Encode([1, 2, 3]);
            return
            [
                new FileChunkMessage("t1", 0, 0, units, null, ChunkEncodings.U15),
                new FileChunkMessage("t1", 0, 0, units, 0, ChunkEncodings.U15),
                new FileChunkMessage("t1", 0, 0, units, 2, ChunkEncodings.U15),
                new FileChunkMessage("t1", 0, 0, units[..^1], 3, ChunkEncodings.U15),
                new FileChunkMessage("t1", 0, 0, "???", 3, ChunkEncodings.U15),
                new FileChunkMessage("t1", 0, 0, U15Codec.Encode(new byte[TransferSender.MaxChunkSize + 1]), TransferSender.MaxChunkSize + 1, ChunkEncodings.U15),
            ];
        }
    }

    [Theory]
    [MemberData(nameof(BadU15Chunks))]
    public void Bad_u15_data_is_refused(FileChunkMessage chunk)
    {
        using var receiver = Create();
        receiver.Handle(new TransferBeginMessage("t1", 1, TransferSender.MaxChunkSize + 1, ChunkEncodings.U15));
        receiver.Handle(new FileBeginMessage("t1", 0, "a.txt", TransferSender.MaxChunkSize + 1));

        receiver.Handle(chunk);

        Assert.Equal(TransferFailure.BadData, receiver.Failure);
        Assert.Contains("is not valid u15", receiver.FailureDetail, StringComparison.Ordinal);
    }

    /// <summary>A receiver of numbered lines with a file of <paramref name="size"/> bytes open.</summary>
    private TransferReceiver Numbered(string encoding, long size)
    {
        var receiver = Create();
        receiver.Handle(new TransferBeginMessage("t1", 1, size, encoding, Seq: 0), out _);
        receiver.Handle(new FileBeginMessage("t1", 0, "a.txt", size, Seq: 1), out _);
        return receiver;
    }

    [Theory]
    [MemberData(nameof(BadU15Chunks))]
    public void A_bad_numbered_u15_chunk_is_left_for_a_resend(FileChunkMessage chunk)
    {
        using var receiver = Numbered(ChunkEncodings.U15, TransferSender.MaxChunkSize + 1);

        var ack = receiver.Handle(chunk with { Seq = 2 }, out var damage);

        Assert.Null(ack);
        Assert.NotNull(damage);
        Assert.Equal(2, damage.Seq);
        Assert.Equal(TransferFailure.BadData, damage.Failure);
        Assert.Contains("is not valid u15", damage.Detail, StringComparison.Ordinal);
        Assert.Equal(TransferState.Receiving, receiver.State);
        Assert.Equal(0, receiver.ReceivedBytes);
        Assert.Equal(1, receiver.DamagedChunks);
        Assert.Empty(_storage.Content("a.txt"));

        // The line sent again goes on as if nothing happened.
        Assert.Equal(new FileAckMessage("t1", 0, 3), receiver.Handle(U15Chunk(0, 0, [1, 2, 3]) with { Seq = 2 }, out damage));
        Assert.Null(damage);
        Assert.Equal(3, receiver.ReceivedBytes);
    }

    [Theory]
    [InlineData("AQI*")]
    [InlineData("AQI")]
    [InlineData("AQIDBA")]
    [InlineData("=AQI")]
    public void A_bad_numbered_base64_chunk_is_left_for_a_resend(string data)
    {
        using var receiver = Numbered(ChunkEncodings.Base64, 3);

        receiver.Handle(new FileChunkMessage("t1", 0, 0, data, Seq: 2), out var damage);

        Assert.Equal(TransferFailure.BadData, damage?.Failure);
        Assert.Equal(TransferState.Receiving, receiver.State);
        Assert.Equal(new FileAckMessage("t1", 0, 3), receiver.Handle(Chunk(0, 0, [1, 2, 3]) with { Seq = 2 }, out damage));
        Assert.Null(damage);
    }

    [Fact]
    public void A_numbered_chunk_at_the_wrong_offset_or_in_another_encoding_is_left_for_a_resend()
    {
        using var receiver = Numbered(ChunkEncodings.U15, 6);
        receiver.Handle(U15Chunk(0, 0, [1, 2, 3]) with { Seq = 2 }, out _);

        receiver.Handle(U15Chunk(0, 4, [4, 5, 6]) with { Seq = 3 }, out var offset);
        receiver.Handle(Chunk(0, 3, [4, 5, 6]) with { Seq = 3 }, out var encoding);
        receiver.Handle(U15Chunk(0, 3, [4, 5, 6]) with { Seq = 3, Enc = "u16" }, out var unknown);

        Assert.Equal(TransferFailure.DataLost, offset?.Failure);
        Assert.Equal("A chunk starts at byte 4 where byte 3 was expected.", offset?.Detail);
        Assert.Equal(TransferFailure.BadData, encoding?.Failure);
        Assert.Equal(TransferFailure.BadData, unknown?.Failure);
        Assert.Equal(ChunkEncodings.U15, unknown?.Encoding);
        Assert.Equal(3, receiver.DamagedChunks);
        Assert.Equal(TransferState.Receiving, receiver.State);
        receiver.Handle(U15Chunk(0, 3, [4, 5, 6]) with { Seq = 3 }, out var right);
        Assert.Null(right);
        Assert.Equal(6, receiver.ReceivedBytes);
    }

    [Fact]
    public void A_bad_chunk_without_a_number_still_fails_the_transfer()
    {
        using var receiver = Create();
        receiver.Handle(new TransferBeginMessage("t1", 1, 3, ChunkEncodings.U15), out _);
        receiver.Handle(new FileBeginMessage("t1", 0, "a.txt", 3), out _);

        receiver.Handle(new FileChunkMessage("t1", 0, 0, "???", 3, ChunkEncodings.U15), out var damage);

        Assert.Null(damage);
        Assert.Equal(TransferFailure.BadData, receiver.Failure);
        Assert.Equal(0, receiver.DamagedChunks);
    }

    [Fact]
    public void Other_problems_of_a_numbered_line_still_fail_the_transfer()
    {
        using var receiver = Numbered(ChunkEncodings.Base64, 3);

        receiver.Handle(Chunk(0, 0, [1, 2, 3, 4]) with { Seq = 2 }, out var damage);

        Assert.Null(damage);
        Assert.Equal(TransferFailure.SizeMismatch, receiver.Failure);
    }

    [Fact]
    public void A_damaged_u15_chunk_is_described_without_its_data()
    {
        var data = new byte[TransferSender.DefaultChunkSize];
        new Random(5).NextBytes(data);
        var units = U15Codec.Encode(data);
        var lineLength = TransferLines.EncodeChunk("t1", 0, 0, data, ChunkEncodings.U15, seq: 2).Length - 1;
        using var receiver = Numbered(ChunkEncodings.U15, data.Length);
        var chunk = new FileChunkMessage("t1", 0, 0, units[..100] + 'A' + units[101..], data.Length, ChunkEncodings.U15, 2);

        receiver.Handle(chunk, out var replaced);
        receiver.Handle(chunk with { Data = units[..800] + units[1600..] }, out var block);

        Assert.Equal(
            new ChunkDamage(2, 0, 0, ChunkEncodings.U15, TransferFailure.BadData, "A chunk at byte 0 is not valid u15 of 1 to 262144 bytes.", 17477, 32768, 17477, 100, 'A', lineLength, lineLength),
            replaced);
        Assert.Equal(
            $"line 2 arrived damaged: A chunk at byte 0 is not valid u15 of 1 to 262144 bytes. Chunk of file 0 at byte 0 in u15: 17477 unit(s) of data, as expected for 32768 bytes, first unit outside the range at 100, 0x0041; line of {lineLength} characters, as expected for that chunk size.",
            replaced?.Describe());
        Assert.Equal(
            $"line 2 arrived damaged: A chunk at byte 0 is not valid u15 of 1 to 262144 bytes. Chunk of file 0 at byte 0 in u15: 16677 unit(s) of data where 17477 were expected for 32768 bytes, every unit in the range; line of {lineLength - 800} characters where {lineLength} were expected for that chunk size.",
            block?.Describe());
        Assert.DoesNotContain(units[..20], replaced?.Describe(), StringComparison.Ordinal);
    }

    /// <summary>A block of 800 characters lost leaves valid base64: the length field of a numbered line, since 0.5.2, catches it.</summary>
    [Fact]
    public void Valid_base64_shorter_than_its_length_field_is_damaged()
    {
        var data = new byte[TransferSender.DefaultChunkSize];
        new Random(9).NextBytes(data);
        var base64 = Convert.ToBase64String(data);
        using var receiver = Numbered(ChunkEncodings.Base64, data.Length);

        receiver.Handle(new FileChunkMessage("t1", 0, 0, base64[..800] + base64[1600..], data.Length, Seq: 2), out var damage);

        Assert.Equal(TransferFailure.BadData, damage?.Failure);
        Assert.Equal(43692, damage?.ExpectedUnits);
        Assert.Equal(43692 - 800, damage?.Units);
        Assert.Null(damage?.BadUnitIndex);
        Assert.Equal(0, receiver.ReceivedBytes);
        Assert.NotNull(receiver.Handle(new FileChunkMessage("t1", 0, 0, base64, data.Length, Seq: 2), out damage));
        Assert.Null(damage);
    }

    /// <summary>A client 0.5.1 numbers its base64 lines without a length field: the chunks before tell the size.</summary>
    [Fact]
    public void A_damaged_base64_chunk_is_measured_against_the_chunks_before_it()
    {
        var data = new byte[3 * TransferSender.DefaultChunkSize];
        new Random(6).NextBytes(data);
        using var receiver = Numbered(ChunkEncodings.Base64, data.Length);
        var first = Convert.ToBase64String(data, 0, TransferSender.DefaultChunkSize);
        var firstLength = "{\"type\":\"fileChunk\",\"transferId\":\"t1\",\"fileIndex\":0,\"offset\":0,\"seq\":2,\"data\":\"\"}".Length + 43692;
        receiver.Handle(new FileChunkMessage("t1", 0, 0, first[..50] + '*' + first[51..], Seq: 2), out var unknown);
        receiver.Handle(new FileChunkMessage("t1", 0, 0, first, Seq: 2), out _);
        var second = Convert.ToBase64String(data, TransferSender.DefaultChunkSize, TransferSender.DefaultChunkSize);
        var lineLength = "{\"type\":\"fileChunk\",\"transferId\":\"t1\",\"fileIndex\":0,\"offset\":32768,\"seq\":3,\"data\":\"\"}".Length + 43692;

        receiver.Handle(new FileChunkMessage("t1", 0, TransferSender.DefaultChunkSize, second[..50] + '*' + second[51..], Seq: 3), out var known);

        Assert.Equal(
            $"line 2 arrived damaged: A chunk at byte 0 is not valid base64 of 1 to 262144 bytes. Chunk of file 0 at byte 0 in base64: 43692 character(s) of data, the expected count is not known, first invalid character at 50, 0x002A; line of {firstLength} characters, the expected length is not known.",
            unknown?.Describe());
        Assert.Equal(32768, known?.ExpectedBytes);
        Assert.Equal(43692, known?.ExpectedUnits);
        Assert.Equal(43692, known?.Units);
        Assert.Equal(50, known?.BadUnitIndex);
        Assert.Equal('*', known?.BadUnit);
        Assert.Equal(lineLength, known?.LineLength);
        Assert.Equal(lineLength, known?.ExpectedLineLength);
    }

    [Fact]
    public void The_current_acknowledgement_says_what_was_received_so_far()
    {
        using var receiver = Create();
        byte[] a = [1, 2, 3];
        receiver.Handle(new TransferBeginMessage("t1", 2, 5));
        Assert.Null(receiver.CurrentAck());

        receiver.Handle(new FileBeginMessage("t1", 0, "a.txt", 3));
        Assert.Equal(new FileAckMessage("t1", 0, 0), receiver.CurrentAck());
        receiver.Handle(Chunk(0, 0, [1, 2]));
        receiver.Handle(Chunk(0, 2, [3]));
        Assert.Equal(new FileAckMessage("t1", 0, 3), receiver.CurrentAck());

        receiver.Handle(new FileEndMessage("t1", 0, Hash(a)));
        Assert.Equal(new FileAckMessage("t1", 0, 3), receiver.CurrentAck());

        receiver.Cancel();
        Assert.Null(receiver.CurrentAck());
    }
}
