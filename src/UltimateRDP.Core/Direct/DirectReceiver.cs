using System.Diagnostics;
using System.Security.Cryptography;
using UltimateRDP.Core.Protocol;
using UltimateRDP.Core.Transfer;

namespace UltimateRDP.Core.Direct;

/// <summary>Where a <see cref="DirectReceiver"/> stands.</summary>
public enum DirectReceiverState
{
    /// <summary>Waiting for connection 0 and its token.</summary>
    AwaitingControl,

    /// <summary>Connection 0 was accepted; waiting for the begin frame.</summary>
    AwaitingBegin,

    /// <summary>Receiving the file headers.</summary>
    Listing,

    /// <summary>The list was checked and the go frame sent: receiving blocks on every connection.</summary>
    Receiving,

    /// <summary>Every file arrived and was checked.</summary>
    Succeeded,

    /// <summary>The transfer failed; see <see cref="DirectReceiver.Failure"/>.</summary>
    Failed,

    /// <summary>The client cancelled the transfer.</summary>
    Cancelled,
}

/// <summary>What became of one connection handed to <see cref="DirectReceiver.Serve"/>.</summary>
/// <param name="Index">The index of the connection, or -1 when its token was refused.</param>
/// <param name="Refusal">Why the token was refused, in English, for the log; null when accepted.</param>
public readonly record struct DirectServeResult(int Index, string? Refusal);

/// <summary>How long the clean up of the files took after the end of a transfer, for the log.</summary>
/// <param name="WaitMilliseconds">From the end of the transfer to the moment no thread wrote or read the files any more.</param>
/// <param name="CloseMilliseconds">Closing the files.</param>
/// <param name="DeleteMilliseconds">Deleting the folder of the transfer; null after a success, which keeps the files.</param>
public sealed record DirectCleanUpTimes(long WaitMilliseconds, long CloseMilliseconds, long? DeleteMilliseconds);

/// <summary>
/// The agent side of a direct transfer, version 2 of the protocol, over up to
/// <see cref="MaxConnections"/> connections that already did their TLS handshake. Each
/// connection is handed to <see cref="Serve"/> on a thread of its own: it checks the proof
/// of the token for the index of the connection, then reads frames. Connection 0 carries
/// the list of the files, which is checked whole before the files are created and the go
/// frame sent; then every connection carries blocks, written at their offsets as they
/// come. A verifier thread reads back the part of each file that is complete from its
/// start and hashes it, compares the SHA-256 with the trailer, acknowledges on connection 0
/// and ends with the final frame. Anything that is not a success deletes what was written, on
/// a thread of its own after the end, so that nothing waits for the file system.
/// </summary>
/// <remarks>
/// The caller bounds the time: it closes a connection whose token does not come in time,
/// gives the streams a read timeout, and calls <see cref="Close"/> to stop the transfer from
/// the agent side. Thread safe.
/// </remarks>
public sealed class DirectReceiver
{
    private const byte BlockFree = 0;
    private const byte BlockClaimed = 1;
    private const byte BlockWritten = 2;

    /// <summary>The verifier wakes at least this often to look for acknowledgements due and for the end of the connections.</summary>
    private const int VerifierWaitMilliseconds = 500;

    private readonly object _gate = new();
    private readonly Lock _controlWrite = new();
    private readonly DirectTokenGate _tokens;
    private readonly IDirectStorage _storage;
    private readonly Func<long> _clock;
    private readonly Func<DirectReceiver, string?> _describe;
    private readonly Func<long, bool>? _hasRoomFor;
    private readonly DirectPacer? _pacer;
    private readonly Connection?[] _connections;
    private readonly HashSet<string> _paths = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<TransferFile> _headers = [];
    private readonly ManualResetEventSlim _ended = new(false);
    private readonly ManualResetEventSlim _stopped = new(false);
    private long _finishStamp;
    private FileSlot[] _files = [];
    private long _announcedBytes;
    private long _bytesAtLastAck;
    private long _startTicks;
    private long _endTicks;
    private int _busy;
    private bool _cleanedUp;
    private volatile DirectReceiverState _state = DirectReceiverState.AwaitingControl;
    private long _receivedBytes;
    private long _readTicks;
    private long _writeTicks;
    private long _hashTicks;
    private long _pacedMilliseconds;

