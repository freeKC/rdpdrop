using System.Diagnostics;
using System.Globalization;
using UltimateRDP.Client.Localization;
using UltimateRDP.Client.Logging;
using UltimateRDP.Client.Resources;
using UltimateRDP.Core.Protocol;
using UltimateRDP.Core.Transfer;
using Message = UltimateRDP.Core.Protocol.Message;

namespace UltimateRDP.Client.Sessions;

/// <summary>
/// Sends the files of one drop to the agent through the channel. A background thread lists
/// the files, reads them with a large buffer, computes their checksums and builds the
/// lines with a <see cref="TransferSender"/>; every send is handed to the UI thread, where
/// the control lives, in order. At most <see cref="TransferSender.WindowBytes"/>, 4 MiB by
/// default, are in flight: the thread waits for the acknowledgements of the agent, which the UI thread
/// passes to <see cref="OnAck"/>.
/// </summary>
/// <remarks>
/// <para>
/// The events are raised on the UI thread. The upload ends once the agent acknowledged the
/// last bytes; the transferResult of the agent, which the owner receives, closes the transfer.
/// </para>
/// <para>
/// The agent acknowledges the first chunk of every file at once: without an
/// acknowledgement <see cref="TransferAckWatch.FirstAckTimeout"/> after the first chunk
/// went out, the upload fails, and the log says how the data was sent.
/// </para>
/// <para>
/// Since 0.5.1 the short lines, transferBegin, fileBegin and fileEnd, are held until the
/// next chunk and handed over with it, so that they leave in the same call of the control;
/// they go on their own before the thread waits for room in the window, and at the end.
/// With an agent that reads numbered lines (<see cref="Sequenced"/>), every line is kept in
/// a <see cref="ReliableOutbox"/> until an acknowledgement covers it: the agent asks for
/// missing lines (<see cref="OnResend"/>), and a thread that waits without any
/// acknowledgement for <see cref="TransferAckWatch.ProbeInterval"/> sends its newest line
/// again, which shows the agent a loss at the end of what was sent.
/// </para>
/// </remarks>
internal sealed class TransferUpload
{
    private const int ReadBufferSize = 1024 * 1024;
    private const int WaitSliceMilliseconds = 500;

    /// <summary>Most characters of short lines held for the next chunk, for a folder of many empty files.</summary>
    private const int MaxHeldCharacters = 100_000;

    /// <summary>Every file, hidden and system ones too, as a copy by Explorer would take them.</summary>
    private static readonly EnumerationOptions ListingOptions = new()
    {
        AttributesToSkip = 0,
        IgnoreInaccessible = false,
        RecurseSubdirectories = false,
        ReturnSpecialDirectories = false,
    };

    private readonly Control _owner;
    private readonly Func<IReadOnlyList<QueuedLine>, bool> _sendLines;
    private readonly IReadOnlyList<DropItem> _items;
    private readonly int _chunkSize;
    private readonly long _windowBytes;
    private readonly ChannelByteModel _model;
    private readonly string _chunkEncoding;
    private readonly object _gate = new();

    /// <summary>Short lines waiting for the next chunk. Transfer thread only.</summary>
    private readonly List<QueuedLine> _held = [];
    private int _heldCharacters;
    private TransferSender? _sender;
    private TransferAckWatch? _watch;
    private ReliableOutbox? _outbox;
    private int _linesSent;
    private int _chunksComing;
    private volatile bool _starting = true;
    private volatile bool _stopped;
    private volatile bool _ackMissing;

