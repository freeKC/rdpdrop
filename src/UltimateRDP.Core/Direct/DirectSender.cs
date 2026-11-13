using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using UltimateRDP.Core.Agent;
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

    /// <summary>The agent closed connection 0 without accepting its token.</summary>
    Refused,

    /// <summary>A connection ended or failed, or the agent broke the rules of the frames.</summary>
    Broken,

    /// <summary>A file could not be read, or changed while it was sent; the agent was told with a cancel frame.</summary>
    LocalFailure,

    /// <summary>The client cancelled the transfer; the agent was told with a cancel frame when it could be sent.</summary>
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

/// <summary>Where the time of a direct transfer went on the client, for its summary line.</summary>
/// <param name="Connections">Connections that carried the transfer, connection 0 included.</param>
/// <param name="BytesPerConnection">Bytes of blocks written to each connection, by index; -1 for an index not used.</param>
/// <param name="Seconds">From the go frame to the end.</param>
/// <param name="ReadSeconds">Time the reader spent opening and reading the files.</param>
/// <param name="WriteSeconds">Time the writers spent in socket writes, summed over the connections.</param>
/// <param name="QueueEmptySeconds">Time the writers waited for a block from the reader, summed over the connections.</param>
/// <param name="QueueFullSeconds">Time the reader waited for room in the queue, all connections being busy.</param>
/// <param name="Rates">Bytes per second written to the connections: average, first and last seconds.</param>
public sealed record DirectSendStats(
    int Connections,
    IReadOnlyList<long> BytesPerConnection,
    double Seconds,
    double ReadSeconds,
    double WriteSeconds,
    double QueueEmptySeconds,
    double QueueFullSeconds,
    DirectRates Rates);

/// <summary>
/// One connection of a direct transfer, its TLS handshake done, as the caller of
/// <see cref="DirectSender"/> opens it.
/// </summary>
/// <param name="stream">The TLS stream.</param>
/// <param name="ready">Called once the agent accepted the token of the connection, to set the timeouts of the transfer.</param>
/// <param name="abort">Closes the socket at once, so that a read or a write in progress fails at once.</param>
public sealed class DirectConnection(Stream stream, Action? ready = null, Action? abort = null) : IDisposable
{
    private int _closed;

    /// <summary>The TLS stream.</summary>
    public Stream Stream { get; } = stream ?? throw new ArgumentNullException(nameof(stream));

    /// <summary>Closes the socket and the stream; callable from any thread, more than once.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0)
        {
            return;
        }

        try
        {
            abort?.Invoke();
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // Closing is all that is asked.
        }

        try
        {
            Stream.Dispose();
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException or NotSupportedException)
        {
            // The socket is closed already.
        }
    }

    internal void OnReady() => ready?.Invoke();
}

/// <summary>
/// The client side of a direct transfer, version 2 of the protocol. On connection 0 it
/// sends the token and waits for the ready frame, lists the files, opens the other
/// connections in parallel, sends the whole list and waits for the go frame of the agent.
/// Then one reader thread reads the files in order by blocks of 1 MiB into a bounded queue
/// and hashes each file as it reads it, and one writer thread per connection takes the next
/// block from the queue, so that a slow connection does not hold the others. Connection 0
/// also carries the trailers, and a thread of its own reads the acknowledgements and the
/// final frame there.
/// </summary>
/// <remarks>
/// The flow control is the one of TCP. <see cref="Cancel"/> sends the cancel frame on
/// connection 0, waits at most <see cref="CancelWait"/> for the final frame, then closes
/// every connection and the file being read, so that no thread stays blocked in a write.
/// </remarks>
public sealed class DirectSender
{
    /// <summary>How long a cancel waits for the final frame of the agent, the cancel frame included.</summary>
    public static readonly TimeSpan CancelWait = TimeSpan.FromSeconds(1);

    /// <summary>After a connection failed, the final frame of the agent may still say why within this time.</summary>
    private static readonly TimeSpan BrokenWait = TimeSpan.FromSeconds(2);

