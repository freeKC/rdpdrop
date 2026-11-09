using System.Security.Cryptography;
using UltimateRDP.Core.Protocol;

namespace UltimateRDP.Core.Transfer;

/// <summary>One file of a transfer, as the client sends it.</summary>
/// <param name="RelativePath">Path inside the transfer, see <see cref="FileBeginMessage.RelativePath"/>.</param>
/// <param name="Size">Size in bytes, announced in <see cref="FileBeginMessage"/>.</param>
/// <param name="Modified">Last write time in UTC, or null.</param>
public sealed record TransferFile(string RelativePath, long Size, DateTime? Modified = null);

/// <summary>
/// Sending side of a transfer: builds the messages in order, counts the bytes in flight
/// and computes the checksum of each file as its chunks go out. It does no input or
/// output: the caller reads the files and hands the bytes over.
/// </summary>
/// <remarks>
/// <para>
/// Order: <see cref="Begin"/>, then for each file <see cref="BeginFile"/>, as many
/// <see cref="EncodeChunk"/> calls as needed with at most <see cref="ChunkSize"/> bytes
/// each, and <see cref="EndFile"/>. Before each chunk, <see cref="CanSend"/> says whether
/// the window has room; <see cref="OnAck"/> makes room as the agent acknowledges.
/// </para>
/// <para>This class is not thread safe.</para>
/// </remarks>
public sealed class TransferSender : IDisposable
{
    /// <summary>Raw bytes per <c>fileChunk</c>: about 350 000 characters of base64, within the line limit.</summary>
    public const int ChunkSize = 256 * 1024;

    /// <summary>Most bytes sent and not yet acknowledged.</summary>
    public const long WindowBytes = 4L * 1024 * 1024;

    private readonly long[] _fileStarts;
    private IncrementalHash? _hash;
    private int _current = -1;
    private long _offset;
    private bool _fileOpen;
    private bool _begun;

    /// <param name="transferId">Identifier given by the agent.</param>
    /// <param name="files">The files, in the order they are sent.</param>
    /// <exception cref="ArgumentException">A size is negative, or the total does not fit.</exception>
    public TransferSender(string transferId, IReadOnlyList<TransferFile> files)
    {
        ArgumentException.ThrowIfNullOrEmpty(transferId);
        ArgumentNullException.ThrowIfNull(files);
        TransferId = transferId;
        Files = files;
        _fileStarts = new long[files.Count + 1];
        for (var i = 0; i < files.Count; i++)
        {
            if (files[i].Size < 0)
            {
                throw new ArgumentException($"File {i} has a negative size.", nameof(files));
            }

            _fileStarts[i + 1] = checked(_fileStarts[i] + files[i].Size);
        }
    }

    /// <summary>Identifier given by the agent.</summary>
    public string TransferId { get; }

    /// <summary>The files, in the order they are sent.</summary>
    public IReadOnlyList<TransferFile> Files { get; }

    /// <summary>Total size of the files.</summary>
    public long TotalBytes => _fileStarts[^1];

    /// <summary>Bytes handed to <see cref="EncodeChunk"/> so far.</summary>
    public long SentBytes { get; private set; }

    /// <summary>Bytes the agent acknowledged so far.</summary>
    public long AcknowledgedBytes { get; private set; }

    /// <summary>Bytes sent and not yet acknowledged.</summary>
    public long InFlightBytes => SentBytes - AcknowledgedBytes;

    /// <summary>Number of files whose <see cref="EndFile"/> was built.</summary>
    public int EndedFiles { get; private set; }

    /// <summary>True once every file was sent.</summary>
    public bool IsSent => _begun && EndedFiles == Files.Count;

    /// <summary>True once every file was sent and every byte acknowledged.</summary>
    public bool IsAcknowledged => IsSent && AcknowledgedBytes == TotalBytes;

    /// <summary>
    /// True when a chunk of <paramref name="byteCount"/> bytes fits in the window. When
    /// nothing is in flight, any chunk fits.
    /// </summary>
    public bool CanSend(int byteCount) => InFlightBytes == 0 || InFlightBytes + byteCount <= WindowBytes;

