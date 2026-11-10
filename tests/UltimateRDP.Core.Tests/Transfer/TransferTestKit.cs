using System.Runtime.InteropServices;
using System.Text;
using UltimateRDP.Core.Protocol;
using UltimateRDP.Core.Transfer;

namespace UltimateRDP.Core.Tests.Transfer;

/// <summary>Files kept in memory, readable after their stream is closed.</summary>
internal sealed class MemoryTransferStorage : ITransferStorage
{
    public Dictionary<string, MemoryStream> Files { get; } = new(StringComparer.OrdinalIgnoreCase);

    public List<string> Directories { get; } = [];

    public Dictionary<string, DateTime?> Completed { get; } = new(StringComparer.OrdinalIgnoreCase);

    public int DeleteAllCalls { get; private set; }

    public bool FailOnCreate { get; set; }

    public void CreateDirectory(string relativePath) => Directories.Add(relativePath);

    public Stream CreateFile(string relativePath)
    {
        if (FailOnCreate)
        {
            throw new IOException("The disk is full.");
        }

        var stream = new MemoryStream();
        Files[relativePath] = stream;
        return stream;
    }

    public void CompleteFile(string relativePath, DateTime? modified) => Completed[relativePath] = modified;

    public void DeleteAll()
    {
        DeleteAllCalls++;
        Files.Clear();
        Completed.Clear();
    }

    public byte[] Content(string relativePath) => Files[relativePath].ToArray();
}

/// <summary>Writes nowhere; used to measure the codec path without holding the data.</summary>
internal sealed class NullTransferStorage : ITransferStorage
{
    public long Written { get; private set; }

    public void CreateDirectory(string relativePath)
    {
    }

    public Stream CreateFile(string relativePath) => new CountingStream(this);

    public void CompleteFile(string relativePath, DateTime? modified)
    {
    }

    public void DeleteAll()
    {
    }

    private sealed class CountingStream(NullTransferStorage owner) : Stream
    {
        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => owner.Written += count;
    }
}

/// <summary>
/// The channel between the client and the agent, in one byte model, with the decoding
/// each side really does: the client hands ASCII lines to the control, the agent reads the
/// bytes in blocks of about 1600 and detects the model from the first two, the agent
/// writes lines in that model and the client reads the strings the control delivers.
/// Acknowledgements wait in a queue until <see cref="DeliverToClient"/>, as if they were
/// on their way.
/// </summary>
internal sealed class SimulatedChannel
{
    private const int AgentReadBlock = 1600;

    private readonly ChannelByteModel _model;
    private readonly LineReader _agentLines = new();
    private readonly LineReader _clientLines = new();
    private readonly Queue<string> _toClient = new();
    private ChannelByteModel _agentModel = ChannelByteModel.Unknown;
    private byte[] _agentCarry = [];

    public SimulatedChannel(ChannelByteModel model)
    {
        _model = model;
    }

    /// <summary>Called by the agent side for each message it receives.</summary>
    public Func<Message, IEnumerable<Message>>? Agent { get; set; }

    /// <summary>Called by the client side for each message it receives.</summary>
    public Action<Message>? Client { get; set; }

    /// <summary>Lines that the channel loses on the way to the agent, by their position from zero.</summary>
    public HashSet<int> LostLines { get; } = [];

    /// <summary>Changes a line on its way to the agent, by its position from zero.</summary>
    public Func<int, string, string>? Tamper { get; set; }

    /// <summary>Loses a whole call on its way to the agent, from its position and its text, as the control did in production.</summary>
    public Func<int, string, bool>? LoseCall { get; set; }

    /// <summary>Delivers a call twice to the agent, from its position and its text.</summary>
    public Func<int, string, bool>? DuplicateCall { get; set; }

    public int LinesSent { get; private set; }

    public long CharactersSent { get; private set; }

    /// <summary>Calls lost on the way to the agent.</summary>
    public int CallsLost { get; private set; }

    public int PendingForClient => _toClient.Count;

    /// <summary>The agent sends a message outside of a read, from a timer.</summary>
    public void SendFromAgent(Message message) => _toClient.Enqueue(ToControlString(LineFraming.EncodeBytes(message, _agentModel)));

