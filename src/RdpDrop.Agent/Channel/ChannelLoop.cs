using System.ComponentModel;
using RdpDrop.Agent.Interop;
using RdpDrop.Agent.Logging;
using RdpDrop.Core.Protocol;
using Message = RdpDrop.Core.Protocol.Message;

namespace RdpDrop.Agent.Channel;

/// <summary>
/// Keeps the RDPDROP channel open for as long as the agent runs. A dedicated background
/// thread opens the channel, reads frames and hands each payload to the handler. On any
/// failure it closes the channel, waits <see cref="RetryDelay"/> and tries again,
/// forever. <see cref="RestartNow"/> skips the wait, for example after a reconnect.
/// </summary>
internal sealed class ChannelLoop : IDisposable
{
    public static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(2);

    private const int ReadTimeoutMilliseconds = 500;
    private const int ReadBufferSize = 64 * 1024;

    // A read that returns no data at once, many times in a row, means the channel is
    // dead even though the call reports success. Without this check the loop would spin.
    private const int MaxInstantEmptyReads = 50;
    private const int InstantReadMilliseconds = 20;

    private readonly FileLogger _logger;
    private readonly AutoResetEvent _wake = new(false);
    private readonly FrameReader _frameReader = new();
    private readonly RepeatedFailureFilter _openFailures = new();
    private Action<byte[]>? _payloadHandler;
    private Thread? _thread;
    private VirtualChannel? _channel;
    private volatile bool _stopping;
    private volatile bool _restartRequested;
    private volatile string? _restartReason;
    private volatile bool _sessionDisconnected;

    public ChannelLoop(FileLogger logger)
    {
        _logger = logger;
        _frameReader.ProtocolError += (_, e) =>
            _logger.Warn($"Protocol error: a frame announced {e.DeclaredLength} bytes, more than the {Framing.MaxFrameSize} byte limit. Buffered data dropped; the channel stays open.");
    }

    /// <summary>True while a channel is open.</summary>
    public bool IsConnected => Volatile.Read(ref _channel) is not null;

    /// <summary>
    /// Starts the background thread. <paramref name="payloadHandler"/> runs on that thread
    /// for every complete frame; it must return quickly.
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
            Name = "RdpDrop channel",
        };
        _thread.Start();
    }

    /// <summary>Closes the current channel, if any, and opens it again without waiting.</summary>
    public void RestartNow(string reason)
    {
        _restartReason = reason;
        _restartRequested = true;
        _wake.Set();
    }

    /// <summary>
    /// Records whether the session currently has a client. While it has none, open
    /// failures are expected and only logged at debug level.
    /// </summary>
    public void SetSessionDisconnected(bool disconnected) => _sessionDisconnected = disconnected;

    /// <summary>
    /// Frames and sends a message on the current channel. Returns false when no channel
    /// is open or the write failed. Callable from any thread.
    /// </summary>
    public bool TrySend(Message message)
    {
        var channel = Volatile.Read(ref _channel);
        if (channel is null)
        {
            _logger.Warn($"Cannot send the {message.Type} message: the channel is not open.");
            return false;
        }

        try
        {
            channel.Write(Framing.Frame(message));
            _logger.Debug($"Sent {message.Type}.");
            return true;
        }
        catch (ChannelException ex)
        {
            _logger.Warn($"Cannot send the {message.Type} message. {ex.Message}");
            return false;
        }
    }

    public void Dispose()
    {
        _stopping = true;
        _wake.Set();
        if (_thread is not null && !_thread.Join(TimeSpan.FromSeconds(2)))
        {
            _logger.Warn("The channel thread did not stop in time.");
        }
    }

    private void Run()
    {
        var buffer = new byte[ReadBufferSize];
        while (!_stopping)
        {
            var channel = Open();
            if (channel is not null)
            {
                try
                {
                    Pump(channel, buffer);
                }
                catch (ChannelException ex)
                {
                    if (!_stopping)
                    {
                        _logger.Info($"The channel closed. {ex.Message}. Reopening it.");
                    }
                }
                finally
                {
                    Volatile.Write(ref _channel, null);
                    channel.Dispose();
                }
            }

            if (!_stopping)
            {
                _wake.WaitOne(RetryDelay);
            }
        }

        _logger.Info("Channel loop stopped.");
    }

    private VirtualChannel? Open()
    {
        _restartRequested = false;

        if (User32.GetSystemMetrics(User32.SmRemoteSession) == 0)
        {
            Report("console",
                LogLevel.Info,
                "This is not a remote session. The agent waits for a Remote Desktop connection.");
            return null;
        }

        var channel = VirtualChannel.TryOpen(ProtocolConstants.ChannelName, out var error);
        if (channel is null)
        {
            Report("open:" + error,
                _sessionDisconnected ? LogLevel.Debug : LogLevel.Warn,
                $"Cannot open the {ProtocolConstants.ChannelName} virtual channel (error {error}: {new Win32Exception(error).Message}). "
                + "This is expected when the session was not opened with the RdpDrop client, or when a policy blocks the channel. "
                + $"Retrying every {RetryDelay.TotalSeconds:0} seconds.");
            return null;
        }

        _openFailures.Reset();
        _frameReader.Reset();
        Volatile.Write(ref _channel, channel);
        _logger.Info($"The {ProtocolConstants.ChannelName} virtual channel is open.");
        return channel;
    }

    private void Pump(VirtualChannel channel, byte[] buffer)
    {
        var instantEmptyReads = 0;
        while (!_stopping)
        {
            if (_restartRequested)
            {
                _restartRequested = false;
                _logger.Info($"Reopening the channel: {_restartReason}.");
                return;
            }

            var started = Environment.TickCount64;
            var count = channel.Read(buffer, ReadTimeoutMilliseconds);
            if (count == 0)
            {
                instantEmptyReads = Environment.TickCount64 - started < InstantReadMilliseconds ? instantEmptyReads + 1 : 0;
                if (instantEmptyReads >= MaxInstantEmptyReads)
                {
                    throw new ChannelException("The channel returns no data without waiting", 0);
                }

                continue;
            }

            instantEmptyReads = 0;
            _frameReader.Append(buffer.AsSpan(0, count));
            while (_frameReader.TryReadFrame(out var payload))
            {
                Dispatch(payload);
            }
        }
    }

    private void Dispatch(byte[] payload)
    {
        try
        {
            _payloadHandler?.Invoke(payload);
        }
        catch (Exception ex)
        {
            // A bug in one message must not take the channel down.
            _logger.Error("Unexpected error while handling a message.", ex);
        }
    }

    private void Report(string key, LogLevel level, string message)
    {
        _logger.Write(_openFailures.IsNew(key) ? level : LogLevel.Debug, message);
    }
}