    /// <param name="token">The token of the offer, <see cref="DirectFrames.TokenLength"/> bytes.</param>
    /// <param name="maxConnections">The connections the offer allows, 1 to <see cref="DirectFrames.MaxConnections"/>.</param>
    /// <param name="items">The dropped items; every relative path must start with one of their names.</param>
    /// <param name="storage">Where the files go.</param>
    /// <param name="clockMilliseconds">A clock in milliseconds, for the time of the final frame and the rates.</param>
    /// <param name="describe">The text of the final frame for the user, in the language of the client; null for none.</param>
    /// <param name="hasRoomFor">Says whether the announced bytes fit in the staging folder; null skips the check.</param>
    /// <param name="pacer">Limits the rate of all the connections together; null for no limit.</param>
    public DirectReceiver(
        ReadOnlySpan<byte> token,
        int maxConnections,
        IReadOnlyList<TransferItem> items,
        IDirectStorage storage,
        Func<long> clockMilliseconds,
        Func<DirectReceiver, string?> describe,
        Func<long, bool>? hasRoomFor = null,
        DirectPacer? pacer = null)
    {
        if (token.Length != DirectFrames.TokenLength)
        {
            throw new ArgumentException("A token has 32 bytes.", nameof(token));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(maxConnections, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxConnections, DirectFrames.MaxConnections);
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(clockMilliseconds);
        ArgumentNullException.ThrowIfNull(describe);
        _tokens = new DirectTokenGate(token, maxConnections, DirectRole.ClientSends);
        MaxConnections = maxConnections;
        Items = items;
        _storage = storage;
        _clock = clockMilliseconds;
        _describe = describe;
        _hasRoomFor = hasRoomFor;
        _pacer = pacer;
        _connections = new Connection?[maxConnections];
        Meter = new DirectRateMeter(clockMilliseconds);
    }

    /// <summary>Raised on the thread of a connection after each block was written: the transfer is alive.</summary>
    public event Action? DataReceived;

    /// <summary>Raised once the go frame went out: the data flows from now on.</summary>
    public event Action? Started;

    /// <summary>Raised once the final frame went out on connection 0, before connection 0 is drained.</summary>
    public event Action? FinalSent;

    /// <summary>Raised when a connection other than 0 closed before it carried any block; the transfer goes on without it.</summary>
    public event Action<int, string>? ConnectionDropped;

    /// <summary>
    /// Raised once the transfer is over and its connections are closed, or connection 0 is
    /// left to drain after the final frame; the files may still be closing and deleting.
    /// </summary>
    public event Action? Stopped;

    /// <summary>Raised when the transfer ends, before the final frame is written, so that the caller can bound that write.</summary>
    public event Action? Finishing;

    /// <summary>
    /// Raised once the transfer is over and nothing touches its files any more: every file is
    /// closed, and deleted unless the transfer succeeded. Raised on a thread of its own.
    /// </summary>
    public event Action? Ended;

    /// <summary>The connections the offer allows.</summary>
    public int MaxConnections { get; }

    /// <summary>The dropped items.</summary>
    public IReadOnlyList<TransferItem> Items { get; }

    /// <summary>Where the transfer stands.</summary>
    public DirectReceiverState State => _state;

    /// <summary>True once the transfer succeeded, failed or was cancelled.</summary>
    public bool IsFinished => _state is DirectReceiverState.Succeeded or DirectReceiverState.Failed or DirectReceiverState.Cancelled;

    /// <summary>True once <see cref="Ended"/> was raised.</summary>
    public bool HasEnded => _ended.IsSet;

    /// <summary>True once <see cref="Stopped"/> was raised.</summary>
    public bool HasStopped => _stopped.IsSet;

    /// <summary>How long the clean up of the files took, once <see cref="Ended"/> was raised.</summary>
    public DirectCleanUpTimes? CleanUpTimes { get; private set; }

    /// <summary>Why the transfer failed, when <see cref="State"/> is <see cref="DirectReceiverState.Failed"/>.</summary>
    public DirectFailure Failure { get; private set; }

    /// <summary>Relative path of the file concerned by the failure, when there is one.</summary>
    public string? FailurePath { get; private set; }

    /// <summary>The failure in English, for the log.</summary>
    public string? FailureDetail { get; private set; }

    /// <summary>The reason the client gave with its cancel, in English.</summary>
    public string? CancelReason { get; private set; }

    /// <summary>True when the client cancelled through the channel rather than with a cancel frame.</summary>
    public bool CancelledThroughChannel { get; private set; }

    /// <summary>True when <see cref="Cancel"/> ended the transfer, on the side that receives.</summary>
    public bool CancelledHere { get; private set; }

    /// <summary>The files of the list, once connection 0 sent it whole; empty before.</summary>
    public IReadOnlyList<TransferFile> ListedFiles
    {
        get
        {
            lock (_gate)
            {
                return [.. _files.Select(slot => slot.File)];
            }
        }
    }

    /// <summary>True when <see cref="Close"/> ended the transfer, from the agent side.</summary>
    public bool ClosedByAgent { get; private set; }

    /// <summary>Files announced by the begin frame.</summary>
    public int ExpectedFiles { get; private set; }

    /// <summary>Bytes announced by the begin frame.</summary>
    public long ExpectedBytes { get; private set; }

    /// <summary>Files received and checked.</summary>
    public int ReceivedFiles { get; private set; }

    /// <summary>Bytes received and written.</summary>
    public long ReceivedBytes => Interlocked.Read(ref _receivedBytes);

    /// <summary>Acknowledgements sent.</summary>
    public int AcksSent { get; private set; }

    /// <summary>True once the final frame went out.</summary>
    public bool FinalWasSent { get; private set; }

    /// <summary>True once connection 0 presented its token.</summary>
    public bool HasControl
    {
        get
        {
            lock (_gate)
            {
                return _connections[0] is not null;
            }
        }
    }

    /// <summary>Connections whose token was accepted.</summary>
    public int Connections
    {
        get
        {
            lock (_gate)
            {
                return _connections.Count(connection => connection is not null);
            }
        }
    }

    /// <summary>Bytes of blocks received on each accepted connection, by index; -1 for an index never used.</summary>
    public IReadOnlyList<long> BytesPerConnection
    {
        get
        {
            lock (_gate)
            {
                return [.. _connections.Select(connection => connection?.Bytes ?? -1)];
            }
        }
    }

    /// <summary>Seconds the connections spent in socket reads, summed over the connections.</summary>
    public double ReadSeconds => Seconds(Interlocked.Read(ref _readTicks));

    /// <summary>Seconds spent writing blocks to the files, summed over the connections.</summary>
    public double WriteSeconds => Seconds(Interlocked.Read(ref _writeTicks));

    /// <summary>Seconds the verifier spent reading the files back and hashing them.</summary>
    public double HashSeconds => Seconds(Interlocked.Read(ref _hashTicks));

    /// <summary>Seconds the connections waited for the limit of <see cref="DirectPacer"/>, summed over the connections.</summary>
    public double PacedSeconds => Interlocked.Read(ref _pacedMilliseconds) / 1000.0;

    /// <summary>The bytes received, from the go frame to the end.</summary>
    public DirectRateMeter Meter { get; }

    /// <summary>Milliseconds from the begin frame to the end, or until now while it runs.</summary>
    public long Milliseconds => _startTicks == 0 ? 0 : Math.Max(0, (IsFinished ? _endTicks : _clock()) - _startTicks);

    /// <summary>Waits at most <paramref name="timeout"/> for <see cref="Ended"/>.</summary>
    public bool WaitEnded(TimeSpan timeout) => _ended.Wait(timeout);

    /// <summary>Waits at most <paramref name="timeout"/> for <see cref="Stopped"/>.</summary>
    public bool WaitStopped(TimeSpan timeout) => _stopped.Wait(timeout);

    /// <summary>
    /// Runs one connection until it is over: reads its token frame, checks the proof for its
    /// index, answers with the ready frame, then reads its frames until the transfer ends or
    /// the connection does. A refused token gets no answer at all: the caller closes the
    /// connection. <paramref name="authenticated"/> is called with the index once the ready
    /// frame went out. Never throws for what the client sends.
    /// </summary>
    public DirectServeResult Serve(Stream stream, Action<int>? authenticated = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var reader = new DirectFrameReader(stream);
        var index = -1;
        try
        {
            if (!reader.TryRead(out var type, out var payload))
            {
                return new DirectServeResult(-1, "The connection closed before its token.");
            }

            if (type != DirectFrameType.Token)
            {
                return new DirectServeResult(-1, $"The first frame is {type}, not a token.");
            }

            var refusal = Register(payload.Span, stream);
            if (refusal.Reason is { } reason)
            {
                return new DirectServeResult(-1, reason);
            }

            index = refusal.Index;
            if (index == 0)
            {
                WriteControl(DirectFrameType.Ready, []);
            }
            else
            {
                DirectFrames.Write(stream, DirectFrameType.Ready, []);
                stream.Flush();
            }

            authenticated?.Invoke(index);
            Run(index, reader);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException or NotSupportedException)
        {
            if (index < 0)
            {
                return new DirectServeResult(-1, "Before the token was accepted: " + ex.Message);
            }

            OnConnectionError(index, ex);
        }

        return new DirectServeResult(index, null);
    }

    /// <summary>
    /// Runs connection <paramref name="index"/>, whose token frame the caller sent and whose
    /// ready frame it read: the side that opened the connection receives, as the client does
    /// for the files that the session sends since 0.9.0. Connection 0 comes first. Returns when
    /// the connection is over; never throws for what the other side sends.
    /// </summary>
    public DirectServeResult Attach(int index, Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        lock (_gate)
        {
            if (IsFinished)
            {
                return new DirectServeResult(-1, "The transfer is over.");
            }

            if (index < 0 || index >= MaxConnections || _connections[index] is not null || (index != 0 && _connections[0] is null))
            {
                return new DirectServeResult(-1, $"Connection {index} cannot be attached now.");
            }

            _connections[index] = new Connection(stream);
            if (index == 0)
            {
                _state = DirectReceiverState.AwaitingBegin;
            }
        }

        try
        {
            Run(index, new DirectFrameReader(stream));
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException or NotSupportedException)
        {
            OnConnectionError(index, ex);
        }

        return new DirectServeResult(index, null);
    }

    /// <summary>
    /// The side that receives cancels the transfer, for <paramref name="reason"/>, in English:
    /// the final frame says cancelled, the connections close and what was written is deleted.
    /// Nothing happens once it is over.
    /// </summary>
    public void Cancel(string reason)
    {
        lock (_gate)
        {
            if (IsFinished)
            {
                return;
            }

            CancelReason = reason;
            CancelledHere = true;
        }

        Finish(DirectReceiverState.Cancelled, DirectFailure.None, null, null);
    }

    private void Run(int index, DirectFrameReader reader)
    {
        if (index == 0)
        {
            RunControl(reader);
        }
        else
        {
            RunData(index, reader);
        }
    }

    /// <summary>
    /// Ends the transfer from the agent side, for <paramref name="reason"/>, in English: the
    /// connections close and what was written is deleted. Nothing happens once it is over.
    /// </summary>
    public void Close(string reason)
    {
        lock (_gate)
        {
            if (IsFinished)
            {
                return;
            }

            ClosedByAgent = true;
        }

        Finish(DirectReceiverState.Failed, DirectFailure.Broken, null, "Closed by the agent: " + reason);
    }

    /// <summary>
    /// The client cancelled the transfer through the channel. A transfer that runs ends as
    /// cancelled; one that already ended because a connection closed, which the cancel of the
    /// client causes, is counted as cancelled too.
    /// </summary>
    public void ClientCancelled(string reason)
    {
        lock (_gate)
        {
            if (IsFinished)
            {
                if (_state == DirectReceiverState.Failed && Failure == DirectFailure.Broken && !ClosedByAgent)
                {
                    _state = DirectReceiverState.Cancelled;
                    Failure = DirectFailure.None;
                    CancelReason = reason;
                    CancelledThroughChannel = true;
                }

                return;
            }

            CancelReason = reason;
            CancelledThroughChannel = true;
        }

        Finish(DirectReceiverState.Cancelled, DirectFailure.None, null, null);
    }

    /// <summary>The content of the final frame for this outcome, with <paramref name="message"/>.</summary>
    public DirectResult Result(string? message) => new(
        _state switch
        {
            DirectReceiverState.Succeeded => DropStatus.Ok,
            DirectReceiverState.Cancelled => DropStatus.Cancelled,
            _ => DropStatus.Error,
        },
        Failure,
        ReceivedBytes,
        Milliseconds,
        message ?? string.Empty);

    private static double Seconds(long ticks) => ticks / (double)Stopwatch.Frequency;

    /// <summary>Checks the token frame of a new connection and registers it; the index when accepted, otherwise why not.</summary>
    private Refused Register(ReadOnlySpan<byte> payload, Stream stream)
    {
        lock (_gate)
        {
            if (IsFinished)
            {
                return new Refused("The transfer is over.", -1);
            }

            if (_tokens.Check(payload, out var index) is { } refusal)
            {
                return new Refused(refusal, -1);
            }

            _connections[index] = new Connection(stream);
            if (index == 0)
            {
                _state = DirectReceiverState.AwaitingBegin;
            }

            return new Refused(null, index);
        }
    }

    /// <summary>Reads connection 0: the list, then blocks, trailers, its end frame or a cancel.</summary>
    private void RunControl(DirectFrameReader reader)
    {
        while (TimedRead(reader, out var type, out var payload))
        {
            if (IsFinished)
            {
                // After the final frame: what the client still sends is dropped until it closes.
                continue;
            }

            switch (_state, type)
            {
                case (_, DirectFrameType.Cancel):
                    lock (_gate)
                    {
                        CancelReason = System.Text.Encoding.UTF8.GetString(payload.Span);
                    }

                    Finish(DirectReceiverState.Cancelled, DirectFailure.None, null, null);
                    break;

                case (DirectReceiverState.AwaitingBegin, DirectFrameType.Begin):
                    OnBegin(payload.Span);
                    break;

                case (DirectReceiverState.Listing, DirectFrameType.FileHeader):
                    OnFileHeader(payload.Span);
                    break;

                case (DirectReceiverState.Listing, DirectFrameType.ListEnd):
                    OnListEnd();
                    break;

                case (DirectReceiverState.Receiving, DirectFrameType.Data):
                    OnData(0, payload.Span);
                    break;

                case (DirectReceiverState.Receiving, DirectFrameType.FileTrailer):
                    OnFileTrailer(payload.Span);
                    break;

                case (DirectReceiverState.Receiving, DirectFrameType.End):
                    OnEnd(0, payload.Span);
                    break;

                default:
                    Fail(DirectFailure.Protocol, null, $"A {type} frame came on connection 0 while the transfer is {_state}.");
                    break;
            }
        }

        if (!IsFinished)
        {
            ConnectionLost(0, "it closed");
        }
    }

    /// <summary>Reads a connection other than 0: blocks, then its end frame.</summary>
    private void RunData(int index, DirectFrameReader reader)
    {
        while (TimedRead(reader, out var type, out var payload))
        {
            if (IsFinished)
            {
                return;
            }

            switch (type)
            {
                case DirectFrameType.Data:
                    OnData(index, payload.Span);
                    break;

                case DirectFrameType.End:
                    OnEnd(index, payload.Span);
                    break;

                default:
                    Fail(DirectFailure.Protocol, null, $"A {type} frame came on connection {index}, which carries blocks only.");
                    return;
            }
        }

        if (!IsFinished)
        {
            ConnectionLost(index, "it closed");
        }
    }

    private bool TimedRead(DirectFrameReader reader, out DirectFrameType type, out ReadOnlyMemory<byte> payload)
    {
        var start = Stopwatch.GetTimestamp();
        try
        {
            return reader.TryRead(out type, out payload);
        }
        finally
        {
            if (!IsFinished)
            {
                Interlocked.Add(ref _readTicks, Stopwatch.GetTimestamp() - start);
            }
        }
    }

    private void OnBegin(ReadOnlySpan<byte> payload)
    {
        if (!DirectFrames.TryParseBegin(payload, out var files, out var bytes))
        {
            Fail(DirectFailure.Protocol, null, "The begin frame is malformed.");
            return;
        }

        lock (_gate)
        {
            _state = DirectReceiverState.Listing;
            _startTicks = Math.Max(1, _clock());
            ExpectedFiles = files;
            ExpectedBytes = bytes;
        }

        if (_hasRoomFor is not null && !_hasRoomFor(bytes))
        {
            Fail(DirectFailure.NoSpace, null, $"{bytes} bytes announced do not fit in the staging folder.");
            return;
        }

        foreach (var item in Items)
        {
            if (!TransferPaths.IsValidName(item.Name))
            {
                Fail(DirectFailure.BadName, item.Name, $"The item name \"{item.Name}\" is not allowed.");
                return;
            }

            if (item.IsDirectory && !TryStorage(() => _storage.CreateDirectory(item.Name), item.Name))
            {
                return;
            }
        }
    }

    private void OnFileHeader(ReadOnlySpan<byte> payload)
    {
        if (!DirectFrames.TryParseFileHeader(payload, out var index, out var file))
        {
            Fail(DirectFailure.Protocol, null, "A file header frame is malformed.");
            return;
        }

        if (index != _headers.Count || index >= ExpectedFiles)
        {
            Fail(DirectFailure.Protocol, file.RelativePath, $"The header of file {index} came out of order ({_headers.Count} of {ExpectedFiles} headers received).");
            return;
        }

        if (!TransferPaths.IsAllowed(file.RelativePath, Items) || !_paths.Add(file.RelativePath))
        {
            Fail(DirectFailure.BadName, file.RelativePath, $"The relative path \"{file.RelativePath}\" is not allowed or came twice.");
            return;
        }

        if (file.Size > ExpectedBytes - _announcedBytes)
        {
            Fail(DirectFailure.SizeMismatch, file.RelativePath, $"The files announce more than the {ExpectedBytes} bytes of the transfer.");
            return;
        }

        _announcedBytes += file.Size;
        _headers.Add(file);
    }

    /// <summary>The whole list came: it is checked, the files are created, and the data may flow.</summary>
    private void OnListEnd()
    {
        if (_headers.Count != ExpectedFiles)
        {
            Fail(DirectFailure.Protocol, null, $"The list ended after {_headers.Count} of the {ExpectedFiles} files announced.");
            return;
        }

        if (_announcedBytes != ExpectedBytes)
        {
            Fail(DirectFailure.SizeMismatch, null, $"The files add up to {_announcedBytes} bytes, the transfer announced {ExpectedBytes}.");
            return;
        }

        foreach (var item in Items)
        {
            if (!item.IsDirectory && !_paths.Contains(item.Name))
            {
                Fail(DirectFailure.Incomplete, item.Name, $"The dropped file \"{item.Name}\" is not in the list.");
                return;
            }
        }

        var files = new FileSlot[_headers.Count];
        for (var i = 0; i < files.Length; i++)
        {
            var file = _headers[i];
            IDirectFile? created = null;
            if (!TryStorage(() => created = _storage.CreateFile(file.RelativePath, file.Size), file.RelativePath))
            {
                foreach (var slot in files.Take(i))
                {
                    slot.Close();
                }

                return;
            }

            files[i] = new FileSlot(file, created!);
        }

        lock (_gate)
        {
            if (IsFinished)
            {
                foreach (var slot in files)
                {
                    slot.Close();
                }

                return;
            }

            _files = files;
            _state = DirectReceiverState.Receiving;
        }

        // The go frame before the verifier, so that its final frame, for a transfer of empty
        // folders only, cannot come first.
        Meter.Start();
        WriteControl(DirectFrameType.Go, []);
        var verifier = new Thread(Verify)
        {
            IsBackground = true,
            Name = "UltimateRDP direct verifier",
        };
        verifier.Start();
        Started?.Invoke();
    }

    /// <summary>A block on connection <paramref name="index"/>: checked, claimed, written at its offset.</summary>
    private void OnData(int index, ReadOnlySpan<byte> payload)
    {
        if (!DirectFrames.TryParseData(payload, out var fileIndex, out var offset, out var data))
        {
            Fail(DirectFailure.Protocol, null, $"A data frame on connection {index} is malformed.");
            return;
        }

        FileSlot slot;
        IDirectFile? handle;
        long block;
        lock (_gate)
        {
            if (IsFinished)
            {
                return;
            }

            if (_state != DirectReceiverState.Receiving)
            {
                Fail(DirectFailure.Protocol, null, $"A data frame came on connection {index} before the go frame.");
                return;
            }

            if (_connections[index]!.Ended)
            {
                Fail(DirectFailure.Protocol, null, $"A data frame came on connection {index} after its end frame.");
                return;
            }

            if (fileIndex >= _files.Length)
            {
                Fail(DirectFailure.Protocol, null, $"A block of file {fileIndex} came on connection {index}; the transfer has {_files.Length} file(s).");
                return;
            }

            slot = _files[fileIndex];
            if (offset % DirectFrames.BlockSize != 0 || offset >= slot.File.Size || data.Length != DirectFrames.BlockLength(slot.File.Size, offset))
            {
                Fail(DirectFailure.Protocol, slot.File.RelativePath, $"A block of {data.Length} bytes at offset {offset} of file {fileIndex}, {slot.File.Size} bytes, is misaligned or out of range.");
                return;
            }

            block = offset / DirectFrames.BlockSize;
            handle = slot.Handle;
            if (slot.Blocks[block] != BlockFree || handle is null)
            {
                Fail(DirectFailure.Protocol, slot.File.RelativePath, $"Block {block} of file {fileIndex} came twice, the second time on connection {index}.");
                return;
            }

            slot.Blocks[block] = BlockClaimed;
            _busy++;
        }

        var start = Stopwatch.GetTimestamp();
        string? error = null;
        try
        {
            handle.Write(offset, data);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ObjectDisposedException)
        {
            error = ex.Message;
        }

        Interlocked.Add(ref _writeTicks, Stopwatch.GetTimestamp() - start);
        lock (_gate)
        {
            if (error is null)
            {
                slot.Blocks[block] = BlockWritten;
                var connection = _connections[index]!;
                connection.Bytes += data.Length;
                connection.DataFrames++;
                _receivedBytes += data.Length;
                Monitor.PulseAll(_gate);
            }
        }

        LeaveBusy();
        if (error is not null)
        {
            Fail(DirectFailure.WriteFailed, slot.File.RelativePath, error);
            return;
        }

        Meter.Add(data.Length);
        DataReceived?.Invoke();
        if (_pacer?.Take(data.Length) is > 0 and var wait)
        {
            Interlocked.Add(ref _pacedMilliseconds, wait);
            Thread.Sleep((int)Math.Min(wait, 1000));
        }
    }

    private void OnFileTrailer(ReadOnlySpan<byte> payload)
    {
        if (!DirectFrames.TryParseFileTrailer(payload, out var index, out var sha256))
        {
            Fail(DirectFailure.Protocol, null, "A file trailer frame is malformed.");
            return;
        }

        lock (_gate)
        {
            if (index >= _files.Length || _files[index].Trailer is not null)
            {
                Fail(DirectFailure.Protocol, null, $"The trailer of file {index} is out of range or came twice.");
                return;
            }

            _files[index].Trailer = sha256.ToArray();
            Monitor.PulseAll(_gate);
        }
    }

    private void OnEnd(int index, ReadOnlySpan<byte> payload)
    {
        if (!DirectFrames.TryParseEnd(payload, out var count))
        {
            Fail(DirectFailure.Protocol, null, $"The end frame of connection {index} is malformed.");
            return;
        }

        lock (_gate)
        {
            var connection = _connections[index]!;
            if (connection.Ended || count != connection.DataFrames)
            {
                Fail(DirectFailure.Protocol, null, connection.Ended
                    ? $"Connection {index} sent its end frame twice."
                    : $"Connection {index} says it carried {count} block(s); {connection.DataFrames} arrived.");
                return;
            }

            connection.Ended = true;
            Monitor.PulseAll(_gate);
        }
    }

    /// <summary>
    /// The verifier, on a thread of its own from the go frame: for each file in order, reads
    /// back each block once it is written, hashes it, and once the whole file is hashed and
    /// its trailer came, compares. The blocks stay in the file only, never in memory.
    /// </summary>
    private void Verify()
    {
        var buffer = new byte[DirectFrames.BlockSize];
        var digest = new byte[DirectFrames.HashLength];
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        try
        {
            for (var fileIndex = 0; fileIndex < _files.Length; fileIndex++)
            {
                var slot = _files[fileIndex];
                var blocks = DirectFrames.BlockCount(slot.File.Size);
                for (long block = 0; block < blocks; block++)
                {
                    if (!WaitFor(fileIndex, () => slot.Blocks[block] == BlockWritten, $"Block {block} of the file never came."))
                    {
                        return;
                    }

                    var offset = block * DirectFrames.BlockSize;
                    var length = DirectFrames.BlockLength(slot.File.Size, offset);
                    var start = Stopwatch.GetTimestamp();
                    string? error = null;
                    try
                    {
                        var read = 0;
                        while (read < length && slot.Handle is { } handle && handle.Read(offset + read, buffer.AsSpan(read, length - read)) is > 0 and var count)
                        {
                            read += count;
                        }

                        if (read != length)
                        {
                            error = $"Block {block} reads back shorter than {length} bytes.";
                        }
                        else
                        {
                            hash.AppendData(buffer, 0, length);
                        }
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ObjectDisposedException)
                    {
                        error = ex.Message;
                    }

                    Interlocked.Add(ref _hashTicks, Stopwatch.GetTimestamp() - start);
                    LeaveBusy();
                    if (error is not null)
                    {
                        Fail(DirectFailure.WriteFailed, slot.File.RelativePath, error);
                        return;
                    }

                    lock (_gate)
                    {
                        slot.Checked += length;
                    }

                    SendAckIfDue(fileIndex);
                }

                if (!WaitFor(fileIndex, () => slot.Trailer is not null, "Its trailer never came."))
                {
                    return;
                }

                LeaveBusy();
                hash.GetHashAndReset(digest);
                if (!CryptographicOperations.FixedTimeEquals(digest, slot.Trailer))
                {
                    Fail(DirectFailure.HashMismatch, slot.File.RelativePath, $"The SHA-256 of the file is {TransferHash.ToHex(digest)}; the client sent {TransferHash.ToHex(slot.Trailer)}.");
                    return;
                }

                lock (_gate)
                {
                    slot.Close();
                }

                if (!TryStorage(() => _storage.CompleteFile(slot.File.RelativePath, slot.File.Modified), slot.File.RelativePath))
                {
                    return;
                }

                long total;
                lock (_gate)
                {
                    ReceivedFiles++;
                    total = _receivedBytes;
                    _bytesAtLastAck = total;
                }

                SendAck(new DirectAck(fileIndex, slot.File.Size, total));
            }

            Finish(DirectReceiverState.Succeeded, DirectFailure.None, null, null);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException or NotSupportedException)
        {
            // Connection 0 failed under an acknowledgement.
            if (!IsFinished)
            {
                ConnectionLost(0, ex.Message);
            }
        }
    }

    /// <summary>
    /// Waits until <paramref name="ready"/> holds, sending the acknowledgements that fall due
    /// meanwhile. True with the receiver marked busy, so that the files stay open; false when
    /// the transfer ended, or failed as incomplete because every connection sent its end frame
    /// and what is awaited never came.
    /// </summary>
    private bool WaitFor(int fileIndex, Func<bool> ready, string missing)
    {
        while (true)
        {
            DirectAck? due;
            var incomplete = false;
            lock (_gate)
            {
                if (IsFinished)
                {
                    return false;
                }

                if (ready())
                {
                    _busy++;
                    return true;
                }

                due = AckDue(fileIndex);
                if (due is null)
                {
                    incomplete = AllEnded();
                    if (!incomplete)
                    {
                        Monitor.Wait(_gate, VerifierWaitMilliseconds);
                        continue;
                    }
                }
            }

            if (incomplete)
            {
                Fail(DirectFailure.Incomplete, _files[fileIndex].File.RelativePath, $"Every connection sent its end frame, and file {fileIndex} is incomplete. {missing}");
                return false;
            }

            SendAck(due!.Value);
        }
    }

    private void SendAckIfDue(int fileIndex)
    {
        DirectAck? due;
        lock (_gate)
        {
            due = AckDue(fileIndex);
        }

        if (due is { } ack)
        {
            SendAck(ack);
        }
    }

    /// <summary>An acknowledgement when <see cref="DirectFrames.AckIntervalBytes"/> arrived since the last one. Under the lock.</summary>
    private DirectAck? AckDue(int fileIndex)
    {
        if (_receivedBytes - _bytesAtLastAck < DirectFrames.AckIntervalBytes)
        {
            return null;
        }

        _bytesAtLastAck = _receivedBytes;
        return new DirectAck(fileIndex, _files[fileIndex].Checked, _receivedBytes);
    }

    /// <summary>True when every connection that carries blocks sent its end frame. Under the lock.</summary>
    private bool AllEnded() => _connections.All(connection => connection is null || connection.Ended || connection.Dropped);

    private void SendAck(DirectAck ack)
    {
        WriteControl(DirectFrameType.Ack, DirectFrames.AckPayload(ack));
        lock (_gate)
        {
            AcksSent++;
        }
    }

    /// <summary>Writes a frame on connection 0; the threads that write there take turns.</summary>
    private void WriteControl(DirectFrameType type, ReadOnlySpan<byte> payload)
    {
        Stream stream;
        lock (_gate)
        {
            stream = _connections[0]!.Stream;
        }

        lock (_controlWrite)
        {
            DirectFrames.Write(stream, type, payload);
            stream.Flush();
        }
    }

    private void OnConnectionError(int index, Exception ex)
    {
        if (IsFinished)
        {
            // The transfer is over and its connections are closing: nothing is wrong.
            return;
        }

        if (ex is DirectProtocolException)
        {
            Fail(DirectFailure.Protocol, null, $"Connection {index}: {ex.Message}");
            return;
        }

        ConnectionLost(index, ex.Message);
    }

    /// <summary>
    /// A connection ended or failed while the transfer runs. One other than 0 that carried no
    /// block yet is dropped and the transfer goes on; any other ends the transfer as broken.
    /// </summary>
    private void ConnectionLost(int index, string detail)
    {
        lock (_gate)
        {
            if (IsFinished)
            {
                return;
            }

            var connection = _connections[index]!;
            if (index != 0 && connection.Ended)
            {
                // After its end frame a connection has nothing more to carry.
                return;
            }

            if (index != 0 && connection.DataFrames == 0)
            {
                connection.Dropped = true;
                Monitor.PulseAll(_gate);
                index = -index;
            }
        }

        if (index < 0)
        {
            ConnectionDropped?.Invoke(-index, detail);
            return;
        }

        Fail(DirectFailure.Broken, null, $"Connection {index} was lost: {detail}");
    }

    /// <summary>Runs a storage call; a file error fails the transfer and returns false.</summary>
    private bool TryStorage(Action action, string relativePath)
    {
        try
        {
            action();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            Fail(DirectFailure.WriteFailed, relativePath, ex.Message);
            return false;
        }
    }

    private void Fail(DirectFailure failure, string? relativePath, string detail) =>
        Finish(DirectReceiverState.Failed, failure, relativePath, detail);

    /// <summary>
    /// Ends the transfer once. The connections other than 0 close first; the final frame goes
    /// out on connection 0 unless a connection broke, and connection 0 closes when it cannot
    /// carry one. Then <see cref="Stopped"/> says that the transfer is over. The files are
    /// closed and, unless it succeeded, deleted afterwards, on a thread of their own, as soon
    /// as no thread writes or reads them: nothing waits for the file system to end a transfer.
    /// </summary>
    private void Finish(DirectReceiverState state, DirectFailure failure, string? relativePath, string? detail)
    {
        Stream? control;
        List<Stream> others = [];
        bool cleanUp;
        lock (_gate)
        {
            if (IsFinished)
            {
                return;
            }

            _endTicks = _clock();
            _finishStamp = Stopwatch.GetTimestamp();
            Failure = failure;
            FailurePath = relativePath;
            FailureDetail = detail;
            _state = state;
            control = _connections[0]?.Stream;
            for (var i = 1; i < _connections.Length; i++)
            {
                if (_connections[i] is { } connection)
                {
                    others.Add(connection.Stream);
                }
            }

            cleanUp = _busy == 0 && !_cleanedUp;
            _cleanedUp |= cleanUp;
            Monitor.PulseAll(_gate);
        }

        Meter.Stop();
        foreach (var stream in others)
        {
            Dispose(stream);
        }

        var sendFinal = control is not null && failure is not (DirectFailure.Token or DirectFailure.Broken);
        if (sendFinal)
        {
            try
            {
                Finishing?.Invoke();
                WriteControl(DirectFrameType.Final, DirectFrames.FinalPayload(Result(_describe(this))));
                FinalWasSent = true;
                FinalSent?.Invoke();
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException or NotSupportedException)
            {
                // The client is gone; the outcome stays as it is.
                sendFinal = false;
            }
        }

        if (!sendFinal && control is not null)
        {
            Dispose(control);
        }

        _stopped.Set();
        Stopped?.Invoke();
        if (cleanUp)
        {
            StartCleanUp();
        }
    }

    /// <summary>A thread is done with the files; the last one after the end starts the clean up.</summary>
    private void LeaveBusy()
    {
        bool cleanUp;
        lock (_gate)
        {
            _busy--;
            cleanUp = IsFinished && _busy == 0 && !_cleanedUp;
            _cleanedUp |= cleanUp;
        }

        if (cleanUp)
        {
            StartCleanUp();
        }
    }

    /// <summary>The clean up runs on a thread of its own, so that no caller, the channel thread included, waits for the file system.</summary>
    private void StartCleanUp()
    {
        var thread = new Thread(CleanUp)
        {
            IsBackground = true,
            Name = "UltimateRDP direct clean up",
        };
        thread.Start();
    }

    private void CleanUp()
    {
        var start = Stopwatch.GetTimestamp();
        FileSlot[] files;
        long finished;
        lock (_gate)
        {
            files = _files;
            finished = _finishStamp;
            _tokens.Clear();
        }

        foreach (var slot in files)
        {
            slot.Close();
        }

        var closed = Stopwatch.GetTimestamp();
        if (_state != DirectReceiverState.Succeeded)
        {
            try
            {
                _storage.DeleteAll();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The storage reports its own failures; the transfer is over all the same.
            }
        }

        var deleted = Stopwatch.GetTimestamp();
        CleanUpTimes = new DirectCleanUpTimes(
            TicksToMilliseconds(start - finished),
            TicksToMilliseconds(closed - start),
            _state == DirectReceiverState.Succeeded ? null : TicksToMilliseconds(deleted - closed));
        _ended.Set();
        Ended?.Invoke();
    }

    private static long TicksToMilliseconds(long ticks) => ticks * 1000 / Stopwatch.Frequency;

    private static void Dispose(Stream stream)
    {
        try
        {
            stream.Dispose();
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // Closing is all that is asked.
        }
    }

    /// <summary>The answer to a token frame: a reason when refused, the index when accepted.</summary>
    private readonly record struct Refused(string? Reason, int Index);

    /// <summary>One accepted connection.</summary>
    private sealed class Connection(Stream stream)
    {
        public Stream Stream { get; } = stream;

        public long Bytes { get; set; }

        public long DataFrames { get; set; }

        public bool Ended { get; set; }

        public bool Dropped { get; set; }
    }

    /// <summary>One file: its blocks, its trailer, and how far the verifier got.</summary>
    private sealed class FileSlot(TransferFile file, IDirectFile handle)
    {
        public TransferFile File { get; } = file;

        public IDirectFile? Handle { get; private set; } = handle;

        public byte[] Blocks { get; } = new byte[DirectFrames.BlockCount(file.Size)];

        public byte[]? Trailer { get; set; }

        public long Checked { get; set; }

        public void Close()
        {
            try
            {
                Handle?.Dispose();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The transfer already ended; the file is deleted with the rest when it failed.
            }

            Handle = null;
        }
    }
}
