using System.Diagnostics;
using System.Security.Cryptography;
using UltimateRDP.Core.Direct;
using UltimateRDP.Core.Protocol;
using UltimateRDP.Core.Tests.Transfer;
using UltimateRDP.Core.Transfer;
using Xunit.Abstractions;

namespace UltimateRDP.Core.Tests.Direct;

/// <summary>
/// Whole direct transfers between a <see cref="DirectSender"/> and a <see cref="DirectReceiver"/>
/// through an in memory connection, as the client and the agent run them over TLS.
/// </summary>
public class DirectTransferTests(ITestOutputHelper output)
{
    private static readonly byte[] Token = RandomNumberGenerator.GetBytes(DirectFrames.TokenLength);

    private static readonly TransferItem[] Items =
    [
        new TransferItem("a.bin", false),
        new TransferItem("Folder", true),
    ];

    private static byte[] Data(int length, int seed)
    {
        var data = new byte[length];
        new Random(seed).NextBytes(data);
        return data;
    }

    [Fact]
    public void A_transfer_writes_every_file_and_acknowledges_by_eight_mebibytes_and_at_each_file_end()
    {
        var a = Data(20 * 1024 * 1024, 1);
        var b = Data(3, 2);
        var c = Data(0, 3);
        var modified = new DateTime(2026, 11, 10, 8, 30, 0, DateTimeKind.Utc);
        TransferFile[] files =
        [
            new("a.bin", a.Length, modified),
            new(@"Folder\sub\b.txt", b.Length),
            new(@"Folder\été ü.txt", c.Length, modified),
        ];

        var run = Run(files, [a, b, c]);

        Assert.Equal(DirectSendStatus.Succeeded, run.Outcome.Status);
        Assert.True(run.Outcome.Acknowledged);
        Assert.Equal(DropStatus.Ok, run.Outcome.Result!.Status);
        Assert.Equal(a.Length + b.Length, run.Outcome.Result.Bytes);
        Assert.Equal("done", run.Outcome.Result.Message);
        Assert.Equal(DirectReceiverState.Succeeded, run.Receiver.State);
        Assert.True(run.Receiver.FinalWasSent);
        Assert.Equal(a, run.Storage.Content("a.bin"));
        Assert.Equal(b, run.Storage.Content(@"Folder\sub\b.txt"));
        Assert.Equal(c, run.Storage.Content(@"Folder\été ü.txt"));
        Assert.Equal(modified, run.Storage.Completed["a.bin"]);
        Assert.Null(run.Storage.Completed[@"Folder\sub\b.txt"]);
        Assert.Equal(["Folder"], run.Storage.Directories);
        Assert.Equal(0, run.Storage.DeleteAllCalls);

        // Two acknowledgements inside the first file, one at the end of each of the three files.
        Assert.Equal(
            [
                new DirectAck(0, 8L * 1024 * 1024, 8L * 1024 * 1024),
                new DirectAck(0, 16L * 1024 * 1024, 16L * 1024 * 1024),
                new DirectAck(0, a.Length, a.Length),
                new DirectAck(1, b.Length, a.Length + b.Length),
                new DirectAck(2, 0, a.Length + b.Length),
            ],
            run.Acks);
    }

    [Fact]
    public void A_transfer_of_empty_folders_only_ends_at_the_begin_frame()
    {
        var run = Run([], [], items: [new TransferItem("Empty", true)]);

        Assert.Equal(DirectSendStatus.Succeeded, run.Outcome.Status);
        Assert.False(run.Outcome.Acknowledged);
        Assert.Equal(["Empty"], run.Storage.Directories);
    }

