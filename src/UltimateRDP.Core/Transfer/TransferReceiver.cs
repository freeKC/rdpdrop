using System.Security.Cryptography;
using UltimateRDP.Core.Protocol;

namespace UltimateRDP.Core.Transfer;

/// <summary>Where the files of a transfer are written. Paths are relative to the folder of the transfer.</summary>
public interface ITransferStorage
{
    /// <summary>Creates a folder, for a dropped folder that may hold no file.</summary>
    void CreateDirectory(string relativePath);

    /// <summary>Creates a new file, and its folders, for writing.</summary>
    Stream CreateFile(string relativePath);

    /// <summary>Called once a file is closed and checked; sets its last write time when known.</summary>
    void CompleteFile(string relativePath, DateTime? modified);

    /// <summary>Deletes everything written for the transfer, after a failure or a cancel.</summary>
    void DeleteAll();
}

/// <summary>State of a <see cref="TransferReceiver"/>.</summary>
public enum TransferState
{
    /// <summary>Waiting for <c>transferBegin</c>.</summary>
    Waiting,

    /// <summary>Receiving files.</summary>
    Receiving,

    /// <summary>Every file arrived and was checked.</summary>
    Succeeded,

    /// <summary>The transfer failed; see <see cref="TransferReceiver.Failure"/>.</summary>
    Failed,

    /// <summary>The client or the agent cancelled the transfer.</summary>
    Cancelled,
}

/// <summary>Why a transfer failed.</summary>
public enum TransferFailure
{
    /// <summary>No failure.</summary>
    None,

    /// <summary>A message came out of order: a chunk before its file, a file before the previous one ended.</summary>
    OutOfOrder,

    /// <summary>
    /// A message of the client never arrived: <see cref="TransferReceiver.LostMessage"/>
    /// names it. A later message shows that it was sent, so the line was lost on the way.
    /// </summary>
    MessageLost,

    /// <summary>A chunk did not start where the previous one ended: data was lost on the way.</summary>
    DataLost,

    /// <summary>
    /// The data of a chunk is not valid in its encoding, the chunk is too long, or the
    /// encoding is unknown or not the one announced by <c>transferBegin</c>.
    /// </summary>
    BadData,

    /// <summary>A file did not have its announced size, or the transfer more bytes than announced.</summary>
    SizeMismatch,

    /// <summary>The checksum of a file does not match.</summary>
    HashMismatch,

    /// <summary>A relative path is not allowed, or names a file a second time.</summary>
    BadName,

    /// <summary>Writing a file failed.</summary>
    WriteFailed,

    /// <summary>The transfer ended without some of the dropped files.</summary>
    Incomplete,
}

/// <summary>
/// Receiving side of a transfer: checks the order of the messages, the offsets, the sizes
/// and the checksums, writes the bytes to an <see cref="ITransferStorage"/> and decides
/// when to acknowledge. Any problem fails the whole transfer and deletes what was written.
/// </summary>
/// <remarks>This class is not thread safe.</remarks>
public sealed class TransferReceiver : IDisposable
{
    /// <summary>An acknowledgement goes out at least every this many bytes received.</summary>
    public const long AckInterval = 2L * 1024 * 1024;

    private readonly ITransferStorage _storage;
    private readonly Func<long> _clock;
    private readonly HashSet<string> _paths = new(StringComparer.OrdinalIgnoreCase);
    private readonly byte[] _buffer = new byte[TransferSender.MaxChunkSize];
    private IncrementalHash? _hash;
    private Stream? _stream;
    private FileBeginMessage? _file;
    private long _fileReceived;
    private long _announcedBytes;
    private long _bytesAtLastAck;
    private long _startTicks;
    private long _endTicks;

    /// <param name="transferId">Identifier of the transfer.</param>
    /// <param name="items">The dropped items; every relative path must start with one of their names.</param>
    /// <param name="storage">Where the files go.</param>
    /// <param name="clockMilliseconds">A clock in milliseconds, for <see cref="Seconds"/>.</param>
    public TransferReceiver(string transferId, IReadOnlyList<TransferItem> items, ITransferStorage storage, Func<long> clockMilliseconds)
    {
        ArgumentException.ThrowIfNullOrEmpty(transferId);
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(clockMilliseconds);
        TransferId = transferId;
        Items = items;
        _storage = storage;
        _clock = clockMilliseconds;
    }

    /// <summary>Identifier of the transfer.</summary>
    public string TransferId { get; }

    /// <summary>The dropped items.</summary>
    public IReadOnlyList<TransferItem> Items { get; }

