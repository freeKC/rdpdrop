using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using UltimateRDP.Agent.Interop;
using UltimateRDP.Agent.Logging;
using UltimateRDP.Core.Agent;
using UltimateRDP.Core.Protocol;
using Message = UltimateRDP.Core.Protocol.Message;

namespace UltimateRDP.Agent.Channel;

/// <summary>
/// Keeps the ULTRDP channel open for as long as the agent runs. A dedicated background
/// thread opens the channel, reads lines (see <see cref="IncomingTextDecoder"/> and
/// <see cref="LineReader"/>) and hands each one, as ASCII bytes, to the handler. On any
/// failure it closes the channel, waits as <see cref="ChannelRetryPolicy"/> says and
/// tries again, forever. A connection or a logon of the session skips the wait.
/// </summary>
/// <remarks>
/// Most sessions of a server never see an UltimateRDP client, so the waiting state is
/// quiet: no log line per attempt, one line per <see cref="IdleReportInterval"/> at most,
/// and a warning only for a failure that a session without client does not explain.
/// The same thread watches the exit event of <c>--uninstall</c>, so that the agent needs
/// no other thread while it waits.
/// <para>
/// While a transfer runs (<see cref="StartReadMeasure"/> to <see cref="StopReadMeasure"/>),
/// every read is counted in a <see cref="ChannelReadStats"/>: its size, the time blocked
/// in the call and the time spent on its data, so that the transfer can log what the
/// channel did. Without a transfer nothing is counted or timed, and the waiting state of
/// a session without client is the same as in version 0.4.3.
/// </para>
/// <para>
/// Once a channel is open, the <c>channelRead</c> setting chooses how it is read: with
/// WTSVirtualChannelRead, the default, or through its file handle
/// (<see cref="ChannelFileReader"/>), whose blocks <see cref="ChannelPduFraming"/> frees
/// from any channel header. At the first inconsistency of the file handle, the connection
/// goes back to WTSVirtualChannelRead.
/// </para>
/// </remarks>
internal sealed class ChannelLoop : IDisposable
{
    /// <summary>Spacing of the "still waiting for a client channel" line.</summary>
    public static readonly TimeSpan IdleReportInterval = TimeSpan.FromHours(1);

    private const int ReadTimeoutMilliseconds = 500;
    private const int ReadBufferSize = 64 * 1024;

    // A read that returns no data at once, many times in a row, means the channel is
    // dead even though the call reports success. Without this check the loop would spin.
    private const int MaxInstantEmptyReads = 50;
    private const int InstantReadMilliseconds = 20;

    private readonly FileLogger _logger;
    private readonly WaitHandle? _exitSignal;
    private readonly ChannelReadMode _readMode;
    private readonly AutoResetEvent _wake = new(false);
    private readonly IncomingTextDecoder _decoder = new();
    private readonly LineReader _lineReader = new();
    private readonly RepeatedFailureFilter _openFailures = new();
    private readonly RepeatedFailureFilter _loopFailures = new();
    private readonly IdleReport _idleReport = new(IdleReportInterval);
    private Action<byte[]>? _payloadHandler;
    private Thread? _thread;
    private byte[]? _buffer;
    private byte[]? _payload;
    private ChannelReadStats? _readStats;
    private volatile string _readModeText = "wts";
    private VirtualChannel? _channel;
    private long _connectedAtTicks = Environment.TickCount64;
    private int _consecutiveFailures;

    // Channels in a row that opened and closed before a client spoke on them. They count
    // as failures for the wait, and their open and close lines go to the debug level once
    // they repeat, so that a channel that opens but never works cannot fill the log.
    private int _silentChannels;
    private bool _exitRaised;
    private bool _clientPresent;
    private volatile bool _stopping;
    private volatile bool _restartRequested;
    private volatile string? _restartReason;
    private volatile bool _sessionDisconnected;
    private volatile ChannelByteModel _model;

    /// <param name="logger">The log.</param>
    /// <param name="exitSignal">Event that asks the agent to exit, or null.</param>
    /// <param name="readMode">How the channel is read, from the <c>channelRead</c> setting.</param>
    public ChannelLoop(FileLogger logger, WaitHandle? exitSignal, ChannelReadMode readMode = ChannelReadMode.Wts)
    {
        _logger = logger;
        _exitSignal = exitSignal;
        _readMode = readMode;
        _lineReader.ProtocolError += (_, _) =>
            _logger.Warn($"Protocol error: a line is longer than {LineFraming.MaxLineLength} characters. Buffered data dropped; the channel stays open.");
    }

