using System.Diagnostics;
using UltimateRDP.Core.Protocol;
using UltimateRDP.Core.Transfer;
using Xunit.Abstractions;

namespace UltimateRDP.Core.Tests.Transfer;

/// <summary>
/// Whole transfers whose channel loses calls of the control, as seen in production from
/// 0.4.0 to 0.5.0, through the whole codec path: numbered lines, batches of the send
/// queue, bytes of the channel in blocks of 1600, model detection, line reader, hand
/// parser, the inbox of the agent, the outbox of the client, checksums on both sides.
/// </summary>
public class LostCallTests(ITestOutputHelper output)
{
    /// <summary>base64 in both byte models, and u15 in the UTF-16 model.</summary>
    public static TheoryData<ChannelByteModel, string> Paths => new()
    {
        { ChannelByteModel.OneBytePerChar, ChunkEncodings.Base64 },
        { ChannelByteModel.Utf16, ChunkEncodings.Base64 },
        { ChannelByteModel.Utf16, ChunkEncodings.U15 },
    };

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

    private static SimulatedTransfer Reliable(ChannelByteModel model, string encoding, ITransferStorage storage, IReadOnlyList<(TransferFile File, byte[] Data)>? files = null) =>
        new(model, FolderAndFile, files ?? SampleFiles(), storage, chunkEncoding: encoding, reliable: true) { Batching = new SendQueue() };

    private static void AssertIntact(SimulatedTransfer transfer, MemoryTransferStorage storage)
    {
        Assert.Equal(DropStatus.Ok, transfer.Result?.Status);
        Assert.Equal(TransferState.Succeeded, transfer.Receiver.State);
        foreach (var (file, data) in transfer.Files)
        {
            Assert.Equal(data, storage.Content(file.RelativePath));
        }

        Assert.True(transfer.Sender.IsAcknowledged);
        Assert.Equal(0, transfer.Outbox!.Count);
        Assert.Equal(InboxFailure.None, transfer.Inbox!.Failure);
        Assert.False(transfer.Inbox.HasGap);
    }