    [Fact]
    public void A_wrong_token_gets_no_answer_and_the_client_sees_a_refusal()
    {
        var a = Data(1000, 1);
        var run = Run([new("a.bin", a.Length)], [a], clientToken: RandomNumberGenerator.GetBytes(DirectFrames.TokenLength));

        Assert.Equal(DirectSendStatus.Refused, run.Outcome.Status);
        Assert.False(run.Outcome.Acknowledged);
        Assert.Equal(DirectFailure.Token, run.Receiver.Failure);
        Assert.False(run.Receiver.FinalWasSent);
        Assert.False(run.Authenticated);
        Assert.Empty(run.Storage.Files);
    }

    [Fact]
    public void A_token_of_another_version_is_refused()
    {
        var (client, agent) = DuplexPipe.Create();
        var receiver = new DirectReceiver(Token, Items, new MemoryTransferStorage(), () => 0);
        var thread = new Thread(() =>
        {
            receiver.Run(agent, _ => null);
            agent.Dispose();
        });
        thread.Start();
        DirectFrames.Write(client, DirectFrameType.Token, DirectFrames.TokenPayload(Token, version: 2));
        var reader = new DirectFrameReader(client);

        Assert.False(reader.TryRead(out _, out _));
        thread.Join();
        Assert.Equal(DirectFailure.Token, receiver.Failure);
        Assert.Contains("version 2", receiver.FailureDetail, StringComparison.Ordinal);
    }

    [Fact]
    public void The_token_is_single_use()
    {
        var (client, agent) = DuplexPipe.Create();
        var receiver = new DirectReceiver(Token, Items, new MemoryTransferStorage(), () => 0);
        var thread = new Thread(() =>
        {
            receiver.Run(agent, _ => null);
            agent.Dispose();
        });
        thread.Start();
        DirectFrames.Write(client, DirectFrameType.Token, DirectFrames.TokenPayload(Token));
        var reader = new DirectFrameReader(client);
        Assert.True(reader.TryRead(out var ready, out _));
        Assert.Equal(DirectFrameType.Ready, ready);

        // The same token a second time is a frame out of order, and ends the transfer.
        DirectFrames.Write(client, DirectFrameType.Token, DirectFrames.TokenPayload(Token));
        Assert.True(reader.TryRead(out var final, out var payload));
        Assert.Equal(DirectFrameType.Final, final);
        Assert.True(DirectFrames.TryParseFinal(payload.Span, out var result));
        client.Dispose();
        thread.Join();
        Assert.Equal(DropStatus.Error, result.Status);
        Assert.Equal(DirectFailure.Protocol, result.Failure);
    }

    [Fact]
    public void A_stream_cut_before_the_first_acknowledgement_is_broken_without_acknowledgement()
    {
        var a = Data(5 * 1024 * 1024, 1);
        var run = Run([new("a.bin", a.Length)], [a], cutAfter: 2 * 1024 * 1024);

        Assert.Equal(DirectSendStatus.Broken, run.Outcome.Status);
        Assert.False(run.Outcome.Acknowledged);
        Assert.Equal(DirectReceiverState.Failed, run.Receiver.State);
        Assert.Equal(DirectFailure.Broken, run.Receiver.Failure);
        Assert.False(run.Receiver.FinalWasSent);
        Assert.Equal(1, run.Storage.DeleteAllCalls);
    }

    [Fact]
    public void A_stream_cut_after_an_acknowledgement_is_broken_with_acknowledgement()
    {
        var a = Data(12 * 1024 * 1024, 1);
        var run = Run([new("a.bin", a.Length)], [a], cutAfter: 10 * 1024 * 1024);

        Assert.Equal(DirectSendStatus.Broken, run.Outcome.Status);
        Assert.True(run.Outcome.Acknowledged);
        Assert.Equal(DirectFailure.Broken, run.Receiver.Failure);
        Assert.Equal(1, run.Storage.DeleteAllCalls);
    }

