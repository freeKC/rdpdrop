using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using UltimateRDP.Core.Direct;
using UltimateRDP.Core.Protocol;
using UltimateRDP.Core.Transfer;
using Xunit.Abstractions;

namespace UltimateRDP.Core.Tests.Direct;

/// <summary>
/// Whole direct transfers of version 2 between a <see cref="DirectSender"/> and a
/// <see cref="DirectReceiver"/> through in memory connections, as the client and the agent
/// run them over TLS, and frames written by hand where the rules of the receiver are tested.
/// </summary>
public class DirectTransferTests(ITestOutputHelper output)
{
    private const int Block = DirectFrames.BlockSize;

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

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(8)]
    public void Files_go_through_one_or_several_connections_with_their_content_and_dates(int connections)
    {
        var a = Data((9 * Block) + 123, 1);
        var b = Data(3, 2);
        var c = Data(0, 3);
        var d = Data(2 * Block, 4);
        var modified = new DateTime(2026, 11, 13, 8, 30, 0, DateTimeKind.Utc);
        TransferFile[] files =
        [
            new("a.bin", a.Length, modified),
            new(@"Folder\sub\b.txt", b.Length),
            new(@"Folder\été ü.txt", c.Length, modified),
            new(@"Folder\d.bin", d.Length),
        ];

        var run = Run(files, [a, b, c, d], connections, startWhen: sender => sender.Stats().Connections == connections);

        Assert.Equal(DirectSendStatus.Succeeded, run.Outcome.Status);
        Assert.True(run.Outcome.Acknowledged);
        Assert.Equal(DropStatus.Ok, run.Outcome.Result!.Status);
        Assert.Equal(a.Length + b.Length + d.Length, run.Outcome.Result.Bytes);
        Assert.Equal("done", run.Outcome.Result.Message);
        Assert.Equal(DirectReceiverState.Succeeded, run.Agent.Receiver.State);
        Assert.Equal(a, run.Agent.Memory.Content("a.bin"));
        Assert.Equal(b, run.Agent.Memory.Content(@"Folder\sub\b.txt"));
        Assert.Equal(c, run.Agent.Memory.Content(@"Folder\été ü.txt"));
        Assert.Equal(d, run.Agent.Memory.Content(@"Folder\d.bin"));
        Assert.Equal(modified, run.Agent.Memory.Completed["a.bin"]);
        Assert.Null(run.Agent.Memory.Completed[@"Folder\sub\b.txt"]);
        Assert.Equal(new[] { "Folder" }, run.Agent.Memory.Directories);
        Assert.Equal(0, run.Agent.Memory.DeleteAllCalls);
        Assert.All(run.Agent.Memory.Files.Values, file => Assert.True(file.Disposed));

        var stats = run.Sender.Stats();
        Assert.Equal(connections, stats.Connections);
        Assert.Equal(connections, run.Agent.Receiver.Connections);
        Assert.Equal(a.Length + b.Length + d.Length, stats.BytesPerConnection.Where(bytes => bytes > 0).Sum());
        Assert.Equal(stats.BytesPerConnection.Where(bytes => bytes > 0), run.Agent.Receiver.BytesPerConnection.Where(bytes => bytes > 0));

        // An acknowledgement at the end of each file, and the bytes only grow.
        Assert.All(Enumerable.Range(0, files.Length), index => Assert.Contains(run.Acks, ack => ack.FileIndex == index && ack.FileBytes == files[index].Size));
        Assert.Equal(a.Length + b.Length + d.Length, run.Acks[^1].TotalBytes);
        Assert.True(run.Acks.Zip(run.Acks.Skip(1)).All(pair => pair.First.TotalBytes <= pair.Second.TotalBytes));
    }

    [Fact]
    public void A_transfer_of_empty_folders_only_succeeds_without_data()
    {
        var run = Run([], [], items: [new TransferItem("Empty", true)]);

        Assert.Equal(DirectSendStatus.Succeeded, run.Outcome.Status);
        Assert.False(run.Outcome.Acknowledged);
        Assert.Equal(new[] { "Empty" }, run.Agent.Memory.Directories);
    }

    [Fact]
    public void Blocks_may_arrive_in_any_order_and_on_any_connection()
    {
        var a = Data((5 * Block) + 100, 1);
        var b = Data(10, 2);
        using var agent = new Agent();
        var (control, reader) = Open(agent, 0);
        var (extra, _) = Open(agent, 1);
        SendList(control, reader, new TransferFile("a.bin", a.Length), new TransferFile(@"Folder\b.bin", b.Length));

        // The trailers first, then the blocks backwards, spread over both connections.
        DirectFrames.Write(control, DirectFrameType.FileTrailer, DirectFrames.FileTrailerPayload(1, SHA256.HashData(b)));
        DirectFrames.Write(control, DirectFrameType.FileTrailer, DirectFrames.FileTrailerPayload(0, SHA256.HashData(a)));
        SendBlock(extra, 1, 0, b);
        long sentOnExtra = 1, sentOnControl = 0;
        for (var block = 5; block >= 0; block--)
        {
            var offset = (long)block * Block;
            var data = a.AsSpan((int)offset, DirectFrames.BlockLength(a.Length, offset));
            if (block % 2 == 0)
            {
                SendBlock(control, 0, offset, data);
                sentOnControl++;
            }
            else
            {
                SendBlock(extra, 0, offset, data);
                sentOnExtra++;
            }
        }

        DirectFrames.Write(extra, DirectFrameType.End, DirectFrames.EndPayload(sentOnExtra));
        DirectFrames.Write(control, DirectFrameType.End, DirectFrames.EndPayload(sentOnControl));
        var result = ReadFinal(reader);
        control.Dispose();
        extra.Dispose();
        agent.Join();

        Assert.Equal(DropStatus.Ok, result.Status);
        Assert.Equal(a, agent.Memory.Content("a.bin"));
        Assert.Equal(b, agent.Memory.Content(@"Folder\b.bin"));
        Assert.Equal(new[] { 3L * Block, (2L * Block) + 110 }, agent.Receiver.BytesPerConnection.Take(2));
    }

    [Fact]
    public void A_block_that_comes_twice_is_a_protocol_error()
    {
        var a = Data(3 * Block, 1);
        using var agent = new Agent();
        var (control, reader) = Open(agent, 0);
        var (extra, _) = Open(agent, 1);
        SendList(control, reader, new TransferFile("a.bin", a.Length));
        SendBlock(control, 0, Block, a.AsSpan(Block, Block));
        SendBlock(extra, 0, 0, a.AsSpan(0, Block));
        SendBlock(extra, 0, Block, a.AsSpan(Block, Block));

        var result = ReadFinal(reader);
        control.Dispose();
        extra.Dispose();
        agent.Join();

        Assert.Equal(DirectFailure.Protocol, result.Failure);
        Assert.Contains("came twice", agent.Receiver.FailureDetail, StringComparison.Ordinal);
        Assert.Equal(1, agent.Memory.DeleteAllCalls);
    }

    [Theory]
    [InlineData(0, 100, Block)]
    [InlineData(0, 3L * Block, Block)]
    [InlineData(0, Block, Block - 1)]
    [InlineData(0, 2L * Block, 10)]
    [InlineData(5, 0, Block)]
    public void A_misaligned_or_out_of_range_block_is_a_protocol_error(int fileIndex, long offset, int length)
    {
        using var agent = new Agent();
        var (control, reader) = Open(agent, 0);
        SendList(control, reader, new TransferFile("a.bin", (2L * Block) + 20));
        SendBlock(control, fileIndex, offset, new byte[length]);

        var result = ReadFinal(reader);
        control.Dispose();
        agent.Join();

        Assert.Equal(DirectFailure.Protocol, result.Failure);
        Assert.Equal(DirectReceiverState.Failed, agent.Receiver.State);
    }

    [Fact]
    public void A_block_that_never_comes_fails_as_incomplete_once_every_connection_ended()
    {
        var a = Data(3 * Block, 1);
        using var agent = new Agent();
        var (control, reader) = Open(agent, 0);
        var (extra, _) = Open(agent, 1);
        SendList(control, reader, new TransferFile("a.bin", a.Length));
        SendBlock(control, 0, 0, a.AsSpan(0, Block));
        SendBlock(extra, 0, 2L * Block, a.AsSpan(2 * Block, Block));
        DirectFrames.Write(control, DirectFrameType.FileTrailer, DirectFrames.FileTrailerPayload(0, SHA256.HashData(a)));
        DirectFrames.Write(extra, DirectFrameType.End, DirectFrames.EndPayload(1));
        DirectFrames.Write(control, DirectFrameType.End, DirectFrames.EndPayload(1));

        var result = ReadFinal(reader);
        control.Dispose();
        extra.Dispose();
        agent.Join();

        Assert.Equal(DirectFailure.Incomplete, result.Failure);
        Assert.Equal("a.bin", agent.Receiver.FailurePath);
        Assert.Contains("Block 1 of the file never came", agent.Receiver.FailureDetail, StringComparison.Ordinal);
        Assert.Equal(1, agent.Memory.DeleteAllCalls);
    }

    [Fact]
    public void An_end_frame_that_counts_other_blocks_is_a_protocol_error()
    {
        using var agent = new Agent();
        var (control, reader) = Open(agent, 0);
        SendList(control, reader, new TransferFile("a.bin", 10));
        SendBlock(control, 0, 0, new byte[10]);
        DirectFrames.Write(control, DirectFrameType.End, DirectFrames.EndPayload(2));

        var result = ReadFinal(reader);
        control.Dispose();
        agent.Join();

        Assert.Equal(DirectFailure.Protocol, result.Failure);
        Assert.Contains("carried 2 block(s); 1 arrived", agent.Receiver.FailureDetail, StringComparison.Ordinal);
    }

    [Fact]
    public void A_wrong_checksum_fails_the_transfer_and_deletes_what_was_written()
    {
        var a = Data(3000, 1);
        using var agent = new Agent();
        var (control, reader) = Open(agent, 0);
        SendList(control, reader, new TransferFile("a.bin", a.Length));
        SendBlock(control, 0, 0, a);
        var wrong = SHA256.HashData(a);
        wrong[0] ^= 1;
        DirectFrames.Write(control, DirectFrameType.FileTrailer, DirectFrames.FileTrailerPayload(0, wrong));

        var result = ReadFinal(reader);
        control.Dispose();
        agent.Join();

        Assert.Equal(DropStatus.Error, result.Status);
        Assert.Equal(DirectFailure.HashMismatch, result.Failure);
        Assert.Equal("failed HashMismatch", result.Message);
        Assert.Equal("a.bin", agent.Receiver.FailurePath);
        Assert.Equal(1, agent.Memory.DeleteAllCalls);
        Assert.Empty(agent.Memory.Completed);
    }

    [Fact]
    public void A_data_frame_before_the_go_frame_is_a_protocol_error()
    {
        using var agent = new Agent();
        var (control, reader) = Open(agent, 0);
        DirectFrames.Write(control, DirectFrameType.Begin, DirectFrames.BeginPayload(1, 10));
        DirectFrames.Write(control, DirectFrameType.FileHeader, DirectFrames.FileHeaderPayload(0, new TransferFile("a.bin", 10)));
        SendBlock(control, 0, 0, new byte[10]);

        var result = ReadFinal(reader);
        control.Dispose();
        agent.Join();

        Assert.Equal(DirectFailure.Protocol, result.Failure);
        Assert.Equal(1, agent.Memory.DeleteAllCalls);
    }

    [Fact]
    public void A_frame_longer_than_allowed_breaks_the_connection()
    {
        using var agent = new Agent();
        var (control, reader) = Open(agent, 0);
        var header = new byte[DirectFrames.HeaderLength];
        DirectFrames.WriteHeader(header, DirectFrameType.Data, DirectFrames.MaxPayloadLength + 1);
        control.Write(header);

        var result = ReadFinal(reader);
        control.Dispose();
        agent.Join();

        Assert.Equal(DirectFailure.Protocol, result.Failure);
        Assert.Contains("more than", agent.Receiver.FailureDetail, StringComparison.Ordinal);
    }

    [Fact]
    public void A_token_of_version_1_from_a_client_0_6_gets_no_answer()
    {
        using var agent = new Agent();
        var client = agent.Connect();
        var version1 = new byte[1 + DirectFrames.TokenLength];
        version1[0] = 1;
        Token.CopyTo(version1, 1);
        DirectFrames.Write(client, DirectFrameType.Token, version1);

        Assert.False(new DirectFrameReader(client).TryRead(out _, out _));
        client.Dispose();
        agent.JoinThreads();
        var refusal = Assert.Single(agent.Results);
        Assert.Equal(-1, refusal.Index);
        Assert.Contains("version 1", refusal.Refusal, StringComparison.Ordinal);
        Assert.Equal(DirectReceiverState.AwaitingControl, agent.Receiver.State);
    }

    [Fact]
    public void A_wrong_token_or_an_index_beyond_the_offer_gets_no_answer()
    {
        using var agent = new Agent(maxConnections: 2);
        var wrong = agent.Connect();
        DirectFrames.Write(wrong, DirectFrameType.Token, DirectFrames.TokenPayload(RandomNumberGenerator.GetBytes(DirectFrames.TokenLength), 0));
        Assert.False(new DirectFrameReader(wrong).TryRead(out _, out _));
        var (control, _) = Open(agent, 0);
        var beyond = agent.Connect();
        DirectFrames.Write(beyond, DirectFrameType.Token, DirectFrames.TokenPayload(Token, 2));
        Assert.False(new DirectFrameReader(beyond).TryRead(out _, out _));

        wrong.Dispose();
        beyond.Dispose();
        agent.Receiver.Close("the test ends");
        control.Dispose();
        agent.Join();
        Assert.Contains(agent.Results, result => result.Refusal == "The token is not the one of the offer.");
        Assert.Contains(agent.Results, result => result.Refusal?.Contains("beyond the 2 connection(s)", StringComparison.Ordinal) == true);
    }

    [Fact]
    public void A_replayed_index_is_refused()
    {
        using var agent = new Agent();
        var (control, _) = Open(agent, 0);
        var (extra, _) = Open(agent, 1);
        var replay = agent.Connect();
        DirectFrames.Write(replay, DirectFrameType.Token, DirectFrames.TokenPayload(Token, 1));

        Assert.False(new DirectFrameReader(replay).TryRead(out _, out _));
        var zero = agent.Connect();
        DirectFrames.Write(zero, DirectFrameType.Token, DirectFrames.TokenPayload(Token, 0));
        Assert.False(new DirectFrameReader(zero).TryRead(out _, out _));
        replay.Dispose();
        zero.Dispose();
        agent.Receiver.Close("the test ends");
        control.Dispose();
        extra.Dispose();
        agent.Join();
        Assert.Equal(2, agent.Results.Count(result => result.Refusal?.Contains("was already used", StringComparison.Ordinal) == true));
        Assert.Equal(2, agent.Receiver.Connections);
    }

    [Fact]
    public void An_index_before_connection_0_is_refused()
    {
        using var agent = new Agent();
        var early = agent.Connect();
        DirectFrames.Write(early, DirectFrameType.Token, DirectFrames.TokenPayload(Token, 3));

        Assert.False(new DirectFrameReader(early).TryRead(out _, out _));
        early.Dispose();
        agent.JoinThreads();
        Assert.Contains("came before connection 0", Assert.Single(agent.Results).Refusal, StringComparison.Ordinal);

        // Connection 3 is still free once connection 0 came.
        var (control, _) = Open(agent, 0);
        var (third, _) = Open(agent, 3);
        agent.Receiver.Close("the test ends");
        control.Dispose();
        third.Dispose();
        agent.Join();
        Assert.Equal(2, agent.Receiver.Connections);
    }

    [Fact]
    public void A_cancel_at_the_first_acknowledgement_ends_both_sides_as_cancelled()
    {
        var a = Data(30 * Block, 1);
        var hold = new HoldingStream(a, 16L * Block);
        var run = Run([new("a.bin", a.Length)], [a], connections: 4, open: _ => hold, onAck: (sender, _) => sender.Cancel("The user cancelled the transfer."));
        hold.Release();

        Assert.Equal(DirectSendStatus.Cancelled, run.Outcome.Status);
        Assert.True(run.Outcome.Acknowledged);
        Assert.Equal(DropStatus.Cancelled, run.Outcome.Result!.Status);
        Assert.Equal(DirectReceiverState.Cancelled, run.Agent.Receiver.State);
        Assert.Equal("The user cancelled the transfer.", run.Agent.Receiver.CancelReason);
        Assert.False(run.Agent.Receiver.CancelledThroughChannel);
        Assert.Equal(1, run.Agent.Memory.DeleteAllCalls);
    }

    [Fact]
    public void A_cancel_before_the_files_are_listed_tells_the_agent()
    {
        var a = Data(100, 1);
        var run = Run([new("a.bin", a.Length)], [a], created: sender => sender.Cancel("The tab of the session was closed."));

        Assert.Equal(DirectSendStatus.Cancelled, run.Outcome.Status);
        Assert.Equal(DirectReceiverState.Cancelled, run.Agent.Receiver.State);
        Assert.Equal("The tab of the session was closed.", run.Agent.Receiver.CancelReason);
    }

    [Fact]
    public void A_cancel_while_the_reader_is_blocked_in_a_file_read_returns_within_about_a_second()
    {
        var a = Data(4 * Block, 1);
        var blocking = new BlockingStream(a);
        var lines = new ConcurrentQueue<string>();
        using var agent = new Agent();
        var sender = new DirectSender(() => [new TransferFile("a.bin", a.Length)], _ => blocking, 2, () => Environment.TickCount64) { BytesPerConnection = Block };
        sender.Info += lines.Enqueue;
        var watch = new Stopwatch();
        var canceller = new Thread(() =>
        {
            blocking.Entered.Wait();
            watch.Start();
            sender.Cancel("The user cancelled the transfer.");
        });
        canceller.Start();

        var outcome = sender.Run(new DirectConnection(agent.Connect()), Token, (_, _) => new DirectConnection(agent.Connect()));
        watch.Stop();
        output.WriteLine($"Run returned {watch.ElapsedMilliseconds} ms after the cancel.");
        agent.Join();

        Assert.Equal(DirectSendStatus.Cancelled, outcome.Status);
        // About a second at most by design: the bound leaves room for a far slower machine.
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(8), $"Run returned {watch.ElapsedMilliseconds} ms after the cancel.");
        Assert.Equal(DirectReceiverState.Cancelled, agent.Receiver.State);
        Assert.Contains(lines, line => line.StartsWith("The reader is still reading a.bin", StringComparison.Ordinal));

        // The read returns long after: the reader says so and sends nothing.
        Thread.Sleep(800);
        blocking.Release();
        var late = Stopwatch.StartNew();
        while (!lines.Any(line => line.Contains("after the connections closed", StringComparison.Ordinal)) && late.Elapsed < TimeSpan.FromSeconds(30))
        {
            Thread.Sleep(20);
        }

        Assert.Contains(lines, line => line.StartsWith("The read of a.bin", StringComparison.Ordinal) && line.Contains("after the connections closed", StringComparison.Ordinal));
    }

    [Fact]
    public void A_cancel_while_a_writer_is_blocked_in_a_socket_write_returns_within_about_a_second()
    {
        var a = Data(20 * Block, 1);
        var (client, agentEnd) = DuplexPipe.Create(capacity: 64 * 1024);
        var listed = new ManualResetEventSlim(false);

        // An agent that accepts the token and the list, says go, then never reads again.
        var stalled = new Thread(() =>
        {
            var reader = new DirectFrameReader(agentEnd);
            reader.TryRead(out _, out _);
            DirectFrames.Write(agentEnd, DirectFrameType.Ready, []);
            while (reader.TryRead(out var type, out _) && type != DirectFrameType.ListEnd)
            {
            }

            DirectFrames.Write(agentEnd, DirectFrameType.Go, []);
            listed.Set();
        });
        stalled.Start();
        var lines = new ConcurrentQueue<string>();
        var sender = new DirectSender(() => [new TransferFile("a.bin", a.Length)], _ => new MemoryStream(a, writable: false), 1, () => Environment.TickCount64);
        sender.Info += lines.Enqueue;
        var watch = new Stopwatch();
        var blocked = false;
        var canceller = new Thread(() =>
        {
            // The cancel comes once the writer is blocked in a write on the full connection.
            listed.Wait();
            blocked = SpinWait.SpinUntil(() => ((DuplexPipe.End)client).WriteBlocked, TimeSpan.FromSeconds(30));
            watch.Start();
            sender.Cancel("The user cancelled the transfer.");
        });
        canceller.Start();

        var outcome = sender.Run(new DirectConnection(client), Token, null);
        watch.Stop();
        output.WriteLine($"Run returned {watch.ElapsedMilliseconds} ms after the cancel.");
        agentEnd.Dispose();
        stalled.Join();

        Assert.True(blocked, "The writer never blocked.");
        Assert.Equal(DirectSendStatus.Cancelled, outcome.Status);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(8), $"Run returned {watch.ElapsedMilliseconds} ms after the cancel.");
        Assert.Contains(lines, line => line.StartsWith("The cancel frame could not be sent within 1 s", StringComparison.Ordinal));
    }

    [Fact]
    public void A_cancel_through_the_channel_ends_the_transfer_as_cancelled()
    {
        var a = Data(30 * Block, 1);
        using var agent = new Agent();
        var hold = new HoldingStream(a, 16L * Block);
        var run = Run([new("a.bin", a.Length)], [a], connections: 2, agent: agent, open: _ => hold, onAck: (_, _) => agent.Receiver.ClientCancelled("The user cancelled the transfer."));
        hold.Release();

        Assert.Equal(DirectSendStatus.AgentCancelled, run.Outcome.Status);
        Assert.Equal(DirectReceiverState.Cancelled, agent.Receiver.State);
        Assert.True(agent.Receiver.CancelledThroughChannel);
        Assert.Equal(1, agent.Memory.DeleteAllCalls);
    }

    [Fact]
    public void Connections_that_closed_just_before_a_cancel_through_the_channel_count_as_cancelled()
    {
        var a = Data(12 * Block, 1);
        using var agent = new Agent();
        var run = Run([new("a.bin", a.Length)], [a], agent: agent, cutControlAfter: 10L * Block);

        Assert.Equal(DirectSendStatus.Broken, run.Outcome.Status);
        Assert.Equal(DirectFailure.Broken, agent.Receiver.Failure);
        agent.Receiver.ClientCancelled("The user cancelled the transfer.");
        Assert.Equal(DirectReceiverState.Cancelled, agent.Receiver.State);
        Assert.Equal(DirectFailure.None, agent.Receiver.Failure);
    }

    [Fact]
    public void A_close_by_the_agent_stays_a_close_by_the_agent()
    {
        var a = Data(30 * Block, 1);
        using var agent = new Agent();
        var hold = new HoldingStream(a, 16L * Block);
        var run = Run([new("a.bin", a.Length)], [a], connections: 2, agent: agent, open: _ => hold, onAck: (_, _) => agent.Receiver.Close("the agent stops"));
        hold.Release();
        agent.Receiver.ClientCancelled("The user cancelled the transfer.");

        Assert.Equal(DirectSendStatus.Broken, run.Outcome.Status);
        Assert.True(run.Outcome.Acknowledged);
        Assert.Equal(DirectReceiverState.Failed, agent.Receiver.State);
        Assert.True(agent.Receiver.ClosedByAgent);
        Assert.Equal("Closed by the agent: the agent stops", agent.Receiver.FailureDetail);
    }

    [Fact]
    public void A_connection_cut_before_the_first_acknowledgement_is_broken_without_acknowledgement()
    {
        var a = Data(5 * Block, 1);
        var run = Run([new("a.bin", a.Length)], [a], cutControlAfter: 2L * Block);

        Assert.Equal(DirectSendStatus.Broken, run.Outcome.Status);
        Assert.False(run.Outcome.Acknowledged);
        Assert.Equal(DirectReceiverState.Failed, run.Agent.Receiver.State);
        Assert.Equal(DirectFailure.Broken, run.Agent.Receiver.Failure);
        Assert.False(run.Agent.Receiver.FinalWasSent);
        Assert.Equal(1, run.Agent.Memory.DeleteAllCalls);
    }

    [Fact]
    public void One_connection_breaking_in_the_middle_fails_the_whole_transfer()
    {
        var a = Data(64 * Block, 1);
        using var agent = new Agent();
        var run = Run(
            [new("a.bin", a.Length)],
            [a],
            connections: 4,
            agent: agent,
            openExtra: (index, _) => new DirectConnection(agent.Connect(index == 2 ? 3L * Block : long.MaxValue)),
            startWhen: sender => sender.Stats().Connections == 4);

        Assert.Equal(DirectSendStatus.Broken, run.Outcome.Status);
        Assert.Equal(DirectReceiverState.Failed, agent.Receiver.State);
        Assert.Equal(DirectFailure.Broken, agent.Receiver.Failure);
        Assert.Contains("Connection 2 was lost", agent.Receiver.FailureDetail, StringComparison.Ordinal);
        Assert.Equal(1, agent.Memory.DeleteAllCalls);
        Assert.Empty(agent.Memory.Files);
    }

    [Fact]
    public void An_extra_connection_that_cannot_be_opened_is_not_an_error()
    {
        var a = Data(8 * Block, 1);
        using var agent = new Agent();
        var lines = new ConcurrentQueue<string>();
        var run = Run(
            [new("a.bin", a.Length)],
            [a],
            connections: 4,
            agent: agent,
            created: sender => sender.Info += lines.Enqueue,
            openExtra: (index, _) => index switch
            {
                2 => throw new IOException("did not answer within 3 s"),
                3 => null,
                _ => new DirectConnection(agent.Connect()),
            },
            startWhen: sender => sender.Stats().Connections == 2 && lines.Contains("Connection 2 is not used: did not answer within 3 s"));

        Assert.Equal(DirectSendStatus.Succeeded, run.Outcome.Status);
        Assert.Equal(a, agent.Memory.Content("a.bin"));
        Assert.Equal(2, run.Sender.Stats().Connections);
        Assert.Contains("Connection 2 is not used: did not answer within 3 s", lines);
    }

    [Fact]
    public void An_extra_connection_that_closes_before_any_block_is_dropped_and_the_transfer_goes_on()
    {
        var a = Data(10, 1);
        using var agent = new Agent();
        var dropped = new ConcurrentQueue<int>();
        agent.Receiver.ConnectionDropped += (index, _) => dropped.Enqueue(index);
        var (control, reader) = Open(agent, 0);
        var (extra, _) = Open(agent, 1);
        extra.Dispose();
        Assert.True(SpinWait.SpinUntil(() => !dropped.IsEmpty, TimeSpan.FromSeconds(30)), "The agent did not see the connection close.");
        SendList(control, reader, new TransferFile("a.bin", a.Length));
        SendBlock(control, 0, 0, a);
        DirectFrames.Write(control, DirectFrameType.FileTrailer, DirectFrames.FileTrailerPayload(0, SHA256.HashData(a)));
        DirectFrames.Write(control, DirectFrameType.End, DirectFrames.EndPayload(1));

        var result = ReadFinal(reader);
        control.Dispose();
        agent.Join();

        Assert.Equal(DropStatus.Ok, result.Status);
        Assert.Equal(new[] { 1 }, dropped);
    }

    [Fact]
    public void Many_small_files_and_empty_files_go_through_four_connections()
    {
        var random = new Random(5);
        var files = new List<TransferFile> { new("a.bin", 1000) };
        var contents = new List<byte[]?> { Data(1000, 0) };
        for (var i = 0; i < 400; i++)
        {
            var size = i % 5 == 0 ? 0 : random.Next(1, 5000);
            files.Add(new TransferFile($@"Folder\f{i:000}.txt", size));
            contents.Add(Data(size, i + 1));
        }

        var run = Run([.. files], [.. contents], connections: 4, bytesPerConnection: 1024, startWhen: sender => sender.Stats().Connections == 4);

        Assert.Equal(DirectSendStatus.Succeeded, run.Outcome.Status);
        Assert.Equal(4, run.Sender.Stats().Connections);
        Assert.Equal(401, run.Agent.Memory.Completed.Count);
        for (var i = 0; i < files.Count; i++)
        {
            Assert.Equal(contents[i], run.Agent.Memory.Content(files[i].RelativePath));
        }

        Assert.Equal(401, run.Agent.Receiver.ReceivedFiles);
    }

    [Fact]
    public void Two_hundred_megabytes_go_through_eight_connections_in_about_two_seconds()
    {
        const long Size = 200_000_000;
        var block = Data(Block, 7);
        var storage = new StreamingDirectStorage();
        using var agent = new Agent(storage: storage);
        var watch = Stopwatch.StartNew();
        var run = Run([new("a.bin", Size)], [], connections: 8, agent: agent, bytesPerConnection: 16L * 1024 * 1024, open: _ => new RepeatingStream(block, Size), startWhen: sender => sender.Stats().Connections == 8);
        watch.Stop();
        var stats = run.Sender.Stats();
        output.WriteLine($"200 MB over {stats.Connections} connections in {watch.ElapsedMilliseconds} ms; the agent read {agent.Receiver.ReadSeconds:0.00} s, wrote {agent.Receiver.WriteSeconds:0.00} s, hashed {agent.Receiver.HashSeconds:0.00} s; the client read {stats.ReadSeconds:0.00} s, wrote {stats.WriteSeconds:0.00} s.");

        Assert.Equal(DirectSendStatus.Succeeded, run.Outcome.Status);
        Assert.Equal(8, stats.Connections);
        Assert.Equal(Size, storage.Written);
        // About 0.2 s on the development computer: the bound holds on a machine far slower.
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(20), $"200 MB took {watch.ElapsedMilliseconds} ms.");
    }

    [Fact]
    public void The_pacer_limits_all_the_connections_together()
    {
        var a = Data(20 * Block, 1);
        using var agent = new Agent(pacer: new DirectPacer(40_000_000, () => Environment.TickCount64));
        var watch = Stopwatch.StartNew();
        var run = Run([new("a.bin", a.Length)], [a], connections: 4, agent: agent);
        watch.Stop();
        output.WriteLine($"20 MiB at 40 MB/s over 4 connections in {watch.ElapsedMilliseconds} ms, {agent.Receiver.PacedSeconds:0.00} s of waits.");

        Assert.Equal(DirectSendStatus.Succeeded, run.Outcome.Status);
        Assert.True(watch.Elapsed > TimeSpan.FromMilliseconds(350), $"20 MiB took {watch.ElapsedMilliseconds} ms.");
        Assert.True(agent.Receiver.PacedSeconds > 0);
    }

    [Fact]
    public void A_path_that_leaves_the_items_is_refused_before_any_data()
    {
        var a = Data(10, 1);
        var run = Run([new(@"a.bin\..\..\evil.txt", a.Length)], [a]);

        Assert.Equal(DirectSendStatus.AgentFailed, run.Outcome.Status);
        Assert.Equal(DirectFailure.BadName, run.Outcome.Result!.Failure);
        Assert.Empty(run.Agent.Memory.Files);
    }

    [Fact]
    public void A_dropped_file_missing_from_the_list_is_refused_before_any_data()
    {
        var b = Data(10, 1);
        var run = Run([new(@"Folder\b.txt", b.Length)], [b]);

        Assert.Equal(DirectSendStatus.AgentFailed, run.Outcome.Status);
        Assert.Equal(DirectFailure.Incomplete, run.Outcome.Result!.Failure);
        Assert.Equal("a.bin", run.Agent.Receiver.FailurePath);
        Assert.Empty(run.Agent.Memory.Files);
    }

    [Fact]
    public void Announced_bytes_that_do_not_fit_are_refused_at_the_begin_frame()
    {
        var a = Data(5000, 1);
        using var agent = new Agent(hasRoomFor: bytes => bytes < 1000);
        var run = Run([new("a.bin", a.Length)], [a], agent: agent);

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
        Assert.Equal(DirectReceiverState.Cancelled, run.Agent.Receiver.State);
    }

    [Fact]
    public void A_file_that_cannot_be_opened_cancels_the_transfer()
    {
        var run = Run([new("a.bin", 10)], [null]);

        Assert.Equal(DirectSendStatus.LocalFailure, run.Outcome.Status);
        Assert.False(run.Outcome.FileChanged);
        Assert.Equal(DirectReceiverState.Cancelled, run.Agent.Receiver.State);
    }

    [Fact]
    public void Items_that_cannot_be_listed_after_the_token_cancel_the_transfer()
    {
        using var agent = new Agent();
        var listed = false;
        var sender = new DirectSender(
            () =>
            {
                listed = true;
                return null;
            },
            _ => throw new InvalidOperationException("Nothing to open."),
            1,
            () => Environment.TickCount64);

        var outcome = sender.Run(new DirectConnection(agent.Connect()), Token, null);
        agent.Join();

        Assert.True(listed);
        Assert.Equal(DirectSendStatus.LocalFailure, outcome.Status);
        Assert.Null(outcome.Path);
        Assert.Equal(DirectReceiverState.Cancelled, agent.Receiver.State);
        Assert.Equal(DropStatus.Cancelled, outcome.Result!.Status);
    }

    [Fact]
    public void A_storage_that_cannot_create_the_files_fails_the_transfer()
    {
        var a = Data(100, 1);
        using var agent = new Agent();
        agent.Memory.FailOnCreate = true;
        var run = Run([new("a.bin", a.Length)], [a], agent: agent);

        Assert.Equal(DirectSendStatus.AgentFailed, run.Outcome.Status);
        Assert.Equal(DirectFailure.WriteFailed, run.Outcome.Result!.Failure);
    }

    [Fact]
    public void A_cancel_through_the_channel_stops_the_transfer_while_the_files_are_still_being_deleted()
    {
        var a = Data(30 * Block, 1);
        var storage = new SlowDeleteStorage();
        using var agent = new Agent(storage: storage);
        var cancelled = 0;
        long returned = -1;
        var stopped = false;
        var endedAtStop = true;

        // The deletion goes on once the test saw the transfer stop, and some time after.
        var releaser = new Thread(() =>
        {
            storage.Entered.Wait(TimeSpan.FromSeconds(30));
            Thread.Sleep(300);
            storage.Released.Set();
        });
        releaser.Start();
        var hold = new HoldingStream(a, 16L * Block);
        var run = Run([new("a.bin", a.Length)], [a], connections: 4, agent: agent, open: _ => hold, onAck: (_, _) =>
        {
            if (Interlocked.Exchange(ref cancelled, 1) != 0)
            {
                return;
            }

            var watch = Stopwatch.StartNew();
            agent.Receiver.ClientCancelled("The user cancelled the transfer.");
            returned = watch.ElapsedMilliseconds;
            stopped = agent.Receiver.HasStopped;
            endedAtStop = agent.Receiver.HasEnded;
        });
        hold.Release();
        releaser.Join();
        output.WriteLine($"ClientCancelled returned after {returned} ms; the files took {agent.Receiver.CleanUpTimes}.");

        Assert.Equal(DirectSendStatus.AgentCancelled, run.Outcome.Status);

        // The call itself does not wait for the files: it returns stopped while they are held.
        Assert.True(stopped);
        Assert.False(endedAtStop);
        Assert.InRange(returned, 0, 5000);
        Assert.True(agent.Receiver.HasEnded);
        Assert.True(agent.Receiver.CleanUpTimes!.DeleteMilliseconds >= 250);
        Assert.Equal(1, storage.Inner.DeleteAllCalls);
    }

    [Fact]
    public void A_lost_connection_stops_the_transfer_while_the_files_are_still_being_deleted()
    {
        var a = Data(12 * Block, 1);
        var storage = new SlowDeleteStorage();
        using var agent = new Agent(storage: storage);
        var sender = new DirectSender(() => [new TransferFile("a.bin", a.Length)], _ => new MemoryStream(a, writable: false), 1, () => Environment.TickCount64);

        var outcome = sender.Run(new DirectConnection(agent.Connect(10L * Block)), Token, null);
        Assert.True(agent.Receiver.WaitStopped(TimeSpan.FromSeconds(30)));
        Assert.True(storage.Entered.Wait(TimeSpan.FromSeconds(30)));
        var endedWhenStopped = agent.Receiver.HasEnded;
        Thread.Sleep(300);
        storage.Released.Set();
        agent.Join();

        Assert.Equal(DirectSendStatus.Broken, outcome.Status);
        Assert.Equal(DirectReceiverState.Failed, agent.Receiver.State);
        Assert.False(endedWhenStopped);
        Assert.True(agent.Receiver.CleanUpTimes!.DeleteMilliseconds >= 250);
    }

    [Fact]
    public void A_transfer_that_succeeds_ends_without_a_deletion()
    {
        var a = Data(3 * Block, 1);
        var storage = new SlowDeleteStorage();
        using var agent = new Agent(storage: storage);

        // The deletion is never released: a success that waited for one would not end.
        var run = Run([new("a.bin", a.Length)], [a], agent: agent);
        storage.Released.Set();

        Assert.Equal(DirectSendStatus.Succeeded, run.Outcome.Status);
        Assert.False(storage.Entered.IsSet);
        Assert.Null(agent.Receiver.CleanUpTimes!.DeleteMilliseconds);
        Assert.Equal(0, storage.Inner.DeleteAllCalls);
        Assert.Equal(a, storage.Inner.Content("a.bin"));
    }

    [Fact]
    public void A_file_slow_to_open_is_told_after_the_delay_and_until_its_first_block()
    {
        var a = Data(2 * Block, 1);
        var events = new ConcurrentQueue<string>();
        var released = new ManualResetEventSlim(false);
        var held = false;
        var run = Run(
            [new("a.bin", a.Length)],
            [a],
            fileWaitDelay: TimeSpan.FromMilliseconds(50),
            created: sender => sender.FileWaiting += path =>
            {
                events.Enqueue(path ?? "(end)");
                if (path is not null)
                {
                    released.Set();
                }
            },
            open: _ =>
            {
                // As Windows checking a large executable: the open lasts until the notice came.
                held = released.Wait(TimeSpan.FromSeconds(30));
                events.Enqueue("opened");
                return new MemoryStream(a, writable: false);
            });
        output.WriteLine(string.Join("; ", events));

        Assert.True(held, "The notice never came.");
        Assert.Equal(DirectSendStatus.Succeeded, run.Outcome.Status);
        Assert.Equal(new[] { "a.bin", "opened", "(end)" }, events);
    }

    [Fact]
    public void A_file_that_opens_at_once_gets_no_notice()
    {
        var a = Data(2 * Block, 1);
        var events = new ConcurrentQueue<string?>();
        var run = Run([new("a.bin", a.Length)], [a], created: sender => sender.FileWaiting += events.Enqueue);

        Assert.Equal(DirectSendStatus.Succeeded, run.Outcome.Status);
        Assert.Empty(events);
    }

    private static (Stream Stream, DirectFrameReader Reader) Open(Agent agent, byte index)
    {
        var stream = agent.Connect();
        DirectFrames.Write(stream, DirectFrameType.Token, DirectFrames.TokenPayload(Token, index));
        var reader = new DirectFrameReader(stream);
        Assert.True(reader.TryRead(out var type, out _));
        Assert.Equal(DirectFrameType.Ready, type);
        return (stream, reader);
    }

    private static void SendList(Stream control, DirectFrameReader reader, params TransferFile[] files)
    {
        DirectFrames.Write(control, DirectFrameType.Begin, DirectFrames.BeginPayload(files.Length, files.Sum(file => file.Size)));
        for (var i = 0; i < files.Length; i++)
        {
            DirectFrames.Write(control, DirectFrameType.FileHeader, DirectFrames.FileHeaderPayload(i, files[i]));
        }

        DirectFrames.Write(control, DirectFrameType.ListEnd, []);
        Assert.True(reader.TryRead(out var type, out _));
        Assert.Equal(DirectFrameType.Go, type);
    }

    private static void SendBlock(Stream stream, int fileIndex, long offset, ReadOnlySpan<byte> data)
    {
        var frame = new byte[DirectFrames.DataFrameOverhead + data.Length];
        DirectFrames.WriteDataHeader(frame, fileIndex, offset, data.Length);
        data.CopyTo(frame.AsSpan(DirectFrames.DataFrameOverhead));
        stream.Write(frame);
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
        int connections = 1,
        TransferItem[]? items = null,
        Agent? agent = null,
        long bytesPerConnection = Block,
        long cutControlAfter = long.MaxValue,
        Action<DirectSender, DirectAck>? onAck = null,
        Action<DirectSender>? created = null,
        Func<int, CancellationToken, DirectConnection?>? openExtra = null,
        Func<int, Stream>? open = null,
        Func<DirectSender, bool>? startWhen = null,
        TimeSpan? fileWaitDelay = null)
    {
        agent ??= new Agent(items: items);
        DirectSender? createdSender = null;
        var started = true;
        Func<int, Stream> openFile = open ?? (index => contents[index] is { } content ? new MemoryStream(content, writable: false) : throw new IOException("The file is locked."));
        var sender = new DirectSender(
            () => files,
            index =>
            {
                // The data waits for the condition of the test, such as every connection being
                // open, so that a slow machine does not end the transfer before it holds.
                if (index == 0 && startWhen is not null)
                {
                    started = SpinWait.SpinUntil(() => startWhen(createdSender!), TimeSpan.FromSeconds(30));
                }

                return openFile(index);
            },
            connections,
            () => Environment.TickCount64)
        {
            BytesPerConnection = bytesPerConnection,
            FileWaitDelay = fileWaitDelay ?? DirectSender.FileWaitNotice,
        };
        createdSender = sender;
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
        var outcome = sender.Run(new DirectConnection(agent.Connect(cutControlAfter)), Token, openExtra ?? ((_, _) => new DirectConnection(agent.Connect())));
        agent.Join();
        Assert.True(started, "The condition of the test before the first file never held.");
        return new RunResult(outcome, agent, sender, acks);
    }

    private sealed record RunResult(DirectSendOutcome Outcome, Agent Agent, DirectSender Sender, List<DirectAck> Acks);

    /// <summary>A receiver and the threads that serve its connections, as the listener of the agent runs them.</summary>
    private sealed class Agent : IDisposable
    {
        private readonly List<Thread> _threads = [];

        public Agent(TransferItem[]? items = null, IDirectStorage? storage = null, int maxConnections = 8, Func<long, bool>? hasRoomFor = null, DirectPacer? pacer = null)
        {
            Memory = new MemoryDirectStorage();
            Receiver = new DirectReceiver(
                Token,
                maxConnections,
                items ?? Items,
                storage ?? Memory,
                () => Environment.TickCount64,
                receiver => receiver.State == DirectReceiverState.Succeeded ? "done" : "failed " + receiver.Failure,
                hasRoomFor,
                pacer);
        }

        public DirectReceiver Receiver { get; }

        public MemoryDirectStorage Memory { get; }

        public ConcurrentQueue<DirectServeResult> Results { get; } = new();

        /// <summary>A new connection, served on a thread of its own; the agent closes its end when the receiver is done with it.</summary>
        public Stream Connect(long cutClientToAgentAfter = long.MaxValue)
        {
            var (client, agent) = DuplexPipe.Create(cutClientToAgentAfter);
            var thread = new Thread(() =>
            {
                Results.Enqueue(Receiver.Serve(agent));
                agent.Dispose();
            })
            {
                IsBackground = true,
            };
            lock (_threads)
            {
                _threads.Add(thread);
            }

            thread.Start();
            return client;
        }

        /// <summary>Waits for the end of the transfer and of every connection.</summary>
        public void Join()
        {
            if (Receiver.HasControl)
            {
                Assert.True(Receiver.WaitEnded(TimeSpan.FromSeconds(30)), "The receiver did not end.");
            }

            JoinThreads();
        }

        public void JoinThreads()
        {
            Thread[] threads;
            lock (_threads)
            {
                threads = [.. _threads];
            }

            foreach (var thread in threads)
            {
                Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "A connection of the agent did not end.");
            }
        }

        public void Dispose()
        {
            if (!Receiver.IsFinished)
            {
                Receiver.Close("the test ends");
            }
        }
    }
}