    /// <summary>When the thread of <see cref="Run"/> has not ended a cancel within this time, the connections close from a timer.</summary>
    private static readonly TimeSpan CancelWatchdog = TimeSpan.FromSeconds(1.5);

    /// <summary>An open or a first read of a file slower than this is logged: it holds the whole transfer.</summary>
    private static readonly TimeSpan SlowFile = TimeSpan.FromMilliseconds(500);

    /// <summary>A file that takes this long from its open to its first block is shown to the user, through <see cref="FileWaiting"/>.</summary>
    public static readonly TimeSpan FileWaitNotice = TimeSpan.FromSeconds(2);

    /// <summary>The delay of <see cref="FileWaiting"/>, <see cref="FileWaitNotice"/> unless a test asks for less.</summary>
    internal TimeSpan FileWaitDelay { get; init; } = FileWaitNotice;

    /// <summary>Blocks in the queue per connection.</summary>
    private const int BlocksPerConnection = 2;

    private readonly Func<IReadOnlyList<TransferFile>?> _list;
    private readonly Func<int, Stream> _open;
    private readonly int _maxConnections;
    private readonly Lock _gate = new();
    private readonly Lock _controlWrite = new();
    private readonly Slot?[] _slots = new Slot?[DirectFrames.MaxConnections];
    private readonly ConcurrentBag<byte[]> _pool = [];
    private readonly ManualResetEventSlim _go = new(false);
    private readonly ManualResetEventSlim _wake = new(false);
    private readonly ManualResetEventSlim _answersDone = new(false);
    private readonly CancellationTokenSource _stop = new();
    private BlockingCollection<Block> _queue = new(1);
    private byte[][] _tokens = [];
    private Timer? _watchdog;
    private volatile DirectResult? _final;
    private volatile string? _brokenDetail;
    private volatile bool _protocolBroken;
    private volatile string? _cancelReason;
    private volatile LocalProblem? _local;
    private volatile bool _acknowledged;
    private volatile bool _stopping;
    private volatile Stream? _source;
    private volatile string? _waitingOn;
    private readonly Lock _noticeGate = new();
    private string? _noticePath;
    private long _noticeSince;
    private bool _noticeShown;
    private long _closedAt;
    private long _acknowledgedBytes;
    private long _readTicks;
    private long _writeTicks;
    private long _emptyTicks;
    private long _fullTicks;

    /// <param name="listFiles">
    /// Lists the files, in the order they are sent, once the agent accepted connection 0, so
    /// that a long listing does not keep the agent waiting for the connection. Returns null
    /// when they cannot be listed: the transfer is then cancelled.
    /// </param>
    /// <param name="openFile">Opens file number <c>i</c> for reading; may throw an <see cref="IOException"/> or <see cref="UnauthorizedAccessException"/>.</param>
    /// <param name="maxConnections">The most connections to use, from the offer and the setting of the client, 1 to 16.</param>
    /// <param name="clockMilliseconds">A clock in milliseconds, for the rates.</param>
    public DirectSender(Func<IReadOnlyList<TransferFile>?> listFiles, Func<int, Stream> openFile, int maxConnections, Func<long> clockMilliseconds)
    {
        ArgumentNullException.ThrowIfNull(listFiles);
        ArgumentNullException.ThrowIfNull(openFile);
        ArgumentNullException.ThrowIfNull(clockMilliseconds);
        _list = listFiles;
        _open = openFile;
        _maxConnections = Math.Clamp(maxConnections, 1, DirectFrames.MaxConnections);
        Meter = new DirectRateMeter(clockMilliseconds);
    }

    /// <summary>Raised on the thread that reads connection 0 for every acknowledgement. The handler must return quickly.</summary>
    public event Action<DirectAck>? Acknowledged;

    /// <summary>Raised on the thread of <see cref="Run"/> once the agent accepted connection 0.</summary>
    public event Action? Ready;

    /// <summary>Raised on the thread of <see cref="Run"/> once the go frame came: the data flows.</summary>
    public event Action? Started;