    [Fact]
    public void A_wrong_checksum_fails_the_transfer_with_a_final_frame_and_deletes_what_was_written()
    {
        var a = Data(3000, 1);
        var (client, agent, receiver, storage, thread) = StartAgent();
        var reader = Authenticate(client);
        DirectFrames.Write(client, DirectFrameType.Begin, DirectFrames.BeginPayload(1, a.Length));
        DirectFrames.Write(client, DirectFrameType.FileHeader, DirectFrames.FileHeaderPayload(0, new TransferFile("a.bin", a.Length)));
        DirectFrames.Write(client, DirectFrameType.Data, a);
        var wrong = SHA256.HashData(a);
        wrong[0] ^= 1;
        DirectFrames.Write(client, DirectFrameType.FileTrailer, DirectFrames.FileTrailerPayload(0, wrong));

        var result = ReadFinal(reader);
        client.Dispose();
        thread.Join();

        Assert.Equal(DropStatus.Error, result.Status);
        Assert.Equal(DirectFailure.HashMismatch, result.Failure);
        Assert.Equal("failed HashMismatch", result.Message);
        Assert.Equal(DirectFailure.HashMismatch, receiver.Failure);
        Assert.Equal("a.bin", receiver.FailurePath);
        Assert.Equal(1, storage.DeleteAllCalls);
        Assert.Empty(storage.Completed);
    }

    [Fact]
    public void A_cancel_frame_ends_the_transfer_as_cancelled()
    {
        var a = Data(30 * 1024 * 1024, 1);
        DirectSender? sender = null;
        var run = Run([new("a.bin", a.Length)], [a], onAck: (s, _) => s.Cancel("The user cancelled the transfer."), created: s => sender = s);

        Assert.NotNull(sender);
        Assert.Equal(DirectSendStatus.Cancelled, run.Outcome.Status);
        Assert.True(run.Outcome.Acknowledged);
        Assert.Equal(DropStatus.Cancelled, run.Outcome.Result!.Status);
        Assert.Equal(DirectReceiverState.Cancelled, run.Receiver.State);
        Assert.Equal("The user cancelled the transfer.", run.Receiver.CancelReason);
        Assert.Equal(1, run.Storage.DeleteAllCalls);
    }

    [Fact]
    public void A_path_that_leaves_the_items_is_refused()
    {
        var a = Data(10, 1);
        var run = Run([new(@"a.bin\..\..\evil.txt", a.Length)], [a]);

        Assert.Equal(DirectSendStatus.AgentFailed, run.Outcome.Status);
        Assert.Equal(DirectFailure.BadName, run.Outcome.Result!.Failure);
        Assert.Empty(run.Storage.Files);
    }

    [Fact]
    public void A_dropped_file_that_never_comes_leaves_the_transfer_incomplete()
    {
        var b = Data(10, 1);
        var run = Run([new(@"Folder\b.txt", b.Length)], [b]);

        Assert.Equal(DirectSendStatus.AgentFailed, run.Outcome.Status);
        Assert.Equal(DirectFailure.Incomplete, run.Outcome.Result!.Failure);
        Assert.Equal("a.bin", run.Receiver.FailurePath);
    }

    [Fact]
    public void Announced_bytes_that_do_not_fit_are_refused_at_the_begin_frame()
    {
        var a = Data(5000, 1);
        var run = Run([new("a.bin", a.Length)], [a], hasRoomFor: bytes => bytes < 1000);

        Assert.Equal(DirectSendStatus.AgentFailed, run.Outcome.Status);
        Assert.Equal(DirectFailure.NoSpace, run.Outcome.Result!.Failure);
        Assert.False(run.Outcome.Acknowledged);
    }

    [Fact]
    public void A_file_that_got_shorter_cancels_the_transfer()
    {
        var a = Data(5000, 1);
        var run = Run([new("a.bin", a.Length + 10)], [a]);

        Assert.Equal(DirectSendStatus.LocalFailure, run.Outcome.Status);
        Assert.True(run.Outcome.FileChanged);
        Assert.Equal("a.bin", run.Outcome.Path);
        Assert.Equal(DirectReceiverState.Cancelled, run.Receiver.State);
    }