    /// <summary>The client hands one line, or one call of several lines, to the control.</summary>
    public void SendFromClient(string line)
    {
        var position = LinesSent++;
        CharactersSent += line.Length;
        if (LostLines.Contains(position) || LoseCall?.Invoke(position, line) == true)
        {
            CallsLost++;
            return;
        }

        var twice = DuplicateCall?.Invoke(position, line) == true;

        if (Tamper is not null)
        {
            line = Tamper(position, line);
        }

        // Whatever the control does with the string, the agent gets ASCII bytes or UTF-16LE.
        // In the one byte per character model a character above 0xFF cannot travel at all.
        Assert.True(_model == ChannelByteModel.Utf16 || line.All(c => c <= 0xFF), "A line above 0xFF was sent in the one byte per character model.");
        var bytes = _model == ChannelByteModel.OneBytePerChar
            ? Encoding.Latin1.GetBytes(line)
            : MemoryMarshal.AsBytes(line.AsSpan()).ToArray();
        for (var copy = twice ? 2 : 1; copy > 0; copy--)
        {
            for (var start = 0; start < bytes.Length; start += AgentReadBlock)
            {
                AgentRead(bytes.AsSpan(start, Math.Min(AgentReadBlock, bytes.Length - start)));
            }
        }
    }

    public void SendFromClient(Message message) => SendFromClient(LineFraming.EncodeLine(message));

    /// <summary>Delivers the lines waiting for the client. Returns how many.</summary>
    public int DeliverToClient()
    {
        var count = 0;
        while (_toClient.TryDequeue(out var data))
        {
            count++;
            // In the UTF-16 model the control turns the UTF-16LE bytes of the agent back
            // into the text, which the client then reads one character per byte.
            var model = ChannelTextCodec.DetectFromControlString(data);
            _clientLines.Append(ChannelTextCodec.ControlStringToText(data, model));
            while (_clientLines.TryReadLine(out var line))
            {
                var message = MessageSerializer.Deserialize(Encoding.ASCII.GetBytes(line.Trim(['\0', ' '])));
                Assert.NotNull(message);
                Client?.Invoke(message);
            }
        }

        return count;
    }

    private void AgentRead(ReadOnlySpan<byte> chunk)
    {
        byte[] bytes = [.. _agentCarry, .. chunk];
        _agentCarry = [];
        if (_agentModel == ChannelByteModel.Unknown)
        {
            _agentModel = ChannelTextCodec.DetectFromBytes(bytes);
            Assert.Equal(_model, _agentModel);
        }

        var usable = ChannelTextCodec.UsableLength(bytes, _agentModel);
        _agentCarry = bytes[usable..];
        _agentLines.Append(ChannelTextCodec.BytesToText(bytes.AsSpan(0, usable), _agentModel));
        while (_agentLines.TryReadLine(out var line))
        {
            // As the agent: a u15 chunk is read by hand from the string, any other line as ASCII JSON.
            var trimmed = line.Trim(['\0', ' ']);
            var message = TransferLines.TryParseChunk(trimmed, out var dense) ? dense : MessageSerializer.Deserialize(Encoding.ASCII.GetBytes(trimmed));
            Assert.NotNull(message);
            foreach (var reply in Agent?.Invoke(message) ?? [])
            {
                _toClient.Enqueue(ToControlString(LineFraming.EncodeBytes(reply, _agentModel)));
            }
        }
    }

    /// <summary>What the control delivers on the client for the bytes the agent wrote.</summary>
    private string ToControlString(byte[] bytes) => _model == ChannelByteModel.OneBytePerChar
        ? Encoding.Latin1.GetString(bytes)
        : new string(MemoryMarshal.Cast<byte, char>(bytes));
}

/// <summary>
/// Drives a whole transfer through a <see cref="SimulatedChannel"/>. With
/// <c>reliable</c>, the lines are numbered, the client keeps them in a
/// <see cref="ReliableOutbox"/>, and the agent puts them back in order with a
/// <see cref="ReliableInbox"/>, as the client and the agent of 0.5.1 do. Time only passes
/// when the client has nothing to do (<see cref="Idle"/>), in steps of
/// <see cref="IdleStepMilliseconds"/>.
/// </summary>
internal sealed class SimulatedTransfer
{
    public const int IdleStepMilliseconds = 100;

    /// <summary>Most idle steps in one run: two simulated minutes.</summary>
    public const int MaxIdleSteps = 1200;

    private int _idleSteps;

