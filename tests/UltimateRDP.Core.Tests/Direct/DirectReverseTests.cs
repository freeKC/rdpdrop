using System.Collections.Concurrent;
using System.Security.Cryptography;
using UltimateRDP.Core.Direct;
using UltimateRDP.Core.Protocol;
using UltimateRDP.Core.Transfer;

namespace UltimateRDP.Core.Tests.Direct;

/// <summary>
/// The files that the session sends to the client since 0.9.0: the client opens the
/// connections with token frames of the receiving role and runs a <see cref="DirectReceiver"/>,
/// the agent checks them with a <see cref="DirectTokenGate"/> and runs a <see cref="DirectSender"/>
/// on the connections it accepted. In memory, without races against real time.
/// </summary>
public class DirectReverseTests
{
    private const int Block = DirectFrames.BlockSize;

    private static readonly byte[] Token = RandomNumberGenerator.GetBytes(DirectFrames.TokenLength);

    private static readonly TransferItem[] Items =
    [
        new TransferItem("report.pdf", false),
        new TransferItem("Photos", true),
    ];

    private static byte[] Data(int length, int seed)
    {
        var data = new byte[length];
        new Random(seed).NextBytes(data);
        return data;
    }

    [Theory]
    [InlineData(1)]
    [InlineData(8)]
    public void The_session_sends_files_over_one_or_eight_connections_that_the_client_opened(int connections)
    {
        var a = Data((5 * Block) + 17, 1);
        var b = Data(2 * Block, 2);
        var c = Data(0, 3);
        TransferFile[] files = [new("report.pdf", a.Length), new(@"Photos\b.jpg", b.Length), new(@"Photos\sub\empty.txt", c.Length)];

        using var run = new ReverseRun(files, [a, b, c], connections);
        run.Start();

        Assert.Equal(DirectSendStatus.Succeeded, run.Outcome.Status);
        Assert.Equal(DirectReceiverState.Succeeded, run.Receiver.State);
        Assert.Equal(a, run.Storage.Content("report.pdf"));
        Assert.Equal(b, run.Storage.Content(@"Photos\b.jpg"));
        Assert.Equal(c, run.Storage.Content(@"Photos\sub\empty.txt"));
        Assert.Equal(connections, run.Receiver.Connections);
        Assert.Equal(connections, run.Sender.Stats().Connections);
        Assert.Equal(a.Length + b.Length, run.Receiver.BytesPerConnection.Where(bytes => bytes > 0).Sum());
        Assert.Equal(new[] { "report.pdf", @"Photos\b.jpg", @"Photos\sub\empty.txt" }, run.Receiver.ListedFiles.Select(file => file.RelativePath));
        Assert.Equal(0, run.Storage.DeleteAllCalls);
    }