    [Fact]
    public void A_file_that_cannot_be_opened_cancels_the_transfer()
    {
        var run = Run([new("a.bin", 10)], [null]);

        Assert.Equal(DirectSendStatus.LocalFailure, run.Outcome.Status);
        Assert.False(run.Outcome.FileChanged);
        Assert.Equal(DirectReceiverState.Cancelled, run.Receiver.State);
    }

    [Fact]
    public void Items_that_cannot_be_listed_after_the_token_cancel_the_transfer()
    {
        var (client, agent) = DuplexPipe.Create();
        var storage = new MemoryTransferStorage();
        var receiver = new DirectReceiver(Token, Items, storage, () => 0);
        var thread = new Thread(() =>
        {
            receiver.Run(agent, _ => null);
            agent.Dispose();
        });
        thread.Start();
        var listed = false;
        var sender = new DirectSender(() =>
        {
            listed = true;
            return null;
        }, _ => throw new InvalidOperationException("Nothing to open."));

        var outcome = sender.Run(client, Token);
        client.Dispose();
        thread.Join();

        Assert.True(listed);
        Assert.Equal(DirectSendStatus.LocalFailure, outcome.Status);
        Assert.Null(outcome.Path);
        Assert.Equal(DirectReceiverState.Cancelled, receiver.State);
        Assert.Equal(DropStatus.Cancelled, outcome.Result!.Status);
    }

    [Fact]
    public void A_data_frame_before_any_file_breaks_the_rules()
    {
        var (client, _, receiver, storage, thread) = StartAgent();
        var reader = Authenticate(client);
        DirectFrames.Write(client, DirectFrameType.Begin, DirectFrames.BeginPayload(1, 10));
        DirectFrames.Write(client, DirectFrameType.Data, new byte[10]);

        var result = ReadFinal(reader);
        client.Dispose();
        thread.Join();

        Assert.Equal(DirectFailure.Protocol, result.Failure);
        Assert.Equal(DirectFailure.Protocol, receiver.Failure);
        Assert.Equal(1, storage.DeleteAllCalls);
    }

    [Fact]
    public void A_frame_longer_than_allowed_breaks_the_connection()
    {
        var (client, _, receiver, _, thread) = StartAgent();
        var reader = Authenticate(client);
        var header = new byte[DirectFrames.HeaderLength];
        DirectFrames.WriteHeader(header, DirectFrameType.Data, DirectFrames.MaxPayloadLength + 1);
        client.Write(header);

        var result = ReadFinal(reader);
        client.Dispose();
        thread.Join();

        Assert.Equal(DirectFailure.Protocol, result.Failure);
        Assert.Contains("more than", receiver.FailureDetail, StringComparison.Ordinal);
    }

    [Fact]
    public void A_file_longer_than_announced_fails()
    {
        var (client, _, receiver, _, thread) = StartAgent();
        var reader = Authenticate(client);
        DirectFrames.Write(client, DirectFrameType.Begin, DirectFrames.BeginPayload(1, 10));
        DirectFrames.Write(client, DirectFrameType.FileHeader, DirectFrames.FileHeaderPayload(0, new TransferFile("a.bin", 10)));
        DirectFrames.Write(client, DirectFrameType.Data, new byte[11]);

        var result = ReadFinal(reader);
        client.Dispose();
        thread.Join();

        Assert.Equal(DirectFailure.SizeMismatch, result.Failure);
        Assert.Equal(DirectFailure.SizeMismatch, receiver.Failure);
    }

    [Fact]
    public void A_storage_that_cannot_write_fails_the_transfer()
    {
        var a = Data(100, 1);
        var storage = new MemoryTransferStorage { FailOnCreate = true };
        var run = Run([new("a.bin", a.Length)], [a], storage: storage);

        Assert.Equal(DirectSendStatus.AgentFailed, run.Outcome.Status);
        Assert.Equal(DirectFailure.WriteFailed, run.Outcome.Result!.Failure);
    }