    /// <param name="owner">A control of the UI thread, to hand the sends to.</param>
    /// <param name="dragId">The drag whose drop was deferred.</param>
    /// <param name="transferId">The id the agent gave in its dragAck.</param>
    /// <param name="items">The dropped items, in the order of the dragEnter.</param>
    /// <param name="chunkSize">Raw bytes per chunk, within the bounds of <see cref="TransferSender"/>.</param>
    /// <param name="windowBytes">Most bytes in flight asked for, within the bounds of <see cref="TransferSender"/>.</param>
    /// <param name="model">Byte model detected on the data received from the agent, for the log.</param>
    /// <param name="chunkEncoding">How the chunks are written, one of <see cref="ChunkEncodings"/>.</param>
    /// <param name="sequenced">True when the agent reads numbered lines and asks for the missing ones.</param>
    /// <param name="sendLines">Queues ready lines on the UI thread, in their order; false when it failed.</param>
    public TransferUpload(Control owner, string dragId, string transferId, IReadOnlyList<DropItem> items, int chunkSize, long windowBytes, ChannelByteModel model, string chunkEncoding, bool sequenced, Func<IReadOnlyList<QueuedLine>, bool> sendLines)
    {
        _owner = owner;
        DragId = dragId;
        TransferId = transferId;
        _items = items;
        _chunkSize = TransferSender.ClampChunkSize(chunkSize);
        _windowBytes = TransferSender.ClampWindowBytes(windowBytes);
        _model = model;
        _chunkEncoding = chunkEncoding;
        Sequenced = sequenced;
        _sendLines = sendLines;
    }

    /// <summary>Raised once the files are listed and transferBegin is ready to go out.</summary>
    public event Action<TransferUpload>? Planned;

    /// <summary>Raised when the client cannot go on: a file cannot be read, a send failed or the agent went silent.</summary>
    public event Action<TransferUpload, LocalText>? Failed;

    public string DragId { get; }

    public string TransferId { get; }

    /// <summary>True when the lines are numbered and kept until the agent processed them.</summary>
    public bool Sequenced { get; }

    /// <summary>Number of files, folders being counted file by file. Known once <see cref="Planned"/> was raised.</summary>
    public int FileCount { get; private set; }

    /// <summary>Total bytes. Known once <see cref="Planned"/> was raised.</summary>
    public long TotalBytes { get; private set; }

    /// <summary>Progress shown in the status bar. UI thread only; null before <see cref="Planned"/>.</summary>
    public TransferProgress? Progress { get; private set; }

    /// <summary>The files in the order they are sent. Empty before <see cref="Planned"/>.</summary>
    public IReadOnlyList<TransferFile> Files { get; private set; } = [];

    /// <summary>The sizes of <see cref="Files"/>, in the same order.</summary>
    public IReadOnlyList<long> FileSizes { get; private set; } = [];

    /// <summary>True when the upload failed because the agent did not acknowledge the data in time.</summary>
    public bool AckMissing => _ackMissing;

    /// <summary>When the upload started, in <see cref="Environment.TickCount64"/> milliseconds.</summary>
    public long StartTicks { get; } = Environment.TickCount64;

    /// <summary>
    /// True while the transfer thread is about to hand over a chunk: from the start until
    /// its first lines are handed over, and from the moment the window has room for the next
    /// chunk until that chunk is queued. Short lines on their way may wait for it.
    /// </summary>
    public bool LargeLineComing => !_stopped && (_starting || Volatile.Read(ref _chunksComing) > 0);

