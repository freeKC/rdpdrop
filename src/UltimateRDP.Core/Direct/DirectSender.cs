using System.Security.Cryptography;
using UltimateRDP.Core.Protocol;
using UltimateRDP.Core.Transfer;

namespace UltimateRDP.Core.Direct;

/// <summary>How a <see cref="DirectSender"/> ended.</summary>
public enum DirectSendStatus
{
    /// <summary>The agent has every file and said so in its final frame.</summary>
    Succeeded,

    /// <summary>The agent ended the transfer with an error in its final frame; its text says why.</summary>
    AgentFailed,

    /// <summary>The agent ended the transfer as cancelled in its final frame.</summary>
    AgentCancelled,

    /// <summary>The agent closed the connection without accepting the token.</summary>
    Refused,

    /// <summary>The stream ended or failed, or the agent broke the rules of the frames.</summary>
    Broken,

    /// <summary>A file could not be read, or changed while it was sent; the agent was told with a cancel frame.</summary>
    LocalFailure,

    /// <summary>The client cancelled the transfer; the agent was told with a cancel frame.</summary>
    Cancelled,
}

/// <summary>The outcome of a <see cref="DirectSender"/>.</summary>
/// <param name="Status">How it ended.</param>
/// <param name="Acknowledged">True when at least one acknowledgement of the agent arrived.</param>
/// <param name="Result">The final frame of the agent, when one arrived.</param>
/// <param name="Detail">What happened, in English, for the log.</param>
/// <param name="Path">The local path concerned, for <see cref="DirectSendStatus.LocalFailure"/>.</param>
/// <param name="FileChanged">For <see cref="DirectSendStatus.LocalFailure"/>: the file changed while it was sent, rather than failed to read.</param>
public sealed record DirectSendOutcome(DirectSendStatus Status, bool Acknowledged, DirectResult? Result, string Detail, string? Path = null, bool FileChanged = false);

/// <summary>
/// The client side of a direct transfer, over a stream that already did its TLS handshake:
/// sends the token and waits for the ready frame, then sends the files frame by frame
/// (<see cref="DirectFrames"/>): a header, the raw data by <see cref="DirectFrames.MaxDataBytes"/>
/// and a trailer with the SHA-256 of each file, while a thread of its own reads the
/// acknowledgements and the final frame of the agent.
/// </summary>
/// <remarks>
/// The flow control is the one of TCP: a write blocks while the agent does not read. The
/// caller bounds the time with the timeouts of the stream, and closes the stream to stop a
/// transfer that <see cref="Cancel"/> could not end in time.
/// </remarks>
public sealed class DirectSender
{
    /// <summary>How long a cancelled transfer waits for the final frame of the agent.</summary>
    public static readonly TimeSpan CancelWait = TimeSpan.FromSeconds(3);

    private readonly Func<IReadOnlyList<TransferFile>?> _list;
    private readonly Func<int, Stream> _open;
    private readonly ManualResetEventSlim _readerDone = new(false);
    private volatile DirectResult? _final;
    private volatile string? _brokenDetail;
    private volatile bool _protocolBroken;
    private volatile string? _cancelReason;
    private volatile bool _acknowledged;
    private long _acknowledgedBytes;

    /// <param name="files">The files, in the order they are sent, with their relative paths.</param>
    /// <param name="openFile">Opens file number <c>i</c> for reading; may throw an <see cref="IOException"/> or <see cref="UnauthorizedAccessException"/>.</param>
    public DirectSender(IReadOnlyList<TransferFile> files, Func<int, Stream> openFile)
        : this(() => files, openFile)
    {
        ArgumentNullException.ThrowIfNull(files);
    }

    /// <param name="listFiles">
    /// Lists the files, in the order they are sent, once the agent accepted the token, so
    /// that a long listing does not keep the agent waiting for the connection. Returns null
    /// when they cannot be listed: the transfer is then cancelled.
    /// </param>
    /// <param name="openFile">Opens file number <c>i</c> for reading; may throw an <see cref="IOException"/> or <see cref="UnauthorizedAccessException"/>.</param>
    public DirectSender(Func<IReadOnlyList<TransferFile>?> listFiles, Func<int, Stream> openFile)
    {
        ArgumentNullException.ThrowIfNull(listFiles);
        ArgumentNullException.ThrowIfNull(openFile);
        _list = listFiles;
        _open = openFile;
    }

