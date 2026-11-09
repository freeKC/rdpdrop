using System.Diagnostics;
using System.Security.Cryptography;
using UltimateRDP.Core.Protocol;
using UltimateRDP.Core.Transfer;
using Xunit.Abstractions;

namespace UltimateRDP.Core.Tests.Transfer;

/// <summary>Whole transfers through the ASCII line codec, in both byte models.</summary>
public class SimulatedTransferTests(ITestOutputHelper output)
{
    public static TheoryData<ChannelByteModel> Models => [ChannelByteModel.OneBytePerChar, ChannelByteModel.Utf16];

    private static readonly TransferItem[] FolderAndFile =
    [
        new TransferItem("big.bin", false),
        new TransferItem("Photos", true),
    ];

    private static List<(TransferFile File, byte[] Data)> SampleFiles() =>
    [
        SimulatedTransfer.MakeFile("big.bin", (3 * 1024 * 1024) + 12345, 1),
        SimulatedTransfer.MakeFile(@"Photos\a.jpg", 700_001, 2),
        SimulatedTransfer.MakeFile(@"Photos\empty.txt", 0, 3),
        SimulatedTransfer.MakeFile(@"Photos\2026\b.raw", TransferSender.DefaultChunkSize * 2, 4),
    ];

    [Theory]
    [MemberData(nameof(Models))]
    public void A_few_megabytes_arrive_intact(ChannelByteModel model)
    {
        var storage = new MemoryTransferStorage();
        var files = SampleFiles();
        var transfer = new SimulatedTransfer(model, FolderAndFile, files, storage);

        transfer.Run();

        Assert.NotNull(transfer.Result);
        Assert.Equal(DropStatus.Ok, transfer.Result.Status);
        Assert.Equal(TransferState.Succeeded, transfer.Receiver.State);
        Assert.Equal(files.Sum(file => (long)file.Data.Length), transfer.Result.Bytes);
        foreach (var (file, data) in files)
        {
            Assert.Equal(data, storage.Content(file.RelativePath));
            Assert.Equal(file.Modified, storage.Completed[file.RelativePath]);
        }

        Assert.Equal(["Photos"], storage.Directories);
        Assert.True(transfer.Sender.IsAcknowledged);
        Assert.Equal(0, storage.DeleteAllCalls);
    }

    [Theory]
    [MemberData(nameof(Models))]
    public void The_window_holds_at_most_four_mebibytes_in_flight(ChannelByteModel model)
    {
        var files = new List<(TransferFile File, byte[] Data)> { SimulatedTransfer.MakeFile("big.bin", 12 * 1024 * 1024, 5) };
        var transfer = new SimulatedTransfer(model, [new TransferItem("big.bin", false)], files, new MemoryTransferStorage());

        transfer.Run();

        Assert.Equal(DropStatus.Ok, transfer.Result?.Status);
        Assert.True(transfer.WindowWaits > 0, "The client never had to wait for an acknowledgement.");
        Assert.True(transfer.MostInFlight <= TransferSender.WindowBytes);
        Assert.Equal(TransferSender.WindowBytes, transfer.MostInFlight);

        // One acknowledgement after the first chunk, then one per 2 MiB, and one at the end of the file.
        Assert.Equal(1 + 5 + 1, transfer.Acks);
    }

    [Theory]
    [MemberData(nameof(Models))]
    public void A_lost_chunk_fails_the_transfer_and_deletes_the_files(ChannelByteModel model)
    {
        var storage = new MemoryTransferStorage();
        var transfer = new SimulatedTransfer(model, FolderAndFile, SampleFiles(), storage);

        // Line 0 is transferBegin, line 1 fileBegin, lines 2 and up the chunks of big.bin.
        transfer.Channel.LostLines.Add(4);
        transfer.Run();

        Assert.Equal(DropStatus.Error, transfer.Result?.Status);
        Assert.Equal(TransferFailure.DataLost, transfer.Receiver.Failure);
        Assert.Equal("big.bin", transfer.Receiver.FailurePath);
        Assert.Contains($"starts at byte {3L * TransferSender.DefaultChunkSize}", transfer.Receiver.FailureDetail, StringComparison.Ordinal);
        Assert.Equal(1, storage.DeleteAllCalls);
        Assert.Empty(storage.Files);
    }

    [Theory]
    [MemberData(nameof(Models))]
    public void Damaged_data_fails_on_the_checksum(ChannelByteModel model)
    {
        var storage = new MemoryTransferStorage();
        var transfer = new SimulatedTransfer(model, FolderAndFile, SampleFiles(), storage);

        // Change one base64 digit of the third chunk: still valid base64, other bytes.
        transfer.Channel.Tamper = (position, line) =>
        {
            if (position != 4)
            {
                return line;
            }

            var index = line.IndexOf("\"data\":\"", StringComparison.Ordinal) + 100;
            var replacement = line[index] == 'A' ? 'B' : 'A';
            return string.Concat(line.AsSpan(0, index), replacement.ToString(), line.AsSpan(index + 1));
        };
        transfer.Run();

        Assert.Equal(DropStatus.Error, transfer.Result?.Status);
        Assert.Equal(TransferFailure.HashMismatch, transfer.Receiver.Failure);
        Assert.Equal("big.bin", transfer.Receiver.FailurePath);
        Assert.Empty(storage.Files);
    }