    [Fact]
    public void The_files_arrive_in_a_part_folder_on_disk_and_move_into_the_folder_for_received_files_without_replacing_anything()
    {
        var root = Path.Combine(Path.GetTempPath(), "ultimaterdp-tests-" + Guid.NewGuid().ToString("N"));
        var part = Path.Combine(root, ReceiveNames.PartFolderName("0f3c")!);
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "Photos"));
            File.WriteAllText(Path.Combine(root, "report.pdf"), "an older file");
            var a = Data((2 * Block) + 5, 1);
            var b = Data(100, 2);
            var completed = new ConcurrentQueue<string>();
            var storage = new FolderDirectStorage(part, completed.Enqueue);
            TransferFile[] files = [new("report.pdf", a.Length, new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc)), new(@"Photos\b.jpg", b.Length)];

            using (var run = new ReverseRun(files, [a, b], 2, storage: storage))
            {
                run.Start();
                Assert.Equal(DirectSendStatus.Succeeded, run.Outcome.Status);
            }

            Assert.Equal(2, completed.Count);
            var moved = ReceivePolicy.MoveIn(part, root, Items);

            Assert.Equal([Path.Combine(root, "report (2).pdf"), Path.Combine(root, "Photos (2)")], moved);
            Assert.Equal("an older file", File.ReadAllText(Path.Combine(root, "report.pdf")));
            Assert.Equal(a, File.ReadAllBytes(Path.Combine(root, "report (2).pdf")));
            Assert.Equal(b, File.ReadAllBytes(Path.Combine(root, "Photos (2)", "b.jpg")));
            Assert.Equal(new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc), File.GetLastWriteTimeUtc(Path.Combine(root, "report (2).pdf")));
            Assert.Throws<IOException>(() => storage.FullPath(@"..\outside.txt"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void A_failed_transfer_deletes_its_part_folder_on_disk()
    {
        var root = Path.Combine(Path.GetTempPath(), "ultimaterdp-tests-" + Guid.NewGuid().ToString("N"));
        var part = Path.Combine(root, ReceiveNames.PartFolderName("77")!);
        try
        {
            var a = Data(64 * Block, 1);
            using (var run = new ReverseRun([new("report.pdf", a.Length)], [a], 4, storage: new FolderDirectStorage(part), cutAgentToClientAfter: index => index == 1 ? 2L * Block : long.MaxValue))
            {
                run.Start();
                Assert.Equal(DirectSendStatus.Broken, run.Outcome.Status);
            }

            Assert.False(Directory.Exists(part));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void A_cancel_on_the_client_that_receives_ends_both_sides_and_deletes_what_came()
    {
        var a = Data(30 * Block, 1);
        var hold = new HoldingStream(a, 16L * Block);
        using var run = new ReverseRun([new("report.pdf", a.Length)], [a], 4, open: _ => hold);
        run.Receiver.DataReceived += () =>
        {
            if (run.Receiver.ReceivedBytes >= 8L * Block)
            {
                run.Receiver.Cancel("The user cancelled the transfer.");
            }
        };
        run.Start();
        hold.Release();

        Assert.Equal(DirectSendStatus.AgentCancelled, run.Outcome.Status);
        Assert.Equal(DropStatus.Cancelled, run.Outcome.Result!.Status);
        Assert.Equal(DirectReceiverState.Cancelled, run.Receiver.State);
        Assert.True(run.Receiver.CancelledHere);
        Assert.Equal("The user cancelled the transfer.", run.Receiver.CancelReason);
        Assert.Equal(1, run.Storage.DeleteAllCalls);
    }

    [Fact]
    public void A_cancel_of_the_session_ends_both_sides()
    {
        var a = Data(30 * Block, 1);
        var hold = new HoldingStream(a, 16L * Block);
        using var run = new ReverseRun([new("report.pdf", a.Length)], [a], 2, open: _ => hold);
        run.Sender.Acknowledged += _ => run.Sender.Cancel("The agent stops.");
        run.Start();
        hold.Release();

        Assert.Equal(DirectSendStatus.Cancelled, run.Outcome.Status);
        Assert.Equal(DirectReceiverState.Cancelled, run.Receiver.State);
        Assert.False(run.Receiver.CancelledHere);
        Assert.Equal("The agent stops.", run.Receiver.CancelReason);
        Assert.Equal(1, run.Storage.DeleteAllCalls);
    }

    [Fact]
    public void A_connection_that_breaks_fails_the_transfer_on_both_sides()
    {
        var a = Data(64 * Block, 1);
        using var run = new ReverseRun([new("report.pdf", a.Length)], [a], 4, cutAgentToClientAfter: index => index == 2 ? 3L * Block : long.MaxValue);
        run.Start();

        Assert.Equal(DirectSendStatus.Broken, run.Outcome.Status);
        Assert.Equal(DirectReceiverState.Failed, run.Receiver.State);
        Assert.Equal(DirectFailure.Broken, run.Receiver.Failure);
        Assert.Equal(1, run.Storage.DeleteAllCalls);
        Assert.Empty(run.Storage.Files);
    }

    [Fact]
    public void A_block_damaged_on_its_way_to_the_disk_of_the_client_fails_the_checksum()
    {
        var a = Data(3 * Block, 1);
        var storage = new CorruptingStorage();
        using var run = new ReverseRun([new("report.pdf", a.Length)], [a], 1, storage: storage);
        run.Start();

        Assert.Equal(DirectSendStatus.AgentFailed, run.Outcome.Status);
        Assert.Equal(DirectFailure.HashMismatch, run.Outcome.Result!.Failure);
        Assert.Equal(DirectFailure.HashMismatch, run.Receiver.Failure);
        Assert.Equal("report.pdf", run.Receiver.FailurePath);
        Assert.Equal(1, storage.Inner.DeleteAllCalls);
    }

    [Fact]
    public void A_path_outside_the_items_of_the_offer_is_refused_by_the_client()
    {
        var a = Data(10, 1);
        using var run = new ReverseRun([new(@"..\evil.exe", a.Length)], [a], 1);
        run.Start();

        Assert.Equal(DirectSendStatus.AgentFailed, run.Outcome.Status);
        Assert.Equal(DirectFailure.BadName, run.Receiver.Failure);
        Assert.Empty(run.Storage.Files);
    }

    [Fact]
    public void The_sends_of_the_agent_are_paced_to_the_limit()
    {
        var a = Data(10 * Block, 1);
        long now = 0;
        var waits = new ConcurrentQueue<int>();
        using var run = new ReverseRun(
            [new("report.pdf", a.Length)],
            [a],
            1,
            pacer: new DirectPacer(10_000_000, () => Interlocked.Read(ref now), burstBytes: 2L * Block),
            paceWait: wait =>
            {
                // The clock of the pacer moves by the wait instead of real time passing.
                waits.Enqueue(wait);
                Interlocked.Add(ref now, wait);
            });
        run.Start();

        Assert.Equal(DirectSendStatus.Succeeded, run.Outcome.Status);

        // Two blocks of burst, then each block waits what 1 MiB takes at 10 MB/s: 8 MiB in 839 ms
        // in all, each wait rounded up and the rest carried to the next one.
        Assert.Equal(8, waits.Count);
        Assert.All(waits, wait => Assert.InRange(wait, 104, 105));
        Assert.InRange(waits.Sum(), 839, 840);
        Assert.Equal(waits.Sum() / 1000.0, run.Sender.Stats().PacedSeconds, 3);
    }

    [Fact]
    public void A_token_of_the_other_direction_is_refused_on_both_kinds_of_offer()
    {
        var receiving = new DirectTokenGate(Token, 2, DirectRole.ClientReceives);
        var sending = new DirectTokenGate(Token, 2, DirectRole.ClientSends);

        Assert.Equal("The token is not the one of the offer.", receiving.Check(DirectFrames.TokenPayload(Token, 0), out _));
        Assert.Equal("The token is not the one of the offer.", sending.Check(DirectFrames.TokenPayload(Token, 0, DirectRole.ClientReceives), out _));
        Assert.Null(receiving.Check(DirectFrames.TokenPayload(Token, 0, DirectRole.ClientReceives), out var index));
        Assert.Equal(0, index);
        Assert.Equal("Connection 0 was already used.", receiving.Check(DirectFrames.TokenPayload(Token, 0, DirectRole.ClientReceives), out _));
        Assert.Null(receiving.Check(DirectFrames.TokenPayload(Token, 1, DirectRole.ClientReceives), out _));
        Assert.Equal("Every connection of the offer was used.", receiving.Check(DirectFrames.TokenPayload(Token, 1, DirectRole.ClientReceives), out _));

        // A proof made for one direction is not one of the other, even with the role byte changed.
        var forged = DirectFrames.TokenPayload(Token, 0);
        Assert.Equal("The token is not the one of the offer.", new DirectTokenGate(Token, 2, DirectRole.ClientReceives).Check([.. forged, (byte)DirectRole.ClientReceives], out _));
        Assert.NotEqual(DirectFrames.ConnectionProof(Token, 0), DirectFrames.ConnectionProof(Token, 0, DirectRole.ClientReceives));
    }

    [Fact]
    public void A_token_of_the_receiving_role_is_refused_by_the_agent_of_a_drop()
    {
        var receiver = new DirectReceiver(Token, 2, Items, new MemoryDirectStorage(), () => 0, _ => null);
        var (client, agent) = DuplexPipe.Create();
        var thread = new Thread(() =>
        {
            Assert.Equal("The token is not the one of the offer.", receiver.Serve(agent).Refusal);
            agent.Dispose();
        });
        thread.Start();
        DirectFrames.Write(client, DirectFrameType.Token, DirectFrames.TokenPayload(Token, 0, DirectRole.ClientReceives));

        Assert.False(new DirectFrameReader(client).TryRead(out _, out _));
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)));
        client.Dispose();
    }

    [Fact]
    public void The_token_frame_of_a_client_that_sends_is_the_one_of_0_7()
    {
        var payload = DirectFrames.TokenPayload(Token, 5);

        Assert.Equal(DirectFrames.TokenPayloadLength, payload.Length);
        Assert.True(DirectFrames.TryParseToken(payload, out _, out var index, out var role, out _));
        Assert.Equal(5, index);
        Assert.Equal(DirectRole.ClientSends, role);

        var receive = DirectFrames.TokenPayload(Token, 5, DirectRole.ClientReceives);
        Assert.Equal(DirectFrames.ReceiveTokenPayloadLength, receive.Length);
        Assert.Equal(1, receive[^1]);
        Assert.True(DirectFrames.TryParseToken(receive, out _, out _, out role, out _));
        Assert.Equal(DirectRole.ClientReceives, role);
        Assert.False(DirectFrames.TryParseToken(receive[..^1].Concat(new byte[] { 7 }).ToArray(), out _, out _, out _, out _));
    }

    /// <summary>Both sides of a send over in memory connections, as the agent and the client run them.</summary>
    private sealed class ReverseRun : IDisposable
    {
        private readonly int _connections;
        private readonly Func<int, long> _cut;
        private readonly DirectTokenGate _gate;
        private readonly List<Thread> _threads = [];
        private readonly ManualResetEventSlim _done = new(false);
        private DirectSendOutcome? _outcome;

        public ReverseRun(
            TransferFile[] files,
            byte[][] contents,
            int connections,
            IDirectStorage? storage = null,
            Func<int, Stream>? open = null,
            Func<int, long>? cutAgentToClientAfter = null,
            DirectPacer? pacer = null,
            Action<int>? paceWait = null)
        {
            _connections = connections;
            _cut = cutAgentToClientAfter ?? (_ => long.MaxValue);
            _gate = new DirectTokenGate(Token, connections, DirectRole.ClientReceives);
            Storage = storage as MemoryDirectStorage ?? new MemoryDirectStorage();
            var openFile = open ?? (index => new MemoryStream(contents[index], writable: false));
            Sender = new DirectSender(
                () => files,
                index =>
                {
                    // The data waits until every connection is attached on both sides, so that
                    // a slow machine cannot end the transfer before the extra ones are in.
                    if (index == 0)
                    {
                        SpinWait.SpinUntil(() => Sender!.Stats().Connections == connections && Receiver!.Connections == connections, TimeSpan.FromSeconds(30));
                    }

                    return openFile(index);
                },
                connections,
                () => Environment.TickCount64)
            {
                BytesPerConnection = Block,
                Peer = "client",
                Pacer = pacer,
                PaceWait = paceWait ?? Thread.Sleep,
            };
            Receiver = new DirectReceiver(Token, connections, Items, storage ?? Storage, () => Environment.TickCount64, _ => null);
        }

        public DirectSender Sender { get; }

        public DirectReceiver Receiver { get; }

        public MemoryDirectStorage Storage { get; }

        public DirectSendOutcome Outcome => _outcome!;

        /// <summary>Connection 0, then the others once it is ready, then the end of both sides.</summary>
        public void Start()
        {
            Connect(0);
            for (var index = 1; index < _connections; index++)
            {
                Connect(index);
            }

            Assert.True(_done.Wait(TimeSpan.FromSeconds(60)), "The sender did not end.");
            Assert.True(Receiver.WaitEnded(TimeSpan.FromSeconds(60)), "The receiver did not end.");
            foreach (var thread in _threads)
            {
                Assert.True(thread.Join(TimeSpan.FromSeconds(60)), "A connection did not end.");
            }
        }

        public void Dispose()
        {
            Receiver.Cancel("the test ends");
            Sender.Cancel("the test ends");
        }

        /// <summary>The client opens connection <paramref name="index"/>: its token frame, the ready frame of the agent, then the receiver.</summary>
        private void Connect(int index)
        {
            // The first end writes the data: the agent end, whose direction may be cut.
            var (agentEnd, clientEnd) = DuplexPipe.Create(_cut(index));
            var agent = new Thread(() => ServeOnAgent(index, agentEnd)) { IsBackground = true };
            agent.Start();
            _threads.Add(agent);
            DirectFrames.Write(clientEnd, DirectFrameType.Token, DirectFrames.TokenPayload(Token, (byte)index, DirectRole.ClientReceives));
            Assert.True(new DirectFrameReader(clientEnd).TryRead(out var type, out _));
            Assert.Equal(DirectFrameType.Ready, type);

            // Registered here, in the order of the indexes, as the client does; only the reads run on threads.
            Assert.Null(Receiver.Register(index, clientEnd));
            var client = new Thread(() =>
            {
                Receiver.RunRegistered(index);
                clientEnd.Dispose();
            })
            {
                IsBackground = true,
            };
            client.Start();
            _threads.Add(client);
        }

        private void ServeOnAgent(int index, Stream agentEnd)
        {
            var reader = new DirectFrameReader(agentEnd);
            if (!reader.TryRead(out _, out var payload) || _gate.Check(payload.Span, out var accepted) is not null || accepted != index)
            {
                agentEnd.Dispose();
                return;
            }

            DirectFrames.Write(agentEnd, DirectFrameType.Ready, []);
            if (index == 0)
            {
                _outcome = Sender.RunAccepted(new DirectConnection(agentEnd));
                _done.Set();
            }
            else
            {
                Sender.Attach(index, new DirectConnection(agentEnd));
            }
        }
    }

    /// <summary>A disk of the client that damages one byte of the first block it writes.</summary>
    private sealed class CorruptingStorage : IDirectStorage
    {
        public MemoryDirectStorage Inner { get; } = new();

        public void CreateDirectory(string relativePath) => Inner.CreateDirectory(relativePath);

        public IDirectFile CreateFile(string relativePath, long size) => new File(Inner.CreateFile(relativePath, size));

        public void CompleteFile(string relativePath, DateTime? modified) => Inner.CompleteFile(relativePath, modified);

        public void DeleteAll() => Inner.DeleteAll();

        private sealed class File(IDirectFile inner) : IDirectFile
        {
            private int _damaged;

            public void Write(long offset, ReadOnlySpan<byte> data)
            {
                if (Interlocked.Exchange(ref _damaged, 1) == 0)
                {
                    var copy = data.ToArray();
                    copy[100] ^= 0x40;
                    inner.Write(offset, copy);
                    return;
                }

                inner.Write(offset, data);
            }

            public int Read(long offset, Span<byte> buffer) => inner.Read(offset, buffer);

            public void Dispose() => inner.Dispose();
        }
    }
}