    /// <summary>
    /// Raised on the thread that reads the stream for every acknowledgement of the agent. The
    /// handler must return quickly.
    /// </summary>
    public event Action<DirectAck>? Acknowledged;

    /// <summary>Raised on the thread of <see cref="Run"/> once the agent accepted the token.</summary>
    public event Action? Ready;

    /// <summary>The files, in the order they are sent, once listed.</summary>
    public IReadOnlyList<TransferFile> Files { get; private set; } = [];

    /// <summary>Total bytes of the files, once listed.</summary>
    public long TotalBytes { get; private set; }

    /// <summary>Bytes the agent acknowledged so far. Readable from any thread.</summary>
    public long AcknowledgedBytes => Interlocked.Read(ref _acknowledgedBytes);

    /// <summary>Bytes handed to the stream so far.</summary>
    public long SentBytes { get; private set; }

    /// <summary>
    /// Stops the transfer: <see cref="Run"/> sends a cancel frame with <paramref name="reason"/>,
    /// in English, before its next data frame, and returns. Callable from any thread.
    /// </summary>
    public void Cancel(string reason) => _cancelReason ??= reason ?? "cancelled";

    /// <summary>
    /// Runs the transfer over <paramref name="stream"/> and returns how it ended. Never
    /// throws for the stream, the agent or the files; the caller disposes the stream after.
    /// </summary>
    public DirectSendOutcome Run(Stream stream, ReadOnlySpan<byte> token)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var reader = new DirectFrameReader(stream);
        try
        {
            DirectFrames.Write(stream, DirectFrameType.Token, DirectFrames.TokenPayload(token));
            stream.Flush();
            if (!reader.TryRead(out var type, out _))
            {
                return Outcome(DirectSendStatus.Refused, "The agent closed the connection without accepting the token.");
            }

            if (type != DirectFrameType.Ready)
            {
                return Outcome(DirectSendStatus.Broken, $"The agent answered the token with a {type} frame instead of ready.");
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            return Outcome(ex is EndOfStreamException ? DirectSendStatus.Refused : DirectSendStatus.Broken, "Before the ready frame: " + ex.Message);
        }

        Ready?.Invoke();
        var readerThread = new Thread(() => ReadAnswers(reader))
        {
            IsBackground = true,
            Name = "UltimateRDP direct answers",
        };
        readerThread.Start();
        if (_list() is not { } files)
        {
            return LocalFailure(stream, null, "The dropped items cannot be listed.", changed: false);
        }

        Files = files;
        TotalBytes = files.Sum(file => file.Size);
        var outcome = Send(stream);
        if (outcome is not null)
        {
            return outcome;
        }

        // Every file went out: the final frame of the agent ends the transfer.
        _readerDone.Wait();
        return FromAnswers();
    }