    [Theory]
    [MemberData(nameof(Models))]
    public void A_cancel_stops_the_transfer_and_deletes_the_files(ChannelByteModel model)
    {
        var storage = new MemoryTransferStorage();
        var transfer = new SimulatedTransfer(model, FolderAndFile, SampleFiles(), storage);

        transfer.Run(cancelAfterChunks: 5);

        Assert.Equal(DropStatus.Cancelled, transfer.Result?.Status);
        Assert.Equal(TransferState.Cancelled, transfer.Receiver.State);
        Assert.Equal(5L * TransferSender.DefaultChunkSize, transfer.Result?.Bytes);
        Assert.Equal(1, storage.DeleteAllCalls);
        Assert.Empty(storage.Files);
    }

    public static TheoryData<ChannelByteModel, int> ModelsAndChunkSizes => new()
    {
        { ChannelByteModel.OneBytePerChar, TransferSender.DefaultChunkSize },
        { ChannelByteModel.Utf16, TransferSender.DefaultChunkSize },
        { ChannelByteModel.OneBytePerChar, TransferSender.MinChunkSize },
        { ChannelByteModel.Utf16, TransferSender.MaxChunkSize },
    };

    [Theory]
    [MemberData(nameof(ModelsAndChunkSizes))]
    public void The_lines_stay_within_the_limit_and_in_ascii(ChannelByteModel model, int chunkSize)
    {
        var files = new List<(TransferFile File, byte[] Data)> { SimulatedTransfer.MakeFile("big.bin", (3 * chunkSize) + 7, 6) };
        var transfer = new SimulatedTransfer(model, [new TransferItem("big.bin", false)], files, new MemoryTransferStorage(), chunkSize);
        var longest = 0;
        transfer.Channel.Tamper = (_, line) =>
        {
            longest = Math.Max(longest, line.Length);
            Assert.All(line, c => Assert.True(c < 0x80));
            Assert.Equal('\n', line[^1]);
            Assert.Equal(1, line.Count(c => c == '\n'));
            return line;
        };

        transfer.Run();

        Assert.Equal(DropStatus.Ok, transfer.Result?.Status);
        Assert.InRange(longest, (chunkSize * 4 / 3) + 50, LineFraming.MaxLineLength);
        Assert.Equal(longest, transfer.Sender.LongestChunkLine);
        Assert.Equal(4, transfer.Sender.ChunkLines);
    }

    [Fact]
    public void A_default_chunk_line_stays_small_on_the_wire_in_both_models()
    {
        var files = new List<(TransferFile File, byte[] Data)> { SimulatedTransfer.MakeFile("big.bin", TransferSender.DefaultChunkSize, 9) };
        var transfer = new SimulatedTransfer(ChannelByteModel.Utf16, [new TransferItem("big.bin", false)], files, new MemoryTransferStorage());

        transfer.Run();

        // About 44 000 characters of base64, so about 88 KB per call in the UTF-16 model.
        Assert.Equal(DropStatus.Ok, transfer.Result?.Status);
        Assert.InRange(transfer.Sender.LongestChunkLine, 43_000, 45_000);
    }

    /// <summary>
    /// 100 MB through the whole codec path: chunk lines, the bytes of the channel in blocks
    /// of 1600, model detection, line reader, JSON, base64, checksums. A quadratic step
    /// would take minutes; the linear path takes a few seconds.
    /// </summary>
    [Theory]
    [MemberData(nameof(Models))]
    public void One_hundred_megabytes_go_through_the_codec_in_a_few_seconds(ChannelByteModel model)
    {
        const int Size = 100 * 1000 * 1000;
        var data = new byte[Size];
        new Random(7).NextBytes(data);
        var file = new TransferFile("big.bin", Size);
        var storage = new NullTransferStorage();
        var transfer = new SimulatedTransfer(model, [new TransferItem("big.bin", false)], [(file, data)], storage);

        var watch = Stopwatch.StartNew();
        transfer.Run();
        watch.Stop();

        Assert.Equal(DropStatus.Ok, transfer.Result?.Status);
        Assert.Equal(Size, storage.Written);
        var rate = Size / watch.Elapsed.TotalSeconds / 1_000_000;
        output.WriteLine($"{model}: 100 MB in {watch.Elapsed.TotalSeconds:0.00} s, {rate:0} MB/s, {transfer.Channel.LinesSent} lines, {transfer.Channel.CharactersSent / 1_000_000.0:0.0} M characters.");
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(60), $"100 MB took {watch.Elapsed.TotalSeconds:0.0} s.");
    }

    [Fact]
    public void The_checksum_is_the_sha256_of_the_file()
    {
        var (file, data) = SimulatedTransfer.MakeFile("a.bin", 1000, 8);
        using var sender = new TransferSender("t1", [file]);
        sender.Begin();
        sender.BeginFile();
        sender.EncodeChunk(data);

        var end = sender.EndFile();

        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(data)), end.Sha256);
    }
}