    public SimulatedTransfer(ChannelByteModel model, IReadOnlyList<TransferItem> items, IReadOnlyList<(TransferFile File, byte[] Data)> files, ITransferStorage storage, int chunkSize = TransferSender.DefaultChunkSize, long windowBytes = TransferSender.DefaultWindowBytes, string chunkEncoding = ChunkEncodings.Base64, bool reliable = false)
    {
        Files = files;
        Channel = new SimulatedChannel(model);
        Sender = new TransferSender("t1", [.. files.Select(file => file.File)], chunkSize, windowBytes, chunkEncoding, reliable);
        Receiver = new TransferReceiver("t1", items, storage, () => Environment.TickCount64);
        Watch = new TransferAckWatch(0);
        if (reliable)
        {
            Outbox = new ReliableOutbox();
            Inbox = new ReliableInbox("t1");
        }

        Channel.Agent = message =>
        {
            var replies = new List<Message>();
            if (Inbox is not null && ReliableInbox.SeqOf(message) is { } seq)
            {
                // As StagedTransfer does on the agent.
                var step = Inbox.Accept(message, Clock, line => Receiver.IsFinished || HandleInOrder(line, replies));
                if (step.Duplicate && seq == Inbox.Expected - 1 && Receiver.CurrentAck() is { } current)
                {
                    replies.Add(current with { Seq = seq });
                }

                if (!Receiver.IsFinished && step.Request is { } request)
                {
                    Requests.Add(request);
                    replies.Add(request);
                }

                if (step.Failed)
                {
                    Receiver.Cancel();
                }
            }
            else if (Inbox is not null && Inbox.Arrived > 0 && ReliableInbox.IsNumberedType(message))
            {
                // As StagedTransfer: a line whose number was damaged is left to the gap that follows.
                UnnumberedIgnored++;
            }
            else
            {
                HandleInOrder(message, replies);
            }

            AddResultIfOver(replies);
            return replies;
        };
        Channel.Client = message =>
        {
            switch (message)
            {
                case FileAckMessage ack:
                    Assert.True(Sender.OnAck(ack));
                    if (ack.Seq is { } seq)
                    {
                        Outbox?.Acknowledge(seq);
                    }

                    Watch.OnAck(Clock);
                    Acks++;
                    break;
                case TransferResendMessage request:
                    Assert.NotNull(Outbox);
                    SendAgain(Outbox.Take(request.From, request.To, request.Damaged == true));
                    break;
                case TransferResultMessage result:
                    Result = result;
                    break;
            }
        };
    }

    public IReadOnlyList<(TransferFile File, byte[] Data)> Files { get; }

    public SimulatedChannel Channel { get; }

    public TransferSender Sender { get; }

    public TransferReceiver Receiver { get; }

    public TransferAckWatch Watch { get; }

    /// <summary>The lines kept by the client, when they are numbered.</summary>
    public ReliableOutbox? Outbox { get; }

    /// <summary>The order of the lines on the agent, when they are numbered.</summary>
    public ReliableInbox? Inbox { get; }

    /// <summary>The requests of the agent, in their order.</summary>
    public List<TransferResendMessage> Requests { get; } = [];

    /// <summary>The chunks that the agent found damaged, in their order.</summary>
    public List<ChunkDamage> Damages { get; } = [];

    /// <summary>Lines of a numbered transfer that arrived without a number, and were ignored.</summary>
    public int UnnumberedIgnored { get; private set; }

    /// <summary>Simulated milliseconds, which only pass in <see cref="Idle"/>.</summary>
    public long Clock { get; private set; }

    /// <summary>True while the client hands lines sent again to the channel.</summary>
    public bool SendingAgain { get; private set; }

    public TransferResultMessage? Result { get; private set; }

    public bool ResultSent { get; private set; }

    public int Acks { get; private set; }

    /// <summary>Times the window was full and the client waited for acknowledgements.</summary>
    public int WindowWaits { get; private set; }

    /// <summary>Most bytes in flight seen before a chunk.</summary>
    public long MostInFlight { get; private set; }

    /// <summary>
    /// When set, the lines go through this queue and leave in batches, as the client of
    /// 0.4.3 sends them: full batches at once, the rest before the client waits.
    /// </summary>
    public SendQueue? Batching { get; set; }

    /// <summary>
    /// With <see cref="Batching"/>, sends transferBegin and fileBegin of the first file in a
    /// call of their own before the first chunk, as the client of 0.5.0 did in production.
    /// </summary>
    public bool FirstLinesAlone { get; set; }