    /// <summary>A line for the log at the info level, in English, raised on any thread of the sender.</summary>
    public event Action<string>? Info;

    /// <summary>
    /// Raised with the relative path of a file when its open and its first read have taken
    /// <see cref="FileWaitNotice"/> without a block, as when Windows checks a large file, and
    /// again with null once its first block is read or the transfer stops. Raised on a timer
    /// or on the reader thread; the handler must return quickly.
    /// </summary>
    public event Action<string?>? FileWaiting;

    /// <summary>The files, in the order they are sent, once listed.</summary>
    public IReadOnlyList<TransferFile> Files { get; private set; } = [];

    /// <summary>Total bytes of the files, once listed.</summary>
    public long TotalBytes { get; private set; }

    /// <summary>The connections planned for the transfer once the files are listed, connection 0 included.</summary>
    public int PlannedConnections { get; private set; }

    /// <summary>One connection per started share of this many bytes; the tests use less than the 32 MiB of the policy.</summary>
    internal long BytesPerConnection { get; init; } = DirectTransferPolicy.BytesPerConnection;

    /// <summary>Bytes the agent acknowledged so far.</summary>
    public long AcknowledgedBytes => Interlocked.Read(ref _acknowledgedBytes);

    /// <summary>The bytes written to the connections, from the go frame to the end.</summary>
    public DirectRateMeter Meter { get; }

    /// <summary>
    /// Stops the transfer: <see cref="Run"/> sends a cancel frame with <paramref name="reason"/>,
    /// in English, waits at most <see cref="CancelWait"/> for the final frame, closes everything
    /// and returns. Callable from any thread.
    /// </summary>
    public void Cancel(string reason)
    {
        lock (_gate)
        {
            if (_cancelReason is not null)
            {
                return;
            }

            _cancelReason = reason ?? "cancelled";
            _watchdog ??= new Timer(_ => CloseAll(), null, CancelWatchdog, Timeout.InfiniteTimeSpan);
        }

        _wake.Set();
    }

    /// <summary>Where the time went, for the summary line.</summary>
    public DirectSendStats Stats()
    {
        long[] bytes;
        int used;
        lock (_gate)
        {
            bytes = [.. _slots.Take(Math.Max(1, PlannedConnections)).Select(slot => slot is null ? -1 : Interlocked.Read(ref slot.Bytes))];
            used = _slots.Count(slot => slot is not null);
        }

        return new DirectSendStats(
            used,
            bytes,
            Meter.ElapsedMilliseconds / 1000.0,
            Seconds(Interlocked.Read(ref _readTicks)),
            Seconds(Interlocked.Read(ref _writeTicks)),
            Seconds(Interlocked.Read(ref _emptyTicks)),
            Seconds(Interlocked.Read(ref _fullTicks)),
            Meter.Rates());
    }

    /// <summary>
    /// Runs the transfer and returns how it ended. <paramref name="control"/> is connection 0;
    /// <paramref name="openConnection"/> opens connection <c>i</c> above 0, its TLS handshake
    /// done, or throws or returns null when it cannot; null uses connection 0 only. Never throws
    /// for the connections, the agent or the files, and closes every connection before it
    /// returns.
    /// </summary>
    public DirectSendOutcome Run(DirectConnection control, ReadOnlySpan<byte> token, Func<int, CancellationToken, DirectConnection?>? openConnection)
    {
        ArgumentNullException.ThrowIfNull(control);
        _tokens = new byte[_maxConnections][];
        for (var index = 0; index < _maxConnections; index++)
        {
            _tokens[index] = DirectFrames.TokenPayload(token, (byte)index);
        }

        try
        {
            return Transfer(control, openConnection);
        }
        finally
        {
            CloseAll();
            lock (_gate)
            {
                _watchdog?.Dispose();
            }

            Meter.Stop();
        }
    }

    private static double Seconds(long ticks) => ticks / (double)Stopwatch.Frequency;