    [Fact]
    public void One_hundred_megabytes_go_through_in_about_a_second()
    {
        const int Size = 100_000_000;
        var data = Data(Size, 7);
        var storage = new NullTransferStorage();
        var watch = Stopwatch.StartNew();
        var run = Run([new("a.bin", Size)], [data], storage: storage);
        watch.Stop();
        output.WriteLine($"100 MB through the direct frames in {watch.ElapsedMilliseconds} ms.");

        Assert.Equal(DirectSendStatus.Succeeded, run.Outcome.Status);
        Assert.Equal(Size, storage.Written);
        Assert.Equal(12, run.Acks.Count);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(15), $"100 MB took {watch.ElapsedMilliseconds} ms.");
    }

    private static (Stream Client, Stream Agent, DirectReceiver Receiver, MemoryTransferStorage Storage, Thread Thread) StartAgent()
    {
        var (client, agent) = DuplexPipe.Create();
        var storage = new MemoryTransferStorage();
        var receiver = new DirectReceiver(Token, Items, storage, () => 0);
        var thread = new Thread(() =>
        {
            receiver.Run(agent, r => "failed " + r.Failure);
            agent.Dispose();
        });
        thread.Start();
        return (client, agent, receiver, storage, thread);
    }

    private static DirectFrameReader Authenticate(Stream client)
    {
        DirectFrames.Write(client, DirectFrameType.Token, DirectFrames.TokenPayload(Token));
        var reader = new DirectFrameReader(client);
        Assert.True(reader.TryRead(out var type, out _));
        Assert.Equal(DirectFrameType.Ready, type);
        return reader;
    }

    private static DirectResult ReadFinal(DirectFrameReader reader)
    {
        while (reader.TryRead(out var type, out var payload))
        {
            if (type == DirectFrameType.Final)
            {
                Assert.True(DirectFrames.TryParseFinal(payload.Span, out var result));
                return result;
            }
        }

        throw new InvalidOperationException("No final frame.");
    }

    private static RunResult Run(
        TransferFile[] files,
        byte[]?[] contents,
        TransferItem[]? items = null,
        byte[]? clientToken = null,
        long cutAfter = long.MaxValue,
        Func<long, bool>? hasRoomFor = null,
        ITransferStorage? storage = null,
        Action<DirectSender, DirectAck>? onAck = null,
        Action<DirectSender>? created = null)
    {
        var (client, agent) = DuplexPipe.Create(cutAfter);
        var memory = storage as MemoryTransferStorage ?? new MemoryTransferStorage();
        var receiver = new DirectReceiver(Token, items ?? Items, storage ?? memory, () => Environment.TickCount64, hasRoomFor);
        var authenticated = false;
        receiver.Authenticated += () => authenticated = true;
        var agentThread = new Thread(() =>
        {
            receiver.Run(agent, r => r.State == DirectReceiverState.Succeeded ? "done" : "failed " + r.Failure);
            agent.Dispose();
        })
        {
            IsBackground = true,
        };
        agentThread.Start();

        var sender = new DirectSender(files, index => contents[index] is { } content ? new MemoryStream(content, writable: false) : throw new IOException("The file is locked."));
        created?.Invoke(sender);
        var acks = new List<DirectAck>();
        sender.Acknowledged += ack =>
        {
            lock (acks)
            {
                acks.Add(ack);
            }

            onAck?.Invoke(sender, ack);
        };
        var outcome = sender.Run(client, clientToken ?? Token);
        client.Dispose();
        Assert.True(agentThread.Join(TimeSpan.FromSeconds(30)), "The agent did not end.");
        return new RunResult(outcome, receiver, memory, acks, authenticated);
    }

    private sealed record RunResult(DirectSendOutcome Outcome, DirectReceiver Receiver, MemoryTransferStorage Storage, List<DirectAck> Acks, bool Authenticated);
}
