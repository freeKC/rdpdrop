using System.Security.Cryptography;
using UltimateRDP.Core.Protocol;
using UltimateRDP.Core.Transfer;

namespace UltimateRDP.Core.Direct;

/// <summary>Where a <see cref="DirectReceiver"/> stands.</summary>
public enum DirectReceiverState
{
    /// <summary>Waiting for the token frame.</summary>
    AwaitingToken,

    /// <summary>The token was right; waiting for the begin frame.</summary>
    AwaitingBegin,

    /// <summary>Receiving files.</summary>
    Receiving,

    /// <summary>Every file arrived and was checked.</summary>
    Succeeded,

    /// <summary>The transfer failed; see <see cref="DirectReceiver.Failure"/>.</summary>
    Failed,

    /// <summary>The client cancelled the transfer.</summary>
    Cancelled,
}

/// <summary>
/// The agent side of a direct transfer, over a stream that already did its TLS handshake:
/// checks the token, then receives the files of the transfer frame by frame
/// (<see cref="DirectFrames"/>), checks their order, paths, sizes and SHA-256 as
/// <see cref="TransferReceiver"/> does for the channel, writes them to an
/// <see cref="ITransferStorage"/>, acknowledges every <see cref="DirectFrames.AckIntervalBytes"/>
/// and at the end of each file, and ends with the final frame. Anything that is not a
/// success deletes what was written.
/// </summary>
/// <remarks>
/// <see cref="Run"/> reads and writes on the calling thread. The caller bounds the time:
/// it closes the stream when the token does not come in time, gives the stream a read
/// timeout, and closes the stream to stop the transfer from the agent side, which ends it
/// as broken. This class is not thread safe apart from <see cref="State"/> and
/// <see cref="ReceivedBytes"/>, which the log may read from another thread.
/// </remarks>
public sealed class DirectReceiver
{
    private readonly byte[] _token;
    private readonly ITransferStorage _storage;
    private readonly Func<long> _clock;
    private readonly Func<long, bool>? _hasRoomFor;
    private readonly HashSet<string> _paths = new(StringComparer.OrdinalIgnoreCase);
    private readonly byte[] _digest = new byte[DirectFrames.HashLength];
    private IncrementalHash? _hash;
    private Stream? _file;
    private TransferFile? _current;
    private int _currentIndex;
    private long _fileReceived;
    private long _announcedBytes;
    private long _bytesAtLastAck;
    private long _startTicks;
    private long _endTicks;
    private volatile DirectReceiverState _state = DirectReceiverState.AwaitingToken;
    private long _receivedBytes;

    /// <param name="token">The token of the offer, <see cref="DirectFrames.TokenLength"/> bytes.</param>
    /// <param name="items">The dropped items; every relative path must start with one of their names.</param>
    /// <param name="storage">Where the files go.</param>
    /// <param name="clockMilliseconds">A clock in milliseconds, for the time of the final frame.</param>
    /// <param name="hasRoomFor">Says whether the announced bytes fit in the staging folder; null skips the check.</param>
    public DirectReceiver(ReadOnlySpan<byte> token, IReadOnlyList<TransferItem> items, ITransferStorage storage, Func<long> clockMilliseconds, Func<long, bool>? hasRoomFor = null)
    {
        if (token.Length != DirectFrames.TokenLength)
        {
            throw new ArgumentException("A token has 32 bytes.", nameof(token));
        }

        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(clockMilliseconds);
        _token = token.ToArray();
        Items = items;
        _storage = storage;
        _clock = clockMilliseconds;
        _hasRoomFor = hasRoomFor;
    }

    /// <summary>
    /// Raised on the thread of <see cref="Run"/> once the token was found right and the ready
    /// frame went out, so that the caller can stop the deadline of the handshake.
    /// </summary>
    public event Action? Authenticated;

    /// <summary>Raised on the thread of <see cref="Run"/> after each data frame was written: the transfer is alive.</summary>
    public event Action? DataReceived;