    /// <summary>Where the transfer stands.</summary>
    public TransferState State { get; private set; } = TransferState.Waiting;

    /// <summary>True once the transfer succeeded, failed or was cancelled.</summary>
    public bool IsFinished => State is TransferState.Succeeded or TransferState.Failed or TransferState.Cancelled;

    /// <summary>Why the transfer failed, when <see cref="State"/> is <see cref="TransferState.Failed"/>.</summary>
    public TransferFailure Failure { get; private set; }

    /// <summary>Relative path of the file concerned by the failure, when there is one.</summary>
    public string? FailurePath { get; private set; }

    /// <summary>The failure in English, for the log.</summary>
    public string? FailureDetail { get; private set; }

    /// <summary>
    /// The type of the message that never arrived, when <see cref="Failure"/> is
    /// <see cref="TransferFailure.MessageLost"/>: <c>transferBegin</c>, <c>fileBegin</c> or <c>fileEnd</c>.
    /// </summary>
    public string? LostMessage { get; private set; }

    /// <summary>The file of <see cref="LostMessage"/>, or null for <c>transferBegin</c>.</summary>
    public int? LostFileIndex { get; private set; }

    /// <summary>Files announced by <c>transferBegin</c>.</summary>
    public int ExpectedFiles { get; private set; }

    /// <summary>Bytes announced by <c>transferBegin</c>.</summary>
    public long ExpectedBytes { get; private set; }

    /// <summary>The encoding of the chunks announced by <c>transferBegin</c>, one of <see cref="ChunkEncodings"/>.</summary>
    public string ChunkEncoding { get; private set; } = ChunkEncodings.Base64;

    /// <summary>Files received and checked.</summary>
    public int ReceivedFiles { get; private set; }

    /// <summary>Bytes received and written.</summary>
    public long ReceivedBytes { get; private set; }

    /// <summary>Seconds from <c>transferBegin</c> to the end, or until now while it runs.</summary>
    public double Seconds => State == TransferState.Waiting ? 0 : Math.Max(0, ((IsFinished ? _endTicks : _clock()) - _startTicks) / 1000.0);

    /// <summary>
    /// Handles one message of this transfer. Returns the acknowledgement to send, if one is
    /// due. Check <see cref="IsFinished"/> afterwards: once it is true, the result must be
    /// sent. Messages of another transfer, and messages after the end, are ignored.
    /// </summary>
    public FileAckMessage? Handle(Message message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (IsFinished)
        {
            return null;
        }

        if (State == TransferState.Waiting && message is FileBeginMessage or FileChunkMessage or FileEndMessage && TransferIdOf(message) == TransferId)
        {
            // Said apart from the other order errors: the line of transferBegin was lost.
            return Lost(MessageTypes.TransferBegin, null, (message as FileBeginMessage)?.RelativePath, $"{message.Type} came before transferBegin: no transferBegin arrived for this transfer.");
        }

        return message switch
        {
            TransferBeginMessage begin when begin.TransferId == TransferId => OnBegin(begin),
            FileBeginMessage file when file.TransferId == TransferId => OnFileBegin(file),
            FileChunkMessage chunk when chunk.TransferId == TransferId => OnChunk(chunk),
            FileEndMessage end when end.TransferId == TransferId => OnFileEnd(end),
            TransferCancelMessage cancel when cancel.TransferId == TransferId => Cancel(),
            _ => null,
        };
    }

    /// <summary>The transfer id of a file message, or null for another message.</summary>
    private static string? TransferIdOf(Message message) => message switch
    {
        FileBeginMessage file => file.TransferId,
        FileChunkMessage chunk => chunk.TransferId,
        FileEndMessage end => end.TransferId,
        _ => null,
    };

    /// <summary>Cancels the transfer and deletes what was written. Does nothing once it is finished.</summary>
    public FileAckMessage? Cancel()
    {
        if (!IsFinished)
        {
            Finish(TransferState.Cancelled);
        }

        return null;
    }

    /// <summary>Cancels the transfer when it is not finished, and closes the open file.</summary>
    public void Dispose()
    {
        if (!IsFinished)
        {
            Cancel();
        }

        CloseFile();
    }