    private static bool Holds(string call, string type, int? fileIndex = null) =>
        call.Contains($"\"type\":\"{type}\",\"transferId\":\"t1\"{(fileIndex is null ? string.Empty : $",\"fileIndex\":{fileIndex}")}", StringComparison.Ordinal);

    [Theory]
    [MemberData(nameof(Paths))]
    public void Without_a_loss_nothing_is_sent_again(ChannelByteModel model, string encoding)
    {
        var storage = new MemoryTransferStorage();
        var transfer = Reliable(model, encoding, storage);

        transfer.Run();

        AssertIntact(transfer, storage);
        Assert.Empty(transfer.Requests);
        Assert.Equal(0, transfer.Outbox!.ResentLines);
        Assert.Equal(0, transfer.Outbox.Probes);
        Assert.Equal(0, transfer.Inbox!.DuplicateLines);
        Assert.Equal(transfer.Sender.LastSeq + 1, transfer.Inbox.Expected);
    }

    /// <summary>
    /// The incident of 0.5.0: transferBegin and fileBegin left in a call of their own, which
    /// was lost; the chunks of the next call arrived. The agent asks for lines 0 and 1, and
    /// the transfer completes.
    /// </summary>
    [Theory]
    [MemberData(nameof(Paths))]
    public void The_lost_call_of_transferBegin_and_fileBegin_is_sent_again(ChannelByteModel model, string encoding)
    {
        var storage = new MemoryTransferStorage();
        var transfer = Reliable(model, encoding, storage);
        transfer.FirstLinesAlone = true;
        transfer.Channel.LoseCall = (_, call) => !transfer.SendingAgain && Holds(call, MessageTypes.TransferBegin);

        transfer.Run();

        AssertIntact(transfer, storage);
        Assert.Equal(1, transfer.Channel.CallsLost);
        Assert.Equal([new TransferResendMessage("t1", 0, 1)], transfer.Requests);
        Assert.Equal(2, transfer.Outbox!.ResentLines);
        Assert.True(transfer.Inbox!.ReleasedLines > 0);
    }

    [Theory]
    [MemberData(nameof(Paths))]
    public void A_lost_call_of_chunks_is_sent_again(ChannelByteModel model, string encoding)
    {
        var storage = new MemoryTransferStorage();
        var transfer = Reliable(model, encoding, storage);
        transfer.Channel.LoseCall = (position, _) => position == 3 && !transfer.SendingAgain;

        transfer.Run();

        AssertIntact(transfer, storage);
        Assert.Equal(1, transfer.Channel.CallsLost);
        var request = Assert.Single(transfer.Requests);
        Assert.True(request.To >= request.From);
        Assert.Equal(request.To - request.From + 1, transfer.Outbox!.ResentLines);
    }

    /// <summary>
    /// The last call, with the last chunks and fileEnd, is lost: nothing after it shows the
    /// agent a gap. The client waits, sends its newest line again after two seconds, the
    /// agent sees the gap and asks for the rest.
    /// </summary>
    [Theory]
    [MemberData(nameof(Paths))]
    public void A_lost_last_call_with_fileEnd_is_found_by_the_newest_line_sent_again(ChannelByteModel model, string encoding)
    {
        var storage = new MemoryTransferStorage();
        var transfer = Reliable(model, encoding, storage);
        transfer.Channel.LoseCall = (_, call) => !transfer.SendingAgain && Holds(call, MessageTypes.FileEnd, 3);

        transfer.Run();

        AssertIntact(transfer, storage);
        Assert.Equal(1, transfer.Channel.CallsLost);
        Assert.Equal(1, transfer.Outbox!.Probes);
        Assert.InRange(transfer.Clock, TransferAckWatch.ProbeInterval.TotalMilliseconds, TransferAckWatch.ProbeInterval.TotalMilliseconds + 1000);
    }

    /// <summary>Only the line of the last fileEnd is lost, in a call of its own: the newest line sent again is that line.</summary>
    [Fact]
    public void A_lost_last_fileEnd_alone_is_sent_again_after_two_seconds()
    {
        var storage = new MemoryTransferStorage();
        var transfer = new SimulatedTransfer(ChannelByteModel.Utf16, FolderAndFile, SampleFiles(), storage, chunkEncoding: ChunkEncodings.U15, reliable: true);
        transfer.Channel.LoseCall = (_, call) => !transfer.SendingAgain && Holds(call, MessageTypes.FileEnd, 3);

        transfer.Run();

        AssertIntact(transfer, storage);
        Assert.Empty(transfer.Requests);
        Assert.Equal(1, transfer.Outbox!.Probes);
    }

    /// <summary>
    /// The window of 2 MiB and one chunk fills after chunk 66. Chunks 65 and 66 are lost:
    /// the agent cannot acknowledge 2 MiB more, the client cannot send, and nothing shows
    /// the gap until the client sends its newest line again.
    /// </summary>
    [Fact]
    public void Lost_chunks_before_a_full_window_are_found_by_the_newest_line_sent_again()
    {
        var storage = new MemoryTransferStorage();
        var files = new List<(TransferFile File, byte[] Data)> { SimulatedTransfer.MakeFile("big.bin", 12 * 1024 * 1024, 5) };
        var transfer = new SimulatedTransfer(ChannelByteModel.OneBytePerChar, [new TransferItem("big.bin", false)], files, storage, windowBytes: TransferSender.MinWindowBytes, reliable: true);

        // Without batching, one call per line: transferBegin, fileBegin, then chunk n in call n + 1.
        transfer.Channel.LoseCall = (position, _) => !transfer.SendingAgain && position is 66 or 67;

        transfer.Run();

        AssertIntact(transfer, storage);
        Assert.Equal(2, transfer.Channel.CallsLost);
        Assert.Equal(1, transfer.Outbox!.Probes);
        Assert.Equal([new TransferResendMessage("t1", 66, 66)], transfer.Requests);
    }

    [Theory]
    [MemberData(nameof(Paths))]
    public void A_lost_resend_is_asked_for_again(ChannelByteModel model, string encoding)
    {
        var storage = new MemoryTransferStorage();
        var transfer = Reliable(model, encoding, storage);
        transfer.FirstLinesAlone = true;
        var resendsLost = 0;
        transfer.Channel.LoseCall = (_, call) => transfer.SendingAgain
            ? resendsLost++ == 0
            : Holds(call, MessageTypes.TransferBegin);

        transfer.Run();

        AssertIntact(transfer, storage);
        Assert.Equal(2, transfer.Channel.CallsLost);
        Assert.Equal([new TransferResendMessage("t1", 0, 1), new TransferResendMessage("t1", 0, 1)], transfer.Requests);
        Assert.Equal(4, transfer.Outbox!.ResentLines);
        Assert.InRange(transfer.Clock, ReliableInbox.RequestInterval.TotalMilliseconds, 2000);
    }

    [Theory]
    [MemberData(nameof(Paths))]
    public void Calls_that_arrive_twice_are_ignored(ChannelByteModel model, string encoding)
    {
        var storage = new MemoryTransferStorage();
        var transfer = Reliable(model, encoding, storage);
        transfer.Channel.DuplicateCall = (position, _) => position % 3 == 1;

        transfer.Run();

        AssertIntact(transfer, storage);
        Assert.Empty(transfer.Requests);
        Assert.True(transfer.Inbox!.DuplicateLines > 0);
    }

    [Theory]
    [MemberData(nameof(Paths))]
    public void Lines_that_never_come_fail_the_transfer_after_ten_requests(ChannelByteModel model, string encoding)
    {
        var storage = new MemoryTransferStorage();
        var transfer = Reliable(model, encoding, storage);
        transfer.FirstLinesAlone = true;
        transfer.Channel.LoseCall = (_, call) => transfer.SendingAgain || Holds(call, MessageTypes.TransferBegin);

        transfer.Run();

        Assert.Equal(DropStatus.Error, transfer.Result?.Status);
        Assert.Equal(InboxFailure.Unrecovered, transfer.Inbox!.Failure);
        Assert.Equal(ReliableInbox.MaxRequests, transfer.Requests.Count);
        Assert.All(transfer.Requests, request => Assert.Equal(new TransferResendMessage("t1", 0, 1), request));
        Assert.Equal("lines 0 to 1 of the transfer never arrived after 10 requests to the client.", transfer.Result?.Message);
        Assert.Empty(storage.Files);
    }

    /// <summary>A line without a number, from a client before 0.5.1, is handled as it arrives, as before.</summary>
    [Fact]
    public void Lines_without_a_number_keep_the_old_behaviour()
    {
        var storage = new MemoryTransferStorage();
        var transfer = new SimulatedTransfer(ChannelByteModel.Utf16, FolderAndFile, SampleFiles(), storage, chunkEncoding: ChunkEncodings.U15) { Batching = new SendQueue(), FirstLinesAlone = true };
        transfer.Channel.LoseCall = (_, call) => Holds(call, MessageTypes.TransferBegin);

        transfer.Run();

        Assert.Equal(DropStatus.Error, transfer.Result?.Status);
        Assert.Equal(TransferFailure.MessageLost, transfer.Receiver.Failure);
        Assert.Equal(MessageTypes.TransferBegin, transfer.Receiver.LostMessage);
    }

    /// <summary>
    /// 100 MB with numbered lines and two lost calls, through the whole codec path: the
    /// reliability costs little, and the transfer still takes a few seconds.
    /// </summary>
    [Theory]
    [MemberData(nameof(Paths))]
    public void One_hundred_megabytes_with_two_lost_calls_go_through_in_a_few_seconds(ChannelByteModel model, string encoding)
    {
        const int Size = 100 * 1000 * 1000;
        var data = new byte[Size];
        new Random(7).NextBytes(data);
        var storage = new NullTransferStorage();
        var transfer = new SimulatedTransfer(model, [new TransferItem("big.bin", false)], [(new TransferFile("big.bin", Size), data)], storage, chunkEncoding: encoding, reliable: true) { Batching = new SendQueue() };
        transfer.Channel.LoseCall = (position, _) => !transfer.SendingAgain && position is 7 or 200;

        var watch = Stopwatch.StartNew();
        transfer.Run();
        watch.Stop();

        Assert.Equal(DropStatus.Ok, transfer.Result?.Status);
        Assert.Equal(Size, storage.Written);
        Assert.Equal(2, transfer.Channel.CallsLost);
        Assert.Equal(2, transfer.Requests.Count);
        output.WriteLine($"{model}, {encoding}: 100 MB with 2 lost calls in {watch.Elapsed.TotalSeconds:0.00} s, {Size / watch.Elapsed.TotalSeconds / 1_000_000:0} MB/s, {transfer.Channel.LinesSent} calls, {transfer.Outbox!.ResentLines} line(s) sent again.");
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(30), $"100 MB took {watch.Elapsed.TotalSeconds:0.0} s.");
    }
}