    /// <summary>
    /// Raised on the channel thread with true when an UltimateRDP client speaks on a newly
    /// opened channel, and with false when that channel closes.
    /// </summary>
    public event Action<bool>? ClientPresenceChanged;

    /// <summary>Raised once on the channel thread when the exit event is set.</summary>
    public event Action? ExitRequested;

    /// <summary>How the channel is read on the current connection, for the summaries of the transfers.</summary>
    public string ReadMode => _readModeText;

    /// <summary>
    /// Starts counting the reads for a transfer and returns the new counters. Callable from
    /// any thread; a transfer that starts while another is counted replaces it.
    /// </summary>
    public ChannelReadStats StartReadMeasure()
    {
        var stats = new ChannelReadStats();
        Volatile.Write(ref _readStats, stats);
        return stats;
    }

    /// <summary>Stops counting into <paramref name="stats"/>, unless another transfer replaced them. Callable from any thread.</summary>
    public void StopReadMeasure(ChannelReadStats stats) => Interlocked.CompareExchange(ref _readStats, null, stats);

    /// <summary>
    /// Starts the background thread. <paramref name="payloadHandler"/> runs on that thread
    /// for every complete line; it must return quickly.
    /// </summary>
    public void Start(Action<byte[]> payloadHandler)
    {
        if (_thread is not null)
        {
            throw new InvalidOperationException("The channel loop is already running.");
        }

        _payloadHandler = payloadHandler;
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "UltimateRDP channel",
        };
        _thread.Start();
    }

    /// <summary>
    /// A client connected to the session, or a user logged on: the channel may be there
    /// now, so the loop tries at once, then often for a short while.
    /// </summary>
    public void OnSessionConnected(string reason)
    {
        Interlocked.Exchange(ref _connectedAtTicks, Environment.TickCount64);
        _sessionDisconnected = false;
        RestartNow(reason);
    }

    /// <summary>
    /// The session lost its client: the channel is closed at once and the loop waits
    /// for the next connection.
    /// </summary>
    public void OnSessionDisconnected(string reason)
    {
        _sessionDisconnected = true;
        RestartNow(reason);
    }

    /// <summary>
    /// Sends a message on the current channel, in the byte model detected on it. Returns
    /// false when no channel is open, nothing was received on it yet, or the write
    /// failed. Callable from any thread.
    /// </summary>
    public bool TrySend(Message message)
    {
        var channel = Volatile.Read(ref _channel);
        if (channel is null)
        {
            _logger.Warn($"Cannot send the {message.Type} message: the channel is not open.");
            return false;
        }

        var model = _model;
        if (model == ChannelByteModel.Unknown)
        {
            _logger.Warn($"Cannot send the {message.Type} message: nothing was received on this connection yet, so its byte model is unknown.");
            return false;
        }

        try
        {
            channel.Write(LineFraming.EncodeBytes(message, model));
            _logger.Debug($"Sent {message.Type}.");
            return true;
        }
        catch (ChannelException ex)
        {
            _logger.Warn($"Cannot send the {message.Type} message. {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Stops the thread and waits for it at most <paramref name="wait"/>. The thread is a
    /// background thread: if it does not stop in time, it ends with the process.
    /// </summary>
    public void Stop(TimeSpan wait)
    {
        _stopping = true;
        _wake.Set();
        if (_thread is not null && _thread != Thread.CurrentThread && !_thread.Join(wait))
        {
            _logger.Warn("The channel thread did not stop in time.");
        }
    }

    public void Dispose() => Stop(TimeSpan.FromSeconds(2));

    private void RestartNow(string reason)
    {
        _restartReason = reason;
        _restartRequested = true;
        _wake.Set();
    }

    private void Run()
    {
        while (!_stopping)
        {
            TimeSpan delay;
            try
            {
                delay = RunOnce();
                _loopFailures.Reset();
            }
            catch (Exception ex)
            {
                // An unexpected error must not end the loop, or the agent would stop
                // listening until it is restarted. The same error is logged once.
                if (_loopFailures.IsNew(ex.GetType().FullName + ": " + ex.Message))
                {
                    _logger.Error("Unexpected error in the channel loop; trying again.", ex);
                }

                _consecutiveFailures++;
                delay = _consecutiveFailures >= ChannelRetryPolicy.FailuresBeforeLongDelay ? ChannelRetryPolicy.LongDelay : ChannelRetryPolicy.ShortDelay;
            }

            if (!_stopping)
            {
                Wait(delay);
            }
        }

        _logger.Debug("Channel loop stopped.");
    }

    /// <summary>One attempt: opens the channel and pumps it until it closes. Returns the wait before the next one.</summary>
    private TimeSpan RunOnce()
    {
        var channel = Open(out var attempt);
        if (channel is null)
        {
            return ChannelRetryPolicy.NextDelay(attempt);
        }

        try
        {
            Pump(channel, _buffer ??= new byte[ReadBufferSize]);
        }
        catch (ChannelException ex)
        {
            if (!_stopping)
            {
                _logger.Write(ChannelLineLevel(), $"The {ProtocolConstants.ChannelName} virtual channel closed. {ex.Message}.");
            }
        }
        catch (Exception ex)
        {
            _logger.Error("Unexpected error on the channel; reopening it.", ex);
        }
        finally
        {
            Volatile.Write(ref _channel, null);
            channel.Dispose();
            SetClientPresent(false);
        }

        if (_model != ChannelByteModel.Unknown)
        {
            _silentChannels = 0;
            return ChannelRetryPolicy.NextDelay(attempt with { OpenError = 0, ConsecutiveFailures = 0 });
        }

        _silentChannels++;
        return ChannelRetryPolicy.NextDelay(attempt with { OpenError = 0, ConsecutiveFailures = _silentChannels });
    }

    /// <summary>Level of the open and close lines: debug once silent channels repeat.</summary>
    private LogLevel ChannelLineLevel() =>
        _silentChannels >= ChannelRetryPolicy.FailuresBeforeLongDelay ? LogLevel.Debug : LogLevel.Info;

    /// <summary>Waits for the delay, a wake up, or the exit event.</summary>
    private void Wait(TimeSpan delay)
    {
        if (_exitSignal is null || _exitRaised)
        {
            _wake.WaitOne(delay);
            return;
        }

        if (WaitHandle.WaitAny([_wake, _exitSignal], delay) == 1)
        {
            RaiseExit();
        }
    }

    private VirtualChannel? Open(out ChannelAttempt attempt)
    {
        _restartRequested = false;
        var remote = User32.GetSystemMetrics(User32.SmRemoteSession) != 0;
        var error = 0;
        var channel = remote ? VirtualChannel.TryOpen(ProtocolConstants.ChannelName, out error) : null;
        var sinceConnect = TimeSpan.FromMilliseconds(Environment.TickCount64 - Interlocked.Read(ref _connectedAtTicks));
        if (channel is null)
        {
            _consecutiveFailures++;
            attempt = new ChannelAttempt(remote, _sessionDisconnected, error, _consecutiveFailures, sinceConnect);
            ReportFailure(attempt);
            return null;
        }

        attempt = new ChannelAttempt(remote, _sessionDisconnected, 0, 0, sinceConnect);
        _consecutiveFailures = 0;
        _openFailures.Reset();
        _idleReport.Reset();
        _decoder.Reset();
        _lineReader.Reset();
        _model = ChannelByteModel.Unknown;
        Volatile.Write(ref _channel, channel);
        _logger.Write(ChannelLineLevel(), $"The {ProtocolConstants.ChannelName} virtual channel is open.");
        return channel;
    }

    /// <summary>
    /// A session without an UltimateRDP client fails every attempt; that is its normal
    /// state, reported once per <see cref="IdleReportInterval"/>. Another failure, such as
    /// a policy that blocks the channel, is a warning, once until the channel opens.
    /// </summary>
    private void ReportFailure(ChannelAttempt attempt)
    {
        var expected = ChannelRetryPolicy.IsExpected(attempt);
        if (!expected && _openFailures.IsNew("open:" + attempt.OpenError))
        {
            _logger.Warn($"Cannot open the {ProtocolConstants.ChannelName} virtual channel: {Describe(attempt)}. "
                + "A policy may block the channel. The agent keeps trying.");
            return;
        }

        if (_idleReport.IsDue(Environment.TickCount64))
        {
            _logger.Info($"Still waiting for a client channel: {Describe(attempt)}.");
        }
        else if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.Debug($"No channel: {Describe(attempt)}; next attempt in {ChannelRetryPolicy.NextDelay(attempt).TotalSeconds:0} seconds.");
        }
    }

    private static string Describe(ChannelAttempt attempt)
    {
        if (!attempt.IsRemoteSession)
        {
            return "this is not a Remote Desktop session";
        }

        var error = $"error {attempt.OpenError}, {new Win32Exception(attempt.OpenError).Message}";
        if (attempt.IsSessionDisconnected)
        {
            return $"the session has no client ({error})";
        }

        return ChannelRetryPolicy.IsNotFound(attempt.OpenError)
            ? $"the client of the session did not open the channel ({error})"
            : error;
    }

    private void Pump(VirtualChannel channel, byte[] buffer)
    {
        var reader = OpenFileReader(channel);
        var framing = reader is null ? null : new ChannelPduFraming();
        try
        {
            Pump(channel, buffer, ref reader, framing);
        }
        finally
        {
            reader?.Dispose();
        }
    }

    /// <summary>The reader of the file handle when the setting asks for it and it can be opened, otherwise null.</summary>
    private ChannelFileReader? OpenFileReader(VirtualChannel channel)
    {
        _readModeText = "wts";
        if (_readMode != ChannelReadMode.File)
        {
            return null;
        }

        var reader = channel.OpenFileReader(out var error);
        if (reader is null)
        {
            _readModeText = "wts, the file handle could not be used";
            _logger.Warn($"channelRead is \"file\" but the file handle of the channel cannot be used: {error}. Reading with WTSVirtualChannelRead.");
            return null;
        }

        _readModeText = "file, layout not known yet";
        _logger.Info($"Reading the channel through its file handle (channelRead \"file\"), up to {ChannelFileReader.BufferSize} bytes per read.");
        return reader;
    }

    private void Pump(VirtualChannel channel, byte[] buffer, ref ChannelFileReader? reader, ChannelPduFraming? framing)
    {
        var payload = framing is null ? null : (_payload ??= new byte[ReadBufferSize]);
        var layoutLogged = false;
        var instantEmptyReads = 0;
        while (!_stopping)
        {
            if (_restartRequested)
            {
                _restartRequested = false;
                _logger.Info($"Closing the channel: {_restartReason}.");
                return;
            }

            if (_exitSignal is not null && !_exitRaised && _exitSignal.WaitOne(0))
            {
                RaiseExit();
            }

            // Only a transfer in progress is timed; otherwise the loop is the one of 0.4.3.
            var stats = Volatile.Read(ref _readStats);
            var started = Environment.TickCount64;
            var readStart = stats is null ? 0 : Stopwatch.GetTimestamp();
            int count;
            if (reader is not null)
            {
                try
                {
                    count = reader.Read(buffer, ReadTimeoutMilliseconds);
                }
                catch (ChannelException ex)
                {
                    FallBack(ref reader, ex.Message);
                    continue;
                }
            }
            else
            {
                count = channel.Read(buffer, ReadTimeoutMilliseconds);
            }

            var readEnd = stats is null ? 0 : Stopwatch.GetTimestamp();
            if (count == 0)
            {
                stats?.RecordRead(0, readEnd - readStart);
                instantEmptyReads = Environment.TickCount64 - started < InstantReadMilliseconds ? instantEmptyReads + 1 : 0;
                if (instantEmptyReads >= MaxInstantEmptyReads)
                {
                    throw new ChannelException("The channel returns no data without waiting", 0);
                }

                continue;
            }

            instantEmptyReads = 0;
            ReadOnlySpan<byte> data = buffer.AsSpan(0, count);
            var headers = 0;
            if (reader is not null && framing is not null && payload is not null)
            {
                if (!framing.TryUnwrap(data, payload, out var written, out headers))
                {
                    stats?.RecordRead(0, readEnd - readStart);
                    FallBack(ref reader, $"{framing.Error}; the {count} bytes of this read are dropped");
                    continue;
                }

                if (!layoutLogged && framing.Layout != ChannelPduLayout.Unknown)
                {
                    layoutLogged = true;
                    _readModeText = framing.Layout == ChannelPduLayout.Headers ? "file with channel headers" : "file without headers";
                    _logger.Info(framing.Layout == ChannelPduLayout.Headers
                        ? $"The file handle of the channel gives a CHANNEL_PDU_HEADER of {ChannelPduFraming.HeaderLength} bytes before each chunk; the headers are taken out."
                        : "The file handle of the channel gives the data without headers.");
                }

                data = payload.AsSpan(0, written);
            }

            stats?.RecordRead(data.Length, readEnd - readStart, headers);
            Process(data);
            stats?.RecordProcessing(Stopwatch.GetTimestamp() - readEnd);
        }
    }

    /// <summary>Stops reading the file handle for the rest of the connection.</summary>
    private void FallBack(ref ChannelFileReader? reader, string reason)
    {
        _logger.Warn($"Reading the channel through its file handle failed: {reason}. The channel is read with WTSVirtualChannelRead until it closes.");
        reader?.Dispose();
        reader = null;
        _readModeText = "wts after a failure of the file handle";
    }

    /// <summary>Decodes the data of one read and handles the lines it completes.</summary>
    private void Process(ReadOnlySpan<byte> data)
    {
        var text = _decoder.Decode(data, out var rejected);
        if (rejected)
        {
            _logger.Warn($"Protocol error: the data at the start of the connection does not start with \"{{\". Dropped.");
            return;
        }

        if (_model == ChannelByteModel.Unknown && _decoder.Model != ChannelByteModel.Unknown)
        {
            _model = _decoder.Model;
            _silentChannels = 0;
            _logger.Info($"Channel byte model: {_model}.");
            _logger.Info($"Session connection: {SessionTraffic.Describe()}");
            SetClientPresent(true);
        }

        _lineReader.Append(text);
        while (_lineReader.TryReadLine(out var line))
        {
            // A control that ends each string with its NUL terminator would put a NUL
            // in front of the next line. More than that means data was replaced or
            // lost on the way, and a whole message may be missing: it is logged, so
            // that no line disappears without a trace.
            var trimmed = line.Trim(['\0', ' ']);
            if (trimmed.Length == 0)
            {
                _logger.Warn($"Protocol error: a line of {line.Length} characters held only NUL characters and spaces. Ignored.");
                continue;
            }

            var padding = line.Length - line.AsSpan().TrimStart(['\0', ' ']).Length;
            if (padding > 1)
            {
                _logger.Warn($"Protocol error: {padding} NUL characters or spaces came before the line that starts with {Head(trimmed)}. They were dropped; a message may be missing before this line.");
            }

            Dispatch(Encoding.ASCII.GetBytes(trimmed));
        }
    }

    /// <summary>The first characters of a line, printable ASCII only, for the log.</summary>
    private static string Head(string line)
    {
        var head = line.Length > 40 ? line[..40] : line;
        return string.Concat(head.Select(c => c is >= ' ' and <= '~' ? c : '?'));
    }

    private void Dispatch(byte[] payload)
    {
        var stats = Volatile.Read(ref _readStats);
        var started = stats is null ? 0 : Stopwatch.GetTimestamp();
        try
        {
            _payloadHandler?.Invoke(payload);
        }
        catch (Exception ex)
        {
            // A bug in one message must not take the channel down.
            _logger.Error("Unexpected error while handling a message.", ex);
        }
        finally
        {
            stats?.RecordHandling(Stopwatch.GetTimestamp() - started);
        }
    }

    private void SetClientPresent(bool present)
    {
        if (_clientPresent == present)
        {
            return;
        }

        _clientPresent = present;
        try
        {
            ClientPresenceChanged?.Invoke(present);
        }
        catch (Exception ex)
        {
            _logger.Error("Unexpected error while following the client connection.", ex);
        }
    }

    private void RaiseExit()
    {
        _exitRaised = true;
        try
        {
            ExitRequested?.Invoke();
        }
        catch (Exception ex)
        {
            _logger.Error("Unexpected error while handling the exit request.", ex);
        }
    }
}
