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

    public int LinesSent { get; private set; }

    public long CharactersSent { get; private set; }

    public int PendingForClient => _toClient.Count;

    /// <summary>The client hands one line to the control.</summary>
    public void SendFromClient(string line)
    {
        var position = LinesSent++;
        CharactersSent += line.Length;
        if (LostLines.Contains(position))
        {
            return;
        }

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
        for (var start = 0; start < bytes.Length; start += AgentReadBlock)
        {
            AgentRead(bytes.AsSpan(start, Math.Min(AgentReadBlock, bytes.Length - start)));
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

/// <summary>Drives a whole transfer through a <see cref="SimulatedChannel"/>.</summary>
internal sealed class SimulatedTransfer
{
    public SimulatedTransfer(ChannelByteModel model, IReadOnlyList<TransferItem> items, IReadOnlyList<(TransferFile File, byte[] Data)> files, ITransferStorage storage, int chunkSize = TransferSender.DefaultChunkSize, long windowBytes = TransferSender.DefaultWindowBytes, string chunkEncoding = ChunkEncodings.Base64)
    {
        Files = files;
        Channel = new SimulatedChannel(model);
        Sender = new TransferSender("t1", [.. files.Select(file => file.File)], chunkSize, windowBytes, chunkEncoding);
        Receiver = new TransferReceiver("t1", items, storage, () => Environment.TickCount64);
        Channel.Agent = message =>
        {
            var ack = Receiver.Handle(message);
            var replies = new List<Message>();
            if (ack is not null)
            {
                replies.Add(ack);
            }

            if (Receiver.IsFinished && !ResultSent)
            {
                ResultSent = true;
                replies.Add(new TransferResultMessage("t1", Receiver.State switch
                {
                    TransferState.Succeeded => DropStatus.Ok,
                    TransferState.Cancelled => DropStatus.Cancelled,
                    _ => DropStatus.Error,
                }, Receiver.FailureDetail, Receiver.ReceivedBytes, Receiver.Seconds));
            }

            return replies;
        };
        Channel.Client = message =>
        {
            switch (message)
            {
                case FileAckMessage ack:
                    Assert.True(Sender.OnAck(ack));
                    Acks++;
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

    /// <summary>Runs the transfer; <paramref name="cancelAfterChunks"/> stops it with transferCancel.</summary>
    public void Run(int? cancelAfterChunks = null)
    {
        var chunks = 0;
        Send(Sender.Begin());
        foreach (var (_, data) in Files)
        {
            Send(Sender.BeginFile());
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
                    if (Channel.DeliverToClient() == 0)
                    {
                        // Nothing on its way: the agent stopped acknowledging, or failed.
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
                Send(Sender.EncodeChunk(data.AsSpan(offset, count)));
                chunks++;
            }

            Send(Sender.EndFile());
        }

        Flush();
        Channel.DeliverToClient();
    }

    private void Send(Message message) => Send(LineFraming.EncodeLine(message), message.Type);

    private void Send(string line, string type = MessageTypes.FileChunk)
    {
        if (Batching is null)
        {
            Channel.SendFromClient(line);
            return;
        }

        Batching.Enqueue(new QueuedLine(line, type, "t1"));
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