    /// <summary>Raised on the thread of <see cref="Run"/> once the final frame went out, before the stream is drained.</summary>
    public event Action? FinalSent;

    /// <summary>The dropped items.</summary>
    public IReadOnlyList<TransferItem> Items { get; }

    /// <summary>Where the transfer stands. Readable from any thread.</summary>
    public DirectReceiverState State => _state;

    /// <summary>True once the transfer succeeded, failed or was cancelled.</summary>
    public bool IsFinished => _state is DirectReceiverState.Succeeded or DirectReceiverState.Failed or DirectReceiverState.Cancelled;

    /// <summary>Why the transfer failed, when <see cref="State"/> is <see cref="DirectReceiverState.Failed"/>.</summary>
    public DirectFailure Failure { get; private set; }

    /// <summary>Relative path of the file concerned by the failure, when there is one.</summary>
    public string? FailurePath { get; private set; }

    /// <summary>The failure in English, for the log.</summary>
    public string? FailureDetail { get; private set; }

    /// <summary>The reason the client gave with its cancel frame, in English.</summary>
    public string? CancelReason { get; private set; }

    /// <summary>Files announced by the begin frame.</summary>
    public int ExpectedFiles { get; private set; }

    /// <summary>Bytes announced by the begin frame.</summary>
    public long ExpectedBytes { get; private set; }

    /// <summary>Files received and checked.</summary>
    public int ReceivedFiles { get; private set; }

    /// <summary>Bytes received and written. Readable from any thread.</summary>
    public long ReceivedBytes => Interlocked.Read(ref _receivedBytes);

    /// <summary>Acknowledgements sent.</summary>
    public int AcksSent { get; private set; }

    /// <summary>True once the final frame went out.</summary>
    public bool FinalWasSent { get; private set; }

    /// <summary>Milliseconds from the begin frame to the end, or until now while it runs.</summary>
    public long Milliseconds => _startTicks == 0 ? 0 : Math.Max(0, (IsFinished ? _endTicks : _clock()) - _startTicks);