    private static double Milliseconds(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

    private DirectSendOutcome Transfer(DirectConnection control, Func<int, CancellationToken, DirectConnection?>? openConnection)
    {
        var reader = new DirectFrameReader(control.Stream);
        try
        {
            lock (_controlWrite)
            {
                DirectFrames.Write(control.Stream, DirectFrameType.Token, _tokens[0]);
                control.Stream.Flush();
            }

            if (!reader.TryRead(out var type, out _))
            {
                return Outcome(DirectSendStatus.Refused, "The agent closed connection 0 without accepting its token.");
            }

            if (type != DirectFrameType.Ready)
            {
                return Outcome(DirectSendStatus.Broken, $"The agent answered the token of connection 0 with a {type} frame instead of ready.");
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException or NotSupportedException)
        {
            if (_cancelReason is { } early)
            {
                return Outcome(DirectSendStatus.Cancelled, "Cancelled: " + early);
            }

            return Outcome(ex is EndOfStreamException ? DirectSendStatus.Refused : DirectSendStatus.Broken, "Before the ready frame of connection 0: " + ex.Message);
        }

        control.OnReady();
        lock (_gate)
        {
            _slots[0] = new Slot(control);
        }

        Ready?.Invoke();
        var answers = new Thread(() => ReadAnswers(reader))
        {
            IsBackground = true,
            Name = "UltimateRDP direct answers",
        };
        answers.Start();
        if (_cancelReason is { } reason)
        {
            return CancelOn(reason, local: null);
        }

        if (_list() is not { } files)
        {
            return CancelOn("The dropped items cannot be listed.", new LocalProblem(null, "The dropped items cannot be listed.", Changed: false));
        }

        Files = files;
        TotalBytes = files.Sum(file => file.Size);
        PlannedConnections = DirectTransferPolicy.ConnectionsFor(_maxConnections, _maxConnections, TotalBytes, BytesPerConnection);
        _queue = new BlockingCollection<Block>(BlocksPerConnection * PlannedConnections);
        if (openConnection is not null)
        {
            for (var index = 1; index < PlannedConnections; index++)
            {
                var connectionIndex = index;
                var opener = new Thread(() => OpenExtra(connectionIndex, openConnection))
                {
                    IsBackground = true,
                    Name = "UltimateRDP direct open " + index.ToString(System.Globalization.CultureInfo.InvariantCulture),
                };
                opener.Start();
            }
        }

        try
        {
            lock (_controlWrite)
            {
                DirectFrames.Write(control.Stream, DirectFrameType.Begin, DirectFrames.BeginPayload(files.Count, TotalBytes));
                for (var index = 0; index < files.Count; index++)
                {
                    DirectFrames.Write(control.Stream, DirectFrameType.FileHeader, DirectFrames.FileHeaderPayload(index, files[index]));
                }

                DirectFrames.Write(control.Stream, DirectFrameType.ListEnd, []);
                control.Stream.Flush();
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException or NotSupportedException)
        {
            Broken("connection 0, while sending the list: " + ex.Message);
        }

        StartWriter(0);
        return Wait();
    }

    /// <summary>Waits for the end: the final frame, a broken connection, a cancel or a file that cannot be sent.</summary>
    private DirectSendOutcome Wait()
    {
        var reading = false;
        while (true)
        {
            _wake.Reset();
            if (_final is not null)
            {
                return FromAnswers();
            }

            if (_cancelReason is { } reason)
            {
                return CancelOn(reason, local: null);
            }

            if (_local is { } local)
            {
                return CancelOn(local.Detail, local);
            }

            if (_brokenDetail is not null)
            {
                // The agent may have ended the transfer and closed: its final frame says why.
                _answersDone.Wait(BrokenWait);
                return FromAnswers();
            }

            if (_go.IsSet && !reading)
            {
                reading = true;
                Meter.Start();
                var reader = new Thread(ReadFiles)
                {
                    IsBackground = true,
                    Name = "UltimateRDP direct reader",
                };
                reader.Start();
                Started?.Invoke();
                continue;
            }

            _wake.Wait();
        }
    }

    /// <summary>Opens connection <paramref name="index"/>, sends its token and starts its writer once the agent accepted it.</summary>
    private void OpenExtra(int index, Func<int, CancellationToken, DirectConnection?> openConnection)
    {
        DirectConnection? connection = null;
        try
        {
            connection = openConnection(index, _stop.Token);
            if (connection is null)
            {
                return;
            }

            DirectFrames.Write(connection.Stream, DirectFrameType.Token, _tokens[index]);
            connection.Stream.Flush();
            if (!new DirectFrameReader(connection.Stream).TryRead(out var type, out _) || type != DirectFrameType.Ready)
            {
                Info?.Invoke($"Connection {index} is not used: the agent did not accept its token.");
                connection.Dispose();
                return;
            }

            connection.OnReady();
            lock (_gate)
            {
                if (_stopping || _final is not null)
                {
                    connection.Dispose();
                    return;
                }

                _slots[index] = new Slot(connection);
            }

            StartWriter(index);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException or NotSupportedException or OperationCanceledException or System.Net.Sockets.SocketException or System.Security.Authentication.AuthenticationException)
        {
            connection?.Dispose();
            if (!_stopping && _final is null)
            {
                Info?.Invoke($"Connection {index} is not used: {ex.Message}");
            }
        }
    }

    private void StartWriter(int index)
    {
        var writer = new Thread(() => Write(index))
        {
            IsBackground = true,
            Name = "UltimateRDP direct writer " + index.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
        writer.Start();
    }

    /// <summary>A writer: once the go frame came, takes the next block of the queue and writes it to its connection, then the end frame.</summary>
    private void Write(int index)
    {
        Slot slot;
        lock (_gate)
        {
            slot = _slots[index]!;
        }

        var stream = slot.Connection.Stream;
        byte[]? held = null;
        try
        {
            WaitHandle.WaitAny([_go.WaitHandle, _stop.Token.WaitHandle]);
            if (_stopping || !_go.IsSet)
            {
                return;
            }

            while (true)
            {
                if (!_queue.TryTake(out var block))
                {
                    var waitStart = Stopwatch.GetTimestamp();
                    var taken = _queue.TryTake(out block, Timeout.Infinite, _stop.Token);
                    Interlocked.Add(ref _emptyTicks, Stopwatch.GetTimestamp() - waitStart);
                    if (!taken)
                    {
                        break;
                    }
                }

                held = block.Buffer;
                DirectFrames.WriteDataHeader(held, block.FileIndex, block.Offset, block.Length);
                var start = Stopwatch.GetTimestamp();
                if (index == 0)
                {
                    lock (_controlWrite)
                    {
                        stream.Write(held, 0, DirectFrames.DataFrameOverhead + block.Length);
                    }
                }
                else
                {
                    stream.Write(held, 0, DirectFrames.DataFrameOverhead + block.Length);
                }

                Interlocked.Add(ref _writeTicks, Stopwatch.GetTimestamp() - start);
                Interlocked.Add(ref slot.Bytes, block.Length);
                slot.Frames++;
                Meter.Add(block.Length);
                _pool.Add(held);
                held = null;
            }

            var end = DirectFrames.EndPayload(slot.Frames);
            if (index == 0)
            {
                lock (_controlWrite)
                {
                    DirectFrames.Write(stream, DirectFrameType.End, end);
                    stream.Flush();
                }
            }
            else
            {
                DirectFrames.Write(stream, DirectFrameType.End, end);
                stream.Flush();
            }
        }
        catch (OperationCanceledException)
        {
            // Stopped while waiting for a block.
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException or NotSupportedException)
        {
            if (_stopping)
            {
                LateAfterStop($"The write on connection {index} failed", ex);
            }
            else
            {
                Broken($"connection {index}: {ex.Message}");
            }
        }

        if (held is not null)
        {
            _pool.Add(held);
        }
    }

    /// <summary>The reader: reads the files in order by blocks into the queue, hashes each file and sends its trailer on connection 0.</summary>
    private void ReadFiles()
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var period = TimeSpan.FromMilliseconds(Math.Clamp(FileWaitDelay.TotalMilliseconds / 4, 10, 250));
        using var notice = new Timer(_ => CheckFileWait(), null, period, period);
        var digest = new byte[DirectFrames.HashLength];
        Slot control;
        lock (_gate)
        {
            control = _slots[0]!;
        }

        try
        {
            for (var index = 0; index < Files.Count; index++)
            {
                var file = Files[index];
                if (!SendFile(index, file, hash))
                {
                    return;
                }

                hash.GetHashAndReset(digest);
                lock (_controlWrite)
                {
                    DirectFrames.Write(control.Connection.Stream, DirectFrameType.FileTrailer, DirectFrames.FileTrailerPayload(index, digest));
                }
            }

            _queue.CompleteAdding();
        }
        catch (OperationCanceledException)
        {
            // Stopped while the queue was full.
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException or NotSupportedException)
        {
            if (_stopping)
            {
                LateAfterStop("The trailer on connection 0 failed", ex);
            }
            else
            {
                Broken("connection 0, while sending a trailer: " + ex.Message);
            }
        }
    }

    /// <summary>Reads one file into the queue. False when the transfer stops.</summary>
    private bool SendFile(int index, TransferFile file, IncrementalHash hash)
    {
        try
        {
            BeginFileWait(file.RelativePath);
            return SendFileBlocks(index, file, hash);
        }
        finally
        {
            EndFileWait();
        }
    }

    /// <summary>From the open of a file to its first block, a wait that the user may see.</summary>
    private void BeginFileWait(string relativePath)
    {
        lock (_noticeGate)
        {
            _noticePath = relativePath;
            _noticeSince = Stopwatch.GetTimestamp();
            _noticeShown = false;
        }
    }

    /// <summary>The first block of the file was read, or the file is over: the notice ends.</summary>
    private void EndFileWait()
    {
        lock (_noticeGate)
        {
            _noticePath = null;
            if (_noticeShown)
            {
                _noticeShown = false;
                FileWaiting?.Invoke(null);
            }
        }
    }

    /// <summary>On a timer while the reader runs: a wait for a file past <see cref="FileWaitDelay"/> is told once.</summary>
    private void CheckFileWait()
    {
        lock (_noticeGate)
        {
            if (_noticePath is { } path && !_noticeShown && !_stopping && Seconds(Stopwatch.GetTimestamp() - _noticeSince) >= FileWaitDelay.TotalSeconds)
            {
                _noticeShown = true;
                FileWaiting?.Invoke(path);
            }
        }
    }

    private bool SendFileBlocks(int index, TransferFile file, IncrementalHash hash)
    {
        if (_stopping)
        {
            return false;
        }

        Stream source;
        var start = Stopwatch.GetTimestamp();
        _waitingOn = "opening " + file.RelativePath;
        try
        {
            source = _open(index);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            LocalFailure(file.RelativePath, $"Cannot read {file.RelativePath}. {ex.Message}", changed: false);
            return false;
        }
        finally
        {
            _waitingOn = null;
        }

        var opened = Stopwatch.GetTimestamp() - start;
        Interlocked.Add(ref _readTicks, opened);
        if (Milliseconds(opened) > SlowFile.TotalMilliseconds)
        {
            Info?.Invoke(FormattableString.Invariant($"Opening {file.RelativePath} took {Milliseconds(opened):0} ms; nothing was sent meanwhile."));
        }

        using (source)
        {
            _source = source;
            if (_stopping)
            {
                LateAfterStop($"Opening {file.RelativePath} returned", null);
                return false;
            }

            long offset = 0;
            while (offset < file.Size)
            {
                if (_stopping)
                {
                    return false;
                }

                var length = DirectFrames.BlockLength(file.Size, offset);
                if (!_pool.TryTake(out var buffer))
                {
                    buffer = new byte[DirectFrames.DataFrameOverhead + DirectFrames.BlockSize];
                }

                int count;
                start = Stopwatch.GetTimestamp();
                _waitingOn = "reading " + file.RelativePath;
                try
                {
                    count = source.ReadAtLeast(buffer.AsSpan(DirectFrames.DataFrameOverhead, length), length, throwOnEndOfStream: false);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException or NotSupportedException)
                {
                    if (_stopping)
                    {
                        LateAfterStop($"The read of {file.RelativePath} failed", ex);
                    }
                    else
                    {
                        LocalFailure(file.RelativePath, $"Cannot read {file.RelativePath}. {ex.Message}", changed: false);
                    }

                    return false;
                }
                finally
                {
                    _waitingOn = null;
                }

                if (offset == 0)
                {
                    EndFileWait();
                }

                var read = Stopwatch.GetTimestamp() - start;
                Interlocked.Add(ref _readTicks, read);
                if (offset == 0 && Milliseconds(read) > SlowFile.TotalMilliseconds)
                {
                    Info?.Invoke(FormattableString.Invariant($"The first read of {file.RelativePath} took {Milliseconds(read):0} ms; nothing was sent meanwhile."));
                }

                if (_stopping)
                {
                    LateAfterStop($"The read of {file.RelativePath} returned", null);
                    return false;
                }

                if (count < length)
                {
                    LocalFailure(file.RelativePath, $"{file.RelativePath} ended {file.Size - offset - count} bytes before its listed size of {file.Size}.", changed: true);
                    return false;
                }

                hash.AppendData(buffer, DirectFrames.DataFrameOverhead, count);
                var block = new Block(index, offset, buffer, length);
                if (!_queue.TryAdd(block))
                {
                    start = Stopwatch.GetTimestamp();
                    _queue.Add(block, _stop.Token);
                    Interlocked.Add(ref _fullTicks, Stopwatch.GetTimestamp() - start);
                }

                offset += length;
            }

            // A file that grew since it was listed would arrive cut at its listed size.
            if (source.CanSeek && source.Length != file.Size)
            {
                LocalFailure(file.RelativePath, $"{file.RelativePath} is now {source.Length} bytes long, {file.Size} when it was listed.", changed: true);
                return false;
            }

            _source = null;
        }

        return true;
    }

    /// <summary>Reads the acknowledgements, the go frame and the final frame on connection 0, on a thread of its own.</summary>
    private void ReadAnswers(DirectFrameReader reader)
    {
        try
        {
            while (reader.TryRead(out var type, out var payload))
            {
                switch (type)
                {
                    case DirectFrameType.Go when !_go.IsSet:
                        _go.Set();
                        _wake.Set();
                        break;

                    case DirectFrameType.Ack when _go.IsSet && DirectFrames.TryParseAck(payload.Span, out var ack):
                        _acknowledged = true;
                        Interlocked.Exchange(ref _acknowledgedBytes, ack.TotalBytes);
                        Acknowledged?.Invoke(ack);
                        break;

                    case DirectFrameType.Final when DirectFrames.TryParseFinal(payload.Span, out var result):
                        _final = result;
                        return;

                    default:
                        _protocolBroken = true;
                        Broken($"The agent sent a {type} frame of {payload.Length} bytes that this client does not expect.");
                        return;
                }
            }

            if (!_stopping)
            {
                Broken("The agent closed connection 0 before its final frame.");
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException or NotSupportedException)
        {
            if (!_stopping)
            {
                _protocolBroken = ex is DirectProtocolException;
                Broken("connection 0: " + ex.Message);
            }
        }
        finally
        {
            _answersDone.Set();
            _wake.Set();
        }
    }

    /// <summary>
    /// Sends the cancel frame on connection 0 when it can within <see cref="CancelWait"/>, waits
    /// for the final frame in the rest of that time, then closes every connection and the file
    /// being read.
    /// </summary>
    private DirectSendOutcome CancelOn(string reason, LocalProblem? local)
    {
        var deadline = Stopwatch.GetTimestamp() + (long)(CancelWait.TotalSeconds * Stopwatch.Frequency);
        StopWork();
        Slot? control;
        lock (_gate)
        {
            control = _slots[0];
        }

        if (control is not null && _final is null && !_answersDone.IsSet)
        {
            if (_controlWrite.TryEnter(Remaining(deadline)))
            {
                try
                {
                    DirectFrames.Write(control.Connection.Stream, DirectFrameType.Cancel, DirectFrames.CancelPayload(reason));
                    control.Connection.Stream.Flush();
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException or NotSupportedException)
                {
                    // The agent is gone; it deletes what it received on its own.
                }
                finally
                {
                    _controlWrite.Exit();
                }

                _answersDone.Wait(Remaining(deadline));
            }
            else
            {
                Info?.Invoke(FormattableString.Invariant($"The cancel frame could not be sent within {CancelWait.TotalSeconds:0} s: a write on connection 0 is blocked. The connections close; the agent sees them close."));
            }
        }

        CloseAll();
        if (_waitingOn is { } waiting)
        {
            Info?.Invoke($"The reader is still {waiting}; it ends when that call returns, and sends nothing more.");
        }

        return local is null
            ? Outcome(DirectSendStatus.Cancelled, "Cancelled: " + reason)
            : new DirectSendOutcome(DirectSendStatus.LocalFailure, _acknowledged, _final, local.Detail, local.Path, local.Changed);
    }

    private static TimeSpan Remaining(long deadline) =>
        TimeSpan.FromSeconds(Math.Max(0, (deadline - Stopwatch.GetTimestamp()) / (double)Stopwatch.Frequency));

    /// <summary>The threads stop taking and adding blocks.</summary>
    private void StopWork()
    {
        lock (_gate)
        {
            if (_stopping)
            {
                return;
            }

            _stopping = true;
        }

        _stop.Cancel();
    }

    /// <summary>Closes every connection and the file being read, from whatever thread; the threads blocked on them fail at once.</summary>
    private void CloseAll()
    {
        StopWork();
        Interlocked.CompareExchange(ref _closedAt, Stopwatch.GetTimestamp(), 0);
        Slot?[] slots;
        lock (_gate)
        {
            slots = [.. _slots];
        }

        foreach (var slot in slots)
        {
            slot?.Connection.Dispose();
        }

        try
        {
            _source?.Dispose();
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // Closing is all that is asked.
        }
    }

    /// <summary>
    /// A thread of the sender saw the end of a read or a write after the transfer stopped. The
    /// calls that the close of the connections ends fail at once; one that returns well after
    /// it, such as a read held by an antivirus, gets a line.
    /// </summary>
    private void LateAfterStop(string what, Exception? ex)
    {
        var closed = Interlocked.Read(ref _closedAt);
        var late = closed == 0 ? 0 : Seconds(Stopwatch.GetTimestamp() - closed);
        if (late > 0.5)
        {
            Info?.Invoke(FormattableString.Invariant($"{what} {late:0.0} s after the connections closed{(ex is null ? string.Empty : ": " + ex.Message)}; nothing more is sent."));
        }
    }

    private void Broken(string detail)
    {
        _brokenDetail ??= detail;
        _wake.Set();
    }

    private void LocalFailure(string path, string detail, bool changed)
    {
        _local ??= new LocalProblem(path, detail, changed);
        _wake.Set();
    }

    /// <summary>The outcome from what the reader of connection 0 found.</summary>
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

    private sealed record LocalProblem(string? Path, string Detail, bool Changed);

    private readonly record struct Block(int FileIndex, long Offset, byte[] Buffer, int Length);

    /// <summary>One connection in use and what it carried.</summary>
    private sealed class Slot(DirectConnection connection)
    {
        public long Bytes;

        public DirectConnection Connection { get; } = connection;

        public long Frames { get; set; }
    }
}
