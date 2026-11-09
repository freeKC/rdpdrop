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
/// the control lives, in order. At most <see cref="TransferSender.WindowBytes"/> are in
/// flight: the thread waits for the acknowledgements of the agent, which the UI thread
/// passes to <see cref="OnAck"/>.
/// </summary>
/// <remarks>
/// The events are raised on the UI thread. The upload ends when the last file was sent;
/// the transferResult of the agent, which the owner receives, closes the transfer.
/// </remarks>
internal sealed class TransferUpload
{
    /// <summary>Without any acknowledgement for this long while the window is full, the agent is taken as gone.</summary>
    public static readonly TimeSpan AckTimeout = TimeSpan.FromSeconds(60);

    private const int ReadBufferSize = 1024 * 1024;
    private const int WaitSliceMilliseconds = 500;

    /// <summary>Every file, hidden and system ones too, as a copy by Explorer would take them.</summary>
    private static readonly EnumerationOptions ListingOptions = new()
    {
        AttributesToSkip = 0,
        IgnoreInaccessible = false,
        RecurseSubdirectories = false,
        ReturnSpecialDirectories = false,
    };

    private readonly Control _owner;
    private readonly Func<Message, bool> _sendMessage;
    private readonly Func<string, bool> _sendLine;
    private readonly IReadOnlyList<DropItem> _items;
    private readonly object _gate = new();
    private TransferSender? _sender;
    private long _lastAckTicks;
    private volatile bool _stopped;

    /// <param name="owner">A control of the UI thread, to hand the sends to.</param>
    /// <param name="dragId">The drag whose drop was deferred.</param>
    /// <param name="transferId">The id the agent gave in its dragAck.</param>
    /// <param name="items">The dropped items, in the order of the dragEnter.</param>
    /// <param name="sendMessage">Sends a message on the UI thread; false when it failed.</param>
    /// <param name="sendLine">Sends a ready line on the UI thread; false when it failed.</param>
    public TransferUpload(Control owner, string dragId, string transferId, IReadOnlyList<DropItem> items, Func<Message, bool> sendMessage, Func<string, bool> sendLine)
    {
        _owner = owner;
        DragId = dragId;
        TransferId = transferId;
        _items = items;
        _sendMessage = sendMessage;
        _sendLine = sendLine;
    }

    /// <summary>Raised once the files are listed and transferBegin went out.</summary>
    public event Action<TransferUpload>? Planned;

    /// <summary>Raised when the client cannot go on: a file cannot be read, a send failed or the agent went silent.</summary>
    public event Action<TransferUpload, LocalText>? Failed;

    public string DragId { get; }

    public string TransferId { get; }

    /// <summary>Number of files, folders being counted file by file. Known once <see cref="Planned"/> was raised.</summary>
    public int FileCount { get; private set; }

    /// <summary>Total bytes. Known once <see cref="Planned"/> was raised.</summary>
    public long TotalBytes { get; private set; }

    /// <summary>Progress shown in the status bar. UI thread only; null before <see cref="Planned"/>.</summary>
    public TransferProgress? Progress { get; private set; }

    /// <summary>When the upload started, in <see cref="Environment.TickCount64"/> milliseconds.</summary>
    public long StartTicks { get; } = Environment.TickCount64;

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

    /// <summary>An acknowledgement of the agent. UI thread.</summary>
    public void OnAck(FileAckMessage ack)
    {
        long acknowledged;
        lock (_gate)
        {
            if (_sender is null || !_sender.OnAck(ack))
            {
                ClientLog.Warn(Invariant($"Transfer {TransferId}: unexpected acknowledgement of {ack.Received} bytes of file {ack.FileIndex}. Ignored."));
                return;
            }

            acknowledged = _sender.AcknowledgedBytes;
            Interlocked.Exchange(ref _lastAckTicks, Environment.TickCount64);
            Monitor.PulseAll(_gate);
        }

        Progress?.Report(Environment.TickCount64, acknowledged);
    }

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

            TransferBeginMessage begin;
            lock (_gate)
            {
                _sender = new TransferSender(TransferId, [.. files.Select(file => file.File)]);
                begin = _sender.Begin();
                Interlocked.Exchange(ref _lastAckTicks, Environment.TickCount64);
            }

            Post(() =>
            {
                FileCount = begin.Files;
                TotalBytes = begin.Bytes;
                Progress = new TransferProgress(begin.Bytes, Environment.TickCount64);
                if (SendMessage(begin))
                {
                    Planned?.Invoke(this);
                }
            });

            var buffer = new byte[TransferSender.ChunkSize];
            foreach (var (path, file) in files)
            {
                if (!SendFile(path, file, buffer))
                {
                    return;
                }
            }
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

        Post(() => SendMessage(begin));
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, ReadBufferSize, FileOptions.SequentialScan);
            var remaining = file.Size;
            while (remaining > 0)
            {
                var wanted = (int)Math.Min(TransferSender.ChunkSize, remaining);
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

                string line;
                lock (_gate)
                {
                    line = _sender!.EncodeChunk(buffer.AsSpan(0, count));
                }

                Post(() => SendLine(line));
                remaining -= count;
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

        Post(() => SendMessage(end));
        return !_stopped;
    }

    /// <summary>Waits until a chunk of <paramref name="count"/> bytes fits in the window. False when stopped or failed.</summary>
    private bool WaitForWindow(int count)
    {
        lock (_gate)
        {
            while (!_stopped && !_sender!.CanSend(count))
            {
                if (Environment.TickCount64 - Interlocked.Read(ref _lastAckTicks) > AckTimeout.TotalMilliseconds)
                {
                    ClientLog.Warn(Invariant($"Transfer {TransferId}: no acknowledgement for {AckTimeout.TotalSeconds:0} s with {_sender.InFlightBytes} bytes in flight."));
                    Fail(LocalText.Of(nameof(Strings.TransferAgentSilent)));
                    return false;
                }

                Monitor.Wait(_gate, WaitSliceMilliseconds);
            }

            return !_stopped;
        }
    }

    /// <summary>Sends on the UI thread; a refusal of the control fails the transfer.</summary>
    private bool SendMessage(Message message)
    {
        if (_stopped)
        {
            return false;
        }

        if (_sendMessage(message))
        {
            return true;
        }

        Fail(LocalText.Of(nameof(Strings.TransferNotSent)));
        return false;
    }

    private void SendLine(string line)
    {
        if (!_stopped && !_sendLine(line))
        {
            Fail(LocalText.Of(nameof(Strings.TransferNotSent)));
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