    /// <summary>
    /// Runs the transfer over <paramref name="stream"/> until it is over: checks the token,
    /// receives the files and sends the final frame, whose text <paramref name="describe"/>
    /// gives in the language of the client. A wrong token, or a stream that ends or fails,
    /// gets no final frame. After the final frame the rest of what the client sends is read
    /// and dropped until it closes, so that the final frame is not lost to a reset. Never
    /// throws for what the client sends; the errors of the storage end the transfer.
    /// </summary>
    public void Run(Stream stream, Func<DirectReceiver, string?> describe)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(describe);
        var reader = new DirectFrameReader(stream);
        try
        {
            while (!IsFinished)
            {
                if (!reader.TryRead(out var type, out var payload))
                {
                    Fail(DirectFailure.Broken, null, "The client closed the connection before the end of the transfer.");
                    break;
                }

                Handle(stream, type, payload.Span);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            if (!IsFinished)
            {
                Fail(ex is DirectProtocolException ? DirectFailure.Protocol : DirectFailure.Broken, null, ex.Message);
            }
        }

        if (Failure is DirectFailure.Token or DirectFailure.Broken)
        {
            // A wrong token gets no answer at all, and a broken stream cannot carry one.
            return;
        }

        try
        {
            DirectFrames.Write(stream, DirectFrameType.Final, DirectFrames.FinalPayload(Result(describe(this))));
            stream.Flush();
            FinalWasSent = true;
            FinalSent?.Invoke();
            Drain(stream);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // The client is gone; the outcome stays as it is.
        }
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

    private void Handle(Stream stream, DirectFrameType type, ReadOnlySpan<byte> payload)
    {
        switch (_state, type)
        {
            case (DirectReceiverState.AwaitingToken, DirectFrameType.Token):
                OnToken(stream, payload);
                break;

            case (DirectReceiverState.AwaitingToken, _):
                Fail(DirectFailure.Token, null, $"The first frame is {type}, not the token.");
                break;

            case (DirectReceiverState.AwaitingBegin, DirectFrameType.Begin):
                OnBegin(payload);
                break;

            case (DirectReceiverState.Receiving, DirectFrameType.FileHeader):
                OnFileHeader(payload);
                break;

            case (DirectReceiverState.Receiving, DirectFrameType.Data):
                OnData(stream, payload);
                break;

            case (DirectReceiverState.Receiving, DirectFrameType.FileTrailer):
                OnFileTrailer(stream, payload);
                break;

            case (DirectReceiverState.AwaitingBegin or DirectReceiverState.Receiving, DirectFrameType.Cancel):
                CancelReason = System.Text.Encoding.UTF8.GetString(payload);
                Finish(DirectReceiverState.Cancelled);
                break;

            default:
                Fail(DirectFailure.Protocol, _current?.RelativePath, $"A {type} frame came while the transfer is {_state}{(_current is null ? string.Empty : ", file " + _currentIndex + " open")}.");
                break;
        }
    }

    private void OnToken(Stream stream, ReadOnlySpan<byte> payload)
    {
        // Compared in constant time, so that the time of the answer tells nothing of the token.
        if (payload.Length != 1 + DirectFrames.TokenLength || payload[0] != DirectFrames.ProtocolVersion || !CryptographicOperations.FixedTimeEquals(payload[1..], _token))
        {
            Fail(DirectFailure.Token, null, payload.Length > 0 && payload[0] != DirectFrames.ProtocolVersion
                ? $"The client speaks version {payload[0]} of the direct connection; this agent speaks {DirectFrames.ProtocolVersion}."
                : "The token is not the one of the offer.");
            return;
        }

        // Single use: the token cannot open a second transfer.
        CryptographicOperations.ZeroMemory(_token);
        _state = DirectReceiverState.AwaitingBegin;
        DirectFrames.Write(stream, DirectFrameType.Ready, []);
        stream.Flush();
        Authenticated?.Invoke();
    }

    private void OnBegin(ReadOnlySpan<byte> payload)
    {
        if (!DirectFrames.TryParseBegin(payload, out var files, out var bytes))
        {
            Fail(DirectFailure.Protocol, null, "The begin frame is malformed.");
            return;
        }

        _state = DirectReceiverState.Receiving;
        _startTicks = Math.Max(1, _clock());
        ExpectedFiles = files;
        ExpectedBytes = bytes;
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

        if (ExpectedFiles == 0)
        {
            Complete();
        }
    }

    private void OnFileHeader(ReadOnlySpan<byte> payload)
    {
        if (!DirectFrames.TryParseFileHeader(payload, out var index, out var file))
        {
            Fail(DirectFailure.Protocol, null, "A file header frame is malformed.");
            return;
        }

        if (_current is not null || index != ReceivedFiles || ReceivedFiles >= ExpectedFiles)
        {
            Fail(DirectFailure.Protocol, file.RelativePath, $"The header of file {index} came out of order ({ReceivedFiles} of {ExpectedFiles} files received, a file {(_current is null ? "is not" : "is")} open).");
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

        Stream? created = null;
        if (!TryStorage(() => created = _storage.CreateFile(file.RelativePath), file.RelativePath))
        {
            return;
        }

        _announcedBytes += file.Size;
        _current = file;
        _currentIndex = index;
        _file = created;
        _fileReceived = 0;
        _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    }

    private void OnData(Stream stream, ReadOnlySpan<byte> payload)
    {
        if (_current is not { } file || payload.Length == 0)
        {
            Fail(DirectFailure.Protocol, null, payload.Length == 0 ? "A data frame is empty." : "A data frame came while no file is open.");
            return;
        }

        if (payload.Length > file.Size - _fileReceived)
        {
            Fail(DirectFailure.SizeMismatch, file.RelativePath, $"The file goes past its announced size of {file.Size} bytes.");
            return;
        }

        var target = _file!;
        try
        {
            target.Write(payload);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            Fail(DirectFailure.WriteFailed, file.RelativePath, ex.Message);
            return;
        }

        _hash!.AppendData(payload);
        _fileReceived += payload.Length;
        var total = Interlocked.Add(ref _receivedBytes, payload.Length);
        DataReceived?.Invoke();
        if (total - _bytesAtLastAck >= DirectFrames.AckIntervalBytes)
        {
            SendAck(stream);
        }
    }

    private void OnFileTrailer(Stream stream, ReadOnlySpan<byte> payload)
    {
        if (!DirectFrames.TryParseFileTrailer(payload, out var index, out var sha256))
        {
            Fail(DirectFailure.Protocol, _current?.RelativePath, "A file trailer frame is malformed.");
            return;
        }

        if (_current is not { } file || index != _currentIndex)
        {
            Fail(DirectFailure.Protocol, _current?.RelativePath, $"The trailer of file {index} came while file {(_current is null ? "(none)" : _currentIndex)} is open.");
            return;
        }

        if (_fileReceived != file.Size)
        {
            Fail(DirectFailure.SizeMismatch, file.RelativePath, $"The file announced {file.Size} bytes and {_fileReceived} arrived.");
            return;
        }

        _hash!.GetHashAndReset(_digest);
        if (!CryptographicOperations.FixedTimeEquals(_digest, sha256))
        {
            Fail(DirectFailure.HashMismatch, file.RelativePath, $"The SHA-256 of the file is {TransferHash.ToHex(_digest)}; the client sent {TransferHash.ToHex(sha256)}.");
            return;
        }

        var target = _file!;
        if (!TryStorage(target.Dispose, file.RelativePath))
        {
            return;
        }

        _file = null;
        if (!TryStorage(() => _storage.CompleteFile(file.RelativePath, file.Modified), file.RelativePath))
        {
            return;
        }

        CloseFile();
        ReceivedFiles++;
        SendAck(stream, index, file.Size);
        if (ReceivedFiles == ExpectedFiles)
        {
            Complete();
        }
    }

    private void SendAck(Stream stream) => SendAck(stream, _currentIndex, _fileReceived);

    private void SendAck(Stream stream, int index, long fileBytes)
    {
        _bytesAtLastAck = ReceivedBytes;
        DirectFrames.Write(stream, DirectFrameType.Ack, DirectFrames.AckPayload(new DirectAck(index, fileBytes, ReceivedBytes)));
        stream.Flush();
        AcksSent++;
    }

    private void Complete()
    {
        if (ReceivedBytes != ExpectedBytes)
        {
            Fail(DirectFailure.SizeMismatch, null, $"The transfer announced {ExpectedBytes} bytes and {ReceivedBytes} arrived.");
            return;
        }

        foreach (var item in Items)
        {
            if (!item.IsDirectory && !_paths.Contains(item.Name))
            {
                Fail(DirectFailure.Incomplete, item.Name, $"The dropped file \"{item.Name}\" did not arrive.");
                return;
            }
        }

        Finish(DirectReceiverState.Succeeded);
    }

    /// <summary>Reads and drops what the client still sends, until it closes or the stream times out.</summary>
    private static void Drain(Stream stream)
    {
        var buffer = new byte[64 * 1024];
        while (stream.Read(buffer, 0, buffer.Length) > 0)
        {
        }
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

    private void Fail(DirectFailure failure, string? relativePath, string detail)
    {
        Failure = failure;
        FailurePath = relativePath;
        FailureDetail = detail;
        Finish(DirectReceiverState.Failed);
    }

    private void Finish(DirectReceiverState state)
    {
        _endTicks = _clock();
        _state = state;
        CloseFile();
        if (state != DirectReceiverState.Succeeded)
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
    }

    private void CloseFile()
    {
        try
        {
            _file?.Dispose();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The transfer already failed or ended; the file is deleted with the rest.
        }

        _file = null;
        _current = null;
        _hash?.Dispose();
        _hash = null;
    }
}