    /// <summary>Runs the transfer; <paramref name="cancelAfterChunks"/> stops it with transferCancel.</summary>
    public void Run(int? cancelAfterChunks = null)
    {
        var chunks = 0;
        Send(Sender.Begin());
        foreach (var (_, data) in Files)
        {
            Send(Sender.BeginFile());
            if (FirstLinesAlone && chunks == 0)
            {
                Flush();
            }

            for (var offset = 0; offset < data.Length; offset += Sender.ChunkSize)
            {
                if (Result is not null)
                {
                    return;
                }

                var count = Math.Min(Sender.ChunkSize, data.Length - offset);
                while (!Sender.CanSend(count))
                {
                    Flush();
                    WindowWaits++;
                    if (Channel.DeliverToClient() == 0 && !Idle())
                    {
                        // Nothing on its way and nothing more to try: the agent stopped acknowledging, or failed.
                        return;
                    }

                    if (Result is not null)
                    {
                        return;
                    }
                }

                if (cancelAfterChunks == chunks)
                {
                    Send(Sender.Cancel("The user cancelled the transfer."));
                    Flush();
                    Channel.DeliverToClient();
                    return;
                }

                MostInFlight = Math.Max(MostInFlight, Sender.InFlightBytes + count);
                var line = Sender.EncodeChunk(data.AsSpan(offset, count));
                Emit(new QueuedLine(line, MessageTypes.FileChunk, "t1", Sender.SentBytes, Sender.Sequenced ? Sender.LastSeq : -1));
                Watch.OnChunkSent(Clock);
                chunks++;
            }

            Send(Sender.EndFile());
        }

        Flush();
        Channel.DeliverToClient();
        while (Result is null && (Channel.DeliverToClient() > 0 || Idle()))
        {
        }
    }

    /// <summary>
    /// The client has nothing to do: time passes. The agent asks again for missing lines
    /// after a silence, and the client sends its newest line again when no acknowledgement
    /// came. Returns false when the lines are not numbered, or after <see cref="MaxIdleSteps"/>.
    /// </summary>
    public bool Idle()
    {
        if (Inbox is null || Outbox is null || ++_idleSteps > MaxIdleSteps)
        {
            return false;
        }

        Clock += IdleStepMilliseconds;
        if (!Receiver.IsFinished)
        {
            var step = Inbox.Check(Clock);
            if (step.Request is { } request)
            {
                Requests.Add(request);
                Channel.SendFromAgent(request);
            }

            if (step.Failed)
            {
                Receiver.Cancel();
                var replies = new List<Message>();
                AddResultIfOver(replies);
                replies.ForEach(Channel.SendFromAgent);
            }
        }

        if (Watch.ProbeDue(Clock, waitingForAck: true) && Outbox.TakeNewest() is { } newest)
        {
            SendAgain([newest]);
        }

        Channel.DeliverToClient();
        return true;
    }

    /// <summary>Hands a line to the receiver; false for a numbered chunk damaged on the way.</summary>
    private bool HandleInOrder(Message message, List<Message> replies)
    {
        var ack = Receiver.Handle(message, out var damage);
        if (damage is not null)
        {
            Damages.Add(damage);
            return false;
        }

        if (ack is not null)
        {
            replies.Add(ReliableInbox.SeqOf(message) is { } seq ? ack with { Seq = seq } : ack);
        }

        return true;
    }

    private void AddResultIfOver(List<Message> replies)
    {
        if (!Receiver.IsFinished || ResultSent)
        {
            return;
        }

        ResultSent = true;
        replies.Add(new TransferResultMessage("t1", Receiver.State switch
        {
            TransferState.Succeeded => DropStatus.Ok,
            TransferState.Cancelled when Inbox?.Failure is null or InboxFailure.None => DropStatus.Cancelled,
            _ => DropStatus.Error,
        }, Receiver.FailureDetail ?? Inbox?.FailureDetail, Receiver.ReceivedBytes, Receiver.Seconds));
    }

    private void SendAgain(IReadOnlyList<QueuedLine> lines)
    {
        SendingAgain = true;
        foreach (var line in lines)
        {
            Emit(line);
        }

        Flush();
        SendingAgain = false;
    }

    private void Send(Message message) =>
        Emit(new QueuedLine(LineFraming.EncodeLine(message), message.Type, "t1", Seq: ReliableInbox.SeqOf(message) ?? -1));

    private void Emit(QueuedLine line)
    {
        if (line.Seq >= 0 && !line.IsResend)
        {
            Outbox!.Add(line);
        }

        if (Batching is null)
        {
            Channel.SendFromClient(line.Text);
            return;
        }

        Batching.Enqueue(line);
        while (Batching.IsOverLimit)
        {
            Channel.SendFromClient(Batching.TakeBatch()!.Text);
        }
    }

    private void Flush()
    {
        while (Batching?.TakeBatch() is { } batch)
        {
            Channel.SendFromClient(batch.Text);
        }
    }

    /// <summary>A file of <paramref name="size"/> bytes of repeatable random data.</summary>
    public static (TransferFile File, byte[] Data) MakeFile(string relativePath, int size, int seed)
    {
        var data = new byte[size];
        new Random(seed).NextBytes(data);
        return (new TransferFile(relativePath, size, new DateTime(2026, 11, 9, 10, 0, 0, DateTimeKind.Utc)), data);
    }
}