    /// <summary>The first message of the transfer.</summary>
    /// <exception cref="InvalidOperationException">Called twice.</exception>
    public TransferBeginMessage Begin()
    {
        if (_begun)
        {
            throw new InvalidOperationException("The transfer has already begun.");
        }

        _begun = true;
        return new TransferBeginMessage(TransferId, Files.Count, TotalBytes);
    }

    /// <summary>Starts the next file.</summary>
    /// <exception cref="InvalidOperationException">The transfer has not begun, a file is open, or every file was sent.</exception>
    public FileBeginMessage BeginFile()
    {
        if (!_begun || _fileOpen || _current + 1 >= Files.Count)
        {
            throw new InvalidOperationException("No file can begin now.");
        }

        _current++;
        _offset = 0;
        _fileOpen = true;
        _hash?.Dispose();
        _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var file = Files[_current];
        return new FileBeginMessage(TransferId, _current, file.RelativePath, file.Size, file.Modified);
    }

    /// <summary>
    /// The <c>fileChunk</c> line, ready for the channel, for the next bytes of the open
    /// file. Adds them to the checksum and to the bytes in flight.
    /// </summary>
    /// <exception cref="InvalidOperationException">No file is open.</exception>
    /// <exception cref="ArgumentException">The chunk is empty, longer than <see cref="ChunkSize"/>, or goes past the announced size.</exception>
    public string EncodeChunk(ReadOnlySpan<byte> data)
    {
        if (!_fileOpen)
        {
            throw new InvalidOperationException("No file is open.");
        }

        if (data.IsEmpty || data.Length > ChunkSize)
        {
            throw new ArgumentException($"A chunk holds 1 to {ChunkSize} bytes.", nameof(data));
        }

        if (_offset + data.Length > Files[_current].Size)
        {
            throw new ArgumentException("The chunk goes past the announced size of the file.", nameof(data));
        }

        var line = TransferLines.EncodeChunk(TransferId, _current, _offset, data);
        _hash!.AppendData(data);
        _offset += data.Length;
        SentBytes += data.Length;
        return line;
    }

    /// <summary>Ends the open file, with its checksum.</summary>
    /// <exception cref="InvalidOperationException">No file is open, or fewer bytes than announced were sent.</exception>
    public FileEndMessage EndFile()
    {
        if (!_fileOpen)
        {
            throw new InvalidOperationException("No file is open.");
        }

        if (_offset != Files[_current].Size)
        {
            throw new InvalidOperationException($"File {_current} announced {Files[_current].Size} bytes and {_offset} were sent.");
        }

        _fileOpen = false;
        EndedFiles++;
        var digest = _hash!.GetHashAndReset();
        return new FileEndMessage(TransferId, _current, TransferHash.ToHex(digest));
    }

    /// <summary>
    /// Takes an acknowledgement into account. Returns false, and changes nothing, when it
    /// belongs to another transfer, names a file that was not begun, or acknowledges bytes
    /// that were never sent. An older acknowledgement changes nothing and returns true.
    /// </summary>
    public bool OnAck(FileAckMessage ack)
    {
        ArgumentNullException.ThrowIfNull(ack);
        if (ack.TransferId != TransferId || ack.FileIndex < 0 || ack.FileIndex > _current || ack.Received > Files[ack.FileIndex].Size)
        {
            return false;
        }

        var position = _fileStarts[ack.FileIndex] + ack.Received;
        if (position > SentBytes)
        {
            return false;
        }

        AcknowledgedBytes = Math.Max(AcknowledgedBytes, position);
        return true;
    }

    /// <summary>The message that stops the transfer.</summary>
    public TransferCancelMessage Cancel(string? reason) => new(TransferId, reason);

    /// <summary>Releases the checksum of the open file.</summary>
    public void Dispose()
    {
        _hash?.Dispose();
        _hash = null;
    }
}