    private FileAckMessage? OnBegin(TransferBeginMessage begin)
    {
        if (State != TransferState.Waiting)
        {
            return Fail(TransferFailure.OutOfOrder, null, "transferBegin came twice.");
        }

        State = TransferState.Receiving;
        _startTicks = _clock();
        ExpectedFiles = begin.Files;
        ExpectedBytes = begin.Bytes;
        if (!ChunkEncodings.IsKnown(begin.ChunkEncoding))
        {
            return Fail(TransferFailure.BadData, null, $"transferBegin announces the chunk encoding \"{begin.ChunkEncoding}\", which this agent does not read.");
        }

        ChunkEncoding = ChunkEncodings.OrDefault(begin.ChunkEncoding);
        foreach (var item in Items)
        {
            if (!TransferPaths.IsValidName(item.Name))
            {
                return Fail(TransferFailure.BadName, item.Name, $"The item name \"{item.Name}\" is not allowed.");
            }

            if (item.IsDirectory && !TryStorage(() => _storage.CreateDirectory(item.Name), item.Name))
            {
                return null;
            }
        }

        if (ExpectedFiles == 0)
        {
            Complete();
        }

        return null;
    }

    private FileAckMessage? OnFileBegin(FileBeginMessage file)
    {
        if (State == TransferState.Receiving && _file is { } open && file.FileIndex == open.FileIndex + 1)
        {
            // The next file begins while this one is open: its fileEnd was lost on the way.
            return Lost(MessageTypes.FileEnd, open.FileIndex, open.RelativePath, $"fileEnd for file {open.FileIndex} never arrived: fileBegin {file.FileIndex} came while file {open.FileIndex} is open.");
        }

        if (State != TransferState.Receiving || _file is not null || file.FileIndex != ReceivedFiles || ReceivedFiles >= ExpectedFiles)
        {
            return Fail(TransferFailure.OutOfOrder, file.RelativePath, $"fileBegin {file.FileIndex} came out of order ({ReceivedFiles} of {ExpectedFiles} files received, a file {(_file is null ? "is not" : "is")} open).");
        }

        if (!TransferPaths.IsAllowed(file.RelativePath, Items) || !_paths.Add(file.RelativePath))
        {
            return Fail(TransferFailure.BadName, file.RelativePath, $"The relative path \"{file.RelativePath}\" is not allowed or came twice.");
        }

        if (file.Size > ExpectedBytes - _announcedBytes)
        {
            return Fail(TransferFailure.SizeMismatch, file.RelativePath, $"The files announce more than the {ExpectedBytes} bytes of the transfer.");
        }

        Stream? stream = null;
        if (!TryStorage(() => stream = _storage.CreateFile(file.RelativePath), file.RelativePath))
        {
            return null;
        }

        _announcedBytes += file.Size;
        _file = file;
        _stream = stream;
        _fileReceived = 0;
        _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        return null;
    }

    private FileAckMessage? OnChunk(FileChunkMessage chunk)
    {
        var file = _file;
        if (chunk.FileIndex >= BegunFiles)
        {
            // Seen in production: the fileBegin line before the first chunk was lost on the way.
            return Lost(MessageTypes.FileBegin, chunk.FileIndex, file?.RelativePath, $"fileBegin for file {chunk.FileIndex} never arrived: a chunk of file {chunk.FileIndex} came while file {(file is null ? "(none)" : file.FileIndex)} is open.");
        }

        if (file is null || chunk.FileIndex != file.FileIndex)
        {
            return Fail(TransferFailure.OutOfOrder, file?.RelativePath, $"A chunk of file {chunk.FileIndex} came while file {(file is null ? "(none)" : file.FileIndex)} is open.");
        }

        if (chunk.Offset != _fileReceived)
        {
            return Fail(TransferFailure.DataLost, file.RelativePath, $"A chunk starts at byte {chunk.Offset} where byte {_fileReceived} was expected.");
        }

        var encoding = ChunkEncodings.OrDefault(chunk.Enc);
        if (!string.Equals(encoding, ChunkEncoding, StringComparison.Ordinal))
        {
            return Fail(TransferFailure.BadData, file.RelativePath, $"A chunk at byte {chunk.Offset} is written in \"{encoding}\" while transferBegin announced \"{ChunkEncoding}\".");
        }

        if (!TryDecode(chunk, out var count))
        {
            return Fail(TransferFailure.BadData, file.RelativePath, $"A chunk at byte {chunk.Offset} is not valid {encoding} of 1 to {TransferSender.MaxChunkSize} bytes.");
        }

        if (count > file.Size - _fileReceived)
        {
            return Fail(TransferFailure.SizeMismatch, file.RelativePath, $"The file goes past its announced size of {file.Size} bytes.");
        }

        var data = _buffer.AsSpan(0, count);
        var stream = _stream!;
        if (!TryStorage(() => stream.Write(_buffer, 0, count), file.RelativePath))
        {
            return null;
        }

        _hash!.AppendData(data);
        var first = _fileReceived == 0;
        _fileReceived += count;
        ReceivedBytes += count;

        // The first chunk of each file is acknowledged at once, so that the client learns
        // quickly that data gets through.
        if (first || ReceivedBytes - _bytesAtLastAck >= AckInterval)
        {
            return Ack(file);
        }

        return null;
    }