    /// <summary>
    /// Size of a folder, counted for at most <paramref name="budget"/> so that the start of a
    /// drag never waits long; a folder too large to count in time gives what was counted.
    /// </summary>
    public static long MeasureFolder(string path, TimeSpan budget)
    {
        var watch = Stopwatch.StartNew();
        long total = 0;
        var pending = new Stack<DirectoryInfo>();
        pending.Push(new DirectoryInfo(path));
        while (pending.Count > 0 && watch.Elapsed < budget)
        {
            try
            {
                foreach (var entry in pending.Pop().EnumerateFileSystemInfos("*", ListingOptions))
                {
                    if (watch.Elapsed >= budget)
                    {
                        break;
                    }

                    if (entry is FileInfo file)
                    {
                        total += file.Length;
                    }
                    else if (entry is DirectoryInfo folder && !folder.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    {
                        pending.Push(folder);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The transfer reports an unreadable folder; the size is only a hint.
            }
        }

        return total;
    }

    /// <summary>Starts the background thread.</summary>
    public void Start()
    {
        var thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "UltimateRDP transfer " + TransferId,
        };
        thread.Start();
    }

    /// <summary>
    /// An acknowledgement of the agent. UI thread. Returns the bytes of the transfer
    /// acknowledged so far, or null when the acknowledgement was ignored. Its <c>seq</c>
    /// frees the lines kept for a resend.
    /// </summary>
    public long? OnAck(FileAckMessage ack)
    {
        long acknowledged;
        lock (_gate)
        {
            if (_sender is null || !_sender.OnAck(ack))
            {
                ClientLog.Warn(Invariant($"Transfer {TransferId}: unexpected acknowledgement of {ack.Received} bytes of file {ack.FileIndex}. Ignored."));
                return null;
            }

            if (ack.Seq is { } seq)
            {
                _outbox?.Acknowledge(seq);
            }

            acknowledged = _sender.AcknowledgedBytes;
            _watch?.OnAck(Environment.TickCount64);
            Monitor.PulseAll(_gate);
        }

        Progress?.Report(Environment.TickCount64, acknowledged);
        return acknowledged;
    }

    /// <summary>
    /// The agent asks for lines again. UI thread. Returns the lines still kept, in order,
    /// marked as sent again, for the caller to queue.
    /// </summary>
    public IReadOnlyList<QueuedLine> OnResend(TransferResendMessage request)
    {
        IReadOnlyList<QueuedLine> lines;
        lock (_gate)
        {
            if (_outbox is null || _stopped)
            {
                ClientLog.Info(Invariant($"Transfer {TransferId}: the agent asked for lines {request.From} to {request.To}, which this transfer does not keep. Ignored."));
                return [];
            }

            lines = _outbox.Take(request.From, request.To);
        }

        if (lines.Count == 0)
        {
            ClientLog.Info(Invariant($"Transfer {TransferId}: the agent asked for lines {request.From} to {request.To}, already acknowledged or never sent; nothing to send again."));
            return lines;
        }

        ClientLog.Info(Invariant($"Transfer {TransferId}: resending lines {lines[0].Seq} to {lines[^1].Seq} asked by the agent, {lines.Count} line(s) of {lines.Sum(line => (long)line.Text.Length)} characters, the first one {lines[0].Type}."));
        return lines;
    }

    /// <summary>What was sent again, for the summary of the transfer. UI thread.</summary>
    public string DescribeResends()
    {
        lock (_gate)
        {
            return _outbox is { } outbox
                ? Invariant($"{outbox.ResentLines} line(s) sent again for {outbox.Requests} request(s) of the agent, the newest line sent again {outbox.Probes} time(s) after a silence")
                : "lines not numbered, the agent cannot ask for lost ones";
        }
    }

    /// <summary>A call of the control that carried lines of this transfer failed. UI thread.</summary>
    public void OnSendFailed() => Fail(LocalText.Of(nameof(Strings.TransferNotSent)));

    /// <summary>Stops reading and sending. Sends nothing. Callable from any thread.</summary>
    public void Stop()
    {
        _stopped = true;
        lock (_gate)
        {
            Monitor.PulseAll(_gate);
        }
    }

    private void Run()
    {
        try
        {
            var files = ListFiles();
            if (files is null || _stopped)
            {
                return;
            }

            TransferFile[] planned = [.. files.Select(file => file.File)];
            TransferBeginMessage begin;
            lock (_gate)
            {
                _sender = new TransferSender(TransferId, planned, _chunkSize, _windowBytes, _chunkEncoding, Sequenced);
                _outbox = Sequenced ? new ReliableOutbox() : null;
                begin = _sender.Begin();
                _watch = new TransferAckWatch(Environment.TickCount64);
            }

            Post(() =>
            {
                FileCount = begin.Files;
                TotalBytes = begin.Bytes;
                Files = planned;
                FileSizes = [.. planned.Select(file => file.Size)];
                Progress = new TransferProgress(begin.Bytes, Environment.TickCount64);
                Planned?.Invoke(this);
            });

            // transferBegin leaves with the first chunk, in the same call.
            Hold(begin, $"{MessageTypes.TransferBegin} {TransferId}");
            var buffer = new byte[_chunkSize];
            foreach (var (path, file) in files)
            {
                if (!SendFile(path, file, buffer))
                {
                    return;
                }
            }

            PostHeld();
            WaitForLastAcknowledgement();
        }
        catch (Exception ex)
        {
            // An error here must not take the client down: the transfer fails instead.
            ClientLog.Error($"Transfer {TransferId}: unexpected error while sending.", ex);
            Fail(LocalText.Of(nameof(Strings.TransferFailedReason), ex.Message));
        }
    }

    /// <summary>The files to send, each with its path on this computer. Null when a file or folder cannot be read.</summary>
    private List<(string Path, TransferFile File)>? ListFiles()
    {
        var files = new List<(string Path, TransferFile File)>();
        foreach (var item in _items)
        {
            var name = TransferPaths.ItemName(item.ClientPath);
            try
            {
                if (!item.IsDirectory)
                {
                    var info = new FileInfo(item.ClientPath);
                    files.Add((info.FullName, new TransferFile(name, info.Length, info.LastWriteTimeUtc)));
                    continue;
                }

                var root = new DirectoryInfo(item.ClientPath);
                var pending = new Stack<DirectoryInfo>();
                pending.Push(root);
                while (pending.Count > 0)
                {
                    var folder = pending.Pop();
                    foreach (var entry in folder.EnumerateFileSystemInfos("*", ListingOptions))
                    {
                        if (entry is FileInfo info)
                        {
                            var relative = TransferPaths.Combine(name, Path.GetRelativePath(root.FullName, info.FullName));
                            files.Add((info.FullName, new TransferFile(relative, info.Length, info.LastWriteTimeUtc)));
                        }
                        else if (entry is DirectoryInfo child && !child.Attributes.HasFlag(FileAttributes.ReparsePoint))
                        {
                            // A junction or a link to a folder could loop, or leave the dropped folder.
                            pending.Push(child);
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                ClientLog.Warn($"Transfer {TransferId}: cannot list {item.ClientPath}. {ex.Message}");
                Fail(LocalText.Of(nameof(Strings.TransferReadFailed), item.ClientPath, ex.Message));
                return null;
            }
        }

        return files;
    }

    /// <summary>Sends one file. Returns false when the upload stopped or failed.</summary>
    private bool SendFile(string path, TransferFile file, byte[] buffer)
    {
        FileBeginMessage begin;
        lock (_gate)
        {
            begin = _sender!.BeginFile();
        }

        Hold(begin, Invariant($"{MessageTypes.FileBegin} {TransferId} file {begin.FileIndex}, {begin.Size} bytes"));
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, ReadBufferSize, FileOptions.SequentialScan);
            var remaining = file.Size;
            while (remaining > 0)
            {
                var wanted = (int)Math.Min(_chunkSize, remaining);
                var count = stream.ReadAtLeast(buffer.AsSpan(0, wanted), wanted, throwOnEndOfStream: false);
                if (count < wanted)
                {
                    ClientLog.Warn(Invariant($"Transfer {TransferId}: {path} ended {remaining - count} bytes before its listed size of {file.Size}."));
                    Fail(LocalText.Of(nameof(Strings.TransferFileChanged), path));
                    return false;
                }

                if (!WaitForWindow(count))
                {
                    return false;
                }

                // From here the chunk is on its way: short lines queued meanwhile may wait for it.
                Interlocked.Increment(ref _chunksComing);
                QueuedLine chunk;
                lock (_gate)
                {
                    var line = _sender!.EncodeChunk(buffer.AsSpan(0, count));
                    chunk = new QueuedLine(line, MessageTypes.FileChunk, TransferId, _sender.SentBytes, Sequenced ? _sender.LastSeq : -1, Label: Invariant($"{MessageTypes.FileChunk} {TransferId} file {begin.FileIndex}"));
                    _outbox?.Add(chunk);
                }

                _held.Add(chunk);
                QueuedLine[] lines = [.. _held];
                _held.Clear();
                _heldCharacters = 0;
                Post(() => SendLines(lines, countsChunk: true));
                remaining -= count;
            }

            // A file that grew since it was listed would arrive cut at its listed size.
            if (stream.Length != file.Size)
            {
                ClientLog.Warn(Invariant($"Transfer {TransferId}: {path} is now {stream.Length} bytes long, {file.Size} when it was listed."));
                Fail(LocalText.Of(nameof(Strings.TransferFileChanged), path));
                return false;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            ClientLog.Warn($"Transfer {TransferId}: cannot read {path}. {ex.Message}");
            Fail(LocalText.Of(nameof(Strings.TransferReadFailed), path, ex.Message));
            return false;
        }

        FileEndMessage end;
        lock (_gate)
        {
            end = _sender!.EndFile();
        }

        Hold(end, Invariant($"{MessageTypes.FileEnd} {TransferId} file {end.FileIndex}"));
        return !_stopped;
    }

    /// <summary>
    /// Builds the line of a short message and holds it for the next chunk; it is kept for a
    /// resend when the lines are numbered. Transfer thread.
    /// </summary>
    private void Hold(Message message, string label)
    {
        var line = new QueuedLine(LineFraming.EncodeLine(message), message.Type, TransferId, Seq: ReliableInbox.SeqOf(message) ?? -1, Label: label);
        if (line.Seq >= 0)
        {
            lock (_gate)
            {
                _outbox!.Add(line);
            }
        }

        _held.Add(line);
        _heldCharacters += line.Text.Length;
        if (_heldCharacters >= MaxHeldCharacters)
        {
            PostHeld();
        }
    }

    /// <summary>Hands the short lines held so far to the UI thread, on their own. Transfer thread.</summary>
    private void PostHeld()
    {
        if (_held.Count == 0)
        {
            return;
        }

        QueuedLine[] lines = [.. _held];
        _held.Clear();
        _heldCharacters = 0;
        Post(() => SendLines(lines, countsChunk: false));
    }

    /// <summary>Waits until a chunk of <paramref name="count"/> bytes fits in the window. False when stopped or failed.</summary>
    private bool WaitForWindow(int count)
    {
        lock (_gate)
        {
            if (TimedOut(waitingForAck: false, "while sending"))
            {
                return false;
            }

            if (!_stopped && !_sender!.CanSend(count))
            {
                // The agent may need these lines, a fileEnd, to send the acknowledgement that makes room.
                PostHeld();
            }

            while (!_stopped && !_sender!.CanSend(count))
            {
                if (TimedOut(waitingForAck: true, "with the window full"))
                {
                    return false;
                }

                Probe("with the window full");
                Monitor.Wait(_gate, WaitSliceMilliseconds);
            }

            return !_stopped;
        }
    }

    /// <summary>
    /// Once every file went out, waits for the agent to acknowledge the last bytes, so that
    /// an agent that stopped answering ends the transfer instead of leaving it open.
    /// </summary>
    private void WaitForLastAcknowledgement()
    {
        lock (_gate)
        {
            while (!_stopped && !_sender!.IsAcknowledged)
            {
                if (TimedOut(waitingForAck: true, "after the last file"))
                {
                    return;
                }

                Probe("after the last file");
                Monitor.Wait(_gate, WaitSliceMilliseconds);
            }
        }
    }

    /// <summary>
    /// Sends the newest line again when the client waits and nothing was acknowledged for
    /// <see cref="TransferAckWatch.ProbeInterval"/>: when the last call was lost, nothing
    /// after it shows the agent the gap. Under the lock.
    /// </summary>
    private void Probe(string when)
    {
        if (_outbox is null || !_watch!.ProbeDue(Environment.TickCount64, waitingForAck: true) || _outbox.TakeNewest() is not { } newest)
        {
            return;
        }

        ClientLog.Info(Invariant($"Transfer {TransferId}: no acknowledgement for {TransferAckWatch.ProbeInterval.TotalSeconds:0} s {when}; sending line {newest.Seq} ({newest.Type}) again, so that the agent sees what it misses."));
        Post(() => SendLines([newest], countsChunk: false));
    }

    /// <summary>
    /// Checks the acknowledgement rules; when one is broken, logs how the data was sent and
    /// fails the upload. Under the lock.
    /// </summary>
    private bool TimedOut(bool waitingForAck, string when)
    {
        var timeout = _watch!.Check(Environment.TickCount64, waitingForAck);
        if (timeout == TransferAckTimeout.None)
        {
            return false;
        }

        var sender = _sender!;
        var rule = timeout == TransferAckTimeout.FirstData
            ? Invariant($"no acknowledgement {TransferAckWatch.FirstAckTimeout.TotalSeconds:0} s after the first chunk was sent")
            : Invariant($"no acknowledgement for {TransferAckWatch.AckTimeout.TotalSeconds:0} s");
        ClientLog.Warn(Invariant(
            $"Transfer {TransferId}: {rule}, {when}. Byte model of the received data {_model}, chunk size {sender.ChunkSize} bytes in {sender.ChunkEncoding}, chunk lines of up to {sender.LongestChunkLine} characters, {Volatile.Read(ref _linesSent)} chunk line(s) sent, {sender.InFlightBytes} bytes in flight, {sender.AcknowledgedBytes} acknowledged{(_outbox is { } outbox ? Invariant($", {outbox.Count} line(s) kept from line {outbox.FirstSeq}") : string.Empty)}."));
        _ackMissing = true;
        Fail(LocalText.Of(timeout == TransferAckTimeout.FirstData ? nameof(Strings.TransferFirstAckMissing) : nameof(Strings.TransferAgentSilent)));
        return true;
    }

    /// <summary>
    /// Queues lines on the UI thread; a refusal fails the transfer. <paramref name="countsChunk"/>
    /// ends the wait announced by <see cref="LargeLineComing"/> for the chunk among them.
    /// </summary>
    private void SendLines(IReadOnlyList<QueuedLine> lines, bool countsChunk)
    {
        try
        {
            _starting = false;
            if (_stopped)
            {
                return;
            }

            if (!_sendLines(lines))
            {
                Fail(LocalText.Of(nameof(Strings.TransferNotSent)));
                return;
            }

            var chunks = lines.Count(line => line.IsChunk && !line.IsResend);
            if (chunks == 0)
            {
                return;
            }

            if (Interlocked.Add(ref _linesSent, chunks) == chunks)
            {
                var first = lines.First(line => line.IsChunk);
                ClientLog.Info(Invariant($"Transfer {TransferId}: first chunk line sent, {first.Text.Length} characters for chunks of {_chunkSize} bytes in {_chunkEncoding}, with {lines.Count - chunks} short line(s) in front of it."));
            }

            lock (_gate)
            {
                _watch?.OnChunkSent(Environment.TickCount64);
            }
        }
        finally
        {
            if (countsChunk)
            {
                Interlocked.Decrement(ref _chunksComing);
            }
        }
    }

    /// <summary>Stops the upload and raises <see cref="Failed"/> on the UI thread, once.</summary>
    private void Fail(LocalText reason)
    {
        if (_stopped)
        {
            return;
        }

        Stop();
        Post(() => Failed?.Invoke(this, reason), evenWhenStopped: true);
    }

    /// <summary>Runs <paramref name="action"/> on the UI thread, after the actions posted before.</summary>
    private void Post(Action action, bool evenWhenStopped = false)
    {
        if (_stopped && !evenWhenStopped)
        {
            return;
        }

        try
        {
            _owner.BeginInvoke(action);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            // The session view is gone: nothing is left to send to.
            _stopped = true;
        }
    }

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