    /// <summary>Sends the begin frame and the files. Returns an outcome when the transfer ended early, null when every file went out.</summary>
    private DirectSendOutcome? Send(Stream stream)
    {
        var frame = new byte[DirectFrames.HeaderLength + DirectFrames.MaxDataBytes];
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var digest = new byte[DirectFrames.HashLength];
        try
        {
            DirectFrames.Write(stream, DirectFrameType.Begin, DirectFrames.BeginPayload(Files.Count, TotalBytes));
            for (var index = 0; index < Files.Count; index++)
            {
                var file = Files[index];
                if (Stopped(stream) is { } early)
                {
                    return early;
                }

                Stream source;
                try
                {
                    source = _open(index);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
                {
                    return LocalFailure(stream, file.RelativePath, $"Cannot read {file.RelativePath}. {ex.Message}", changed: false);
                }

                using (source)
                {
                    DirectFrames.Write(stream, DirectFrameType.FileHeader, DirectFrames.FileHeaderPayload(index, file));
                    var remaining = file.Size;
                    while (remaining > 0)
                    {
                        if (Stopped(stream) is { } stopped)
                        {
                            return stopped;
                        }

                        var wanted = (int)Math.Min(DirectFrames.MaxDataBytes, remaining);
                        int count;
                        try
                        {
                            count = source.ReadAtLeast(frame.AsSpan(DirectFrames.HeaderLength, wanted), wanted, throwOnEndOfStream: false);
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                        {
                            return LocalFailure(stream, file.RelativePath, $"Cannot read {file.RelativePath}. {ex.Message}", changed: false);
                        }

                        if (count < wanted)
                        {
                            return LocalFailure(stream, file.RelativePath, $"{file.RelativePath} ended {remaining - count} bytes before its listed size of {file.Size}.", changed: true);
                        }

                        hash.AppendData(frame.AsSpan(DirectFrames.HeaderLength, count));
                        DirectFrames.WriteHeader(frame, DirectFrameType.Data, count);
                        stream.Write(frame, 0, DirectFrames.HeaderLength + count);
                        SentBytes += count;
                        remaining -= count;
                    }

                    // A file that grew since it was listed would arrive cut at its listed size.
                    if (source.CanSeek && source.Length != file.Size)
                    {
                        return LocalFailure(stream, file.RelativePath, $"{file.RelativePath} is now {source.Length} bytes long, {file.Size} when it was listed.", changed: true);
                    }
                }

                hash.GetHashAndReset(digest);
                DirectFrames.Write(stream, DirectFrameType.FileTrailer, DirectFrames.FileTrailerPayload(index, digest));
            }

            stream.Flush();
            return null;
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // The agent may have ended the transfer and closed: its final frame says why.
            _readerDone.Wait(CancelWait);
            return _final is not null ? FromAnswers() : Outcome(DirectSendStatus.Broken, "While sending: " + ex.Message);
        }
    }

    /// <summary>An outcome when the agent already ended the transfer, the stream broke or the client cancelled; null to go on.</summary>
    private DirectSendOutcome? Stopped(Stream stream)
    {
        if (_final is not null || _brokenDetail is not null)
        {
            return FromAnswers();
        }

        return _cancelReason is { } reason ? CancelOn(stream, reason) : null;
    }

    /// <summary>A file cannot be sent: the agent is told, and the outcome names the file.</summary>
    private DirectSendOutcome LocalFailure(Stream stream, string? path, string detail, bool changed)
    {
        CancelOn(stream, detail);
        return new DirectSendOutcome(DirectSendStatus.LocalFailure, _acknowledged, _final, detail, path, changed);
    }

    /// <summary>Sends the cancel frame, waits a little for the final frame, and returns the cancelled outcome.</summary>
    private DirectSendOutcome CancelOn(Stream stream, string reason)
    {
        try
        {
            DirectFrames.Write(stream, DirectFrameType.Cancel, DirectFrames.CancelPayload(reason));
            stream.Flush();
            _readerDone.Wait(CancelWait);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // The agent is gone; it deletes what it received on its own.
        }

        return Outcome(DirectSendStatus.Cancelled, "Cancelled: " + reason);
    }

    /// <summary>Reads the acknowledgements and the final frame, on a thread of its own.</summary>
    private void ReadAnswers(DirectFrameReader reader)
    {
        try
        {
            while (reader.TryRead(out var type, out var payload))
            {
                switch (type)
                {
                    case DirectFrameType.Ack when DirectFrames.TryParseAck(payload.Span, out var ack):
                        _acknowledged = true;
                        Interlocked.Exchange(ref _acknowledgedBytes, ack.TotalBytes);
                        Acknowledged?.Invoke(ack);
                        break;

                    case DirectFrameType.Final when DirectFrames.TryParseFinal(payload.Span, out var result):
                        _final = result;
                        return;

                    default:
                        _protocolBroken = true;
                        _brokenDetail = $"The agent sent a {type} frame of {payload.Length} bytes that this client does not expect.";
                        return;
                }
            }

            _brokenDetail = "The agent closed the connection before its final frame.";
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            _protocolBroken = ex is DirectProtocolException;
            _brokenDetail = ex.Message;
        }
        finally
        {
            _readerDone.Set();
        }
    }

    /// <summary>The outcome from what the reader found.</summary>
    private DirectSendOutcome FromAnswers()
    {
        if (_final is { } result)
        {
            var status = result.Status switch
            {
                DropStatus.Ok => DirectSendStatus.Succeeded,
                DropStatus.Cancelled => DirectSendStatus.AgentCancelled,
                _ => DirectSendStatus.AgentFailed,
            };
            return new DirectSendOutcome(status, _acknowledged, result, status == DirectSendStatus.Succeeded ? "The agent has every file." : $"The agent ended the transfer, {result.Failure}: {result.Message}");
        }

        return Outcome(DirectSendStatus.Broken, (_protocolBroken ? "Protocol error: " : string.Empty) + (_brokenDetail ?? "The connection ended."));
    }

    private DirectSendOutcome Outcome(DirectSendStatus status, string detail) => new(status, _acknowledged, _final, detail);
}