    /// <summary>Decodes the data of a chunk into the buffer; false when it is not 1 to <see cref="TransferSender.MaxChunkSize"/> valid bytes.</summary>
    private bool TryDecode(FileChunkMessage chunk, out int count)
    {
        if (ChunkEncoding == ChunkEncodings.U15)
        {
            count = chunk.Length ?? 0;
            return count is > 0 and <= TransferSender.MaxChunkSize && U15Codec.TryDecode(chunk.Data, count, _buffer);
        }

        count = 0;
        return chunk.Data.Length <= (TransferSender.MaxChunkSize + 2) / 3 * 4
            && Convert.TryFromBase64String(chunk.Data, _buffer, out count)
            && count > 0;
    }

    private FileAckMessage? OnFileEnd(FileEndMessage end)
    {
        var file = _file;
        if (end.FileIndex >= BegunFiles)
        {
            return Lost(MessageTypes.FileBegin, end.FileIndex, file?.RelativePath, $"fileBegin for file {end.FileIndex} never arrived: its fileEnd came while file {(file is null ? "(none)" : file.FileIndex)} is open.");
        }

        if (file is null || end.FileIndex != file.FileIndex)
        {
            return Fail(TransferFailure.OutOfOrder, file?.RelativePath, $"fileEnd {end.FileIndex} came while file {(file is null ? "(none)" : file.FileIndex)} is open.");
        }

        if (_fileReceived != file.Size)
        {
            return Fail(TransferFailure.SizeMismatch, file.RelativePath, $"The file announced {file.Size} bytes and {_fileReceived} arrived.");
        }

        var digest = TransferHash.ToHex(_hash!.GetHashAndReset());
        if (!string.Equals(digest, end.Sha256, StringComparison.Ordinal))
        {
            return Fail(TransferFailure.HashMismatch, file.RelativePath, $"The SHA-256 of the file is {digest}; the client sent {end.Sha256}.");
        }

        var stream = _stream!;
        if (!TryStorage(stream.Dispose, file.RelativePath))
        {
            return null;
        }

        _stream = null;
        if (!TryStorage(() => _storage.CompleteFile(file.RelativePath, file.Modified), file.RelativePath))
        {
            return null;
        }

        CloseFile();
        ReceivedFiles++;
        var ack = Ack(file);
        if (ReceivedFiles == ExpectedFiles)
        {
            Complete();
        }

        return ack;
    }

    private void Complete()
    {
        if (ReceivedBytes != ExpectedBytes)
        {
            Fail(TransferFailure.SizeMismatch, null, $"The transfer announced {ExpectedBytes} bytes and {ReceivedBytes} arrived.");
            return;
        }

        foreach (var item in Items)
        {
            if (!item.IsDirectory && !_paths.Contains(item.Name))
            {
                Fail(TransferFailure.Incomplete, item.Name, $"The dropped file \"{item.Name}\" did not arrive.");
                return;
            }
        }

        Finish(TransferState.Succeeded);
    }

    private FileAckMessage Ack(FileBeginMessage file)
    {
        _bytesAtLastAck = ReceivedBytes;
        return new FileAckMessage(TransferId, file.FileIndex, _fileReceived);
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
            Fail(TransferFailure.WriteFailed, relativePath, ex.Message);
            return false;
        }
    }

    /// <summary>Files whose fileBegin arrived: the files received, and the open one.</summary>
    private int BegunFiles => ReceivedFiles + (_file is null ? 0 : 1);

    private FileAckMessage? Lost(string messageType, int? fileIndex, string? relativePath, string detail)
    {
        LostMessage = messageType;
        LostFileIndex = fileIndex;
        return Fail(TransferFailure.MessageLost, relativePath, detail);
    }

    private FileAckMessage? Fail(TransferFailure failure, string? relativePath, string detail)
    {
        Failure = failure;
        FailurePath = relativePath;
        FailureDetail = detail;
        Finish(TransferState.Failed);
        return null;
    }

    private void Finish(TransferState state)
    {
        var started = State != TransferState.Waiting;
        State = state;
        _endTicks = _clock();
        if (!started)
        {
            _startTicks = _endTicks;
        }

        CloseFile();
        if (state != TransferState.Succeeded)
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
            _stream?.Dispose();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The transfer already failed or ended; the file is deleted with the rest.
        }

        _stream = null;
        _file = null;
        _hash?.Dispose();
        _hash = null;
    }
}
