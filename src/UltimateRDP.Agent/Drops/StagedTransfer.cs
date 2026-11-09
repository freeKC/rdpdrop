using System.Globalization;
using Timer = System.Threading.Timer;
using UltimateRDP.Agent.Channel;
using UltimateRDP.Agent.Localization;
using UltimateRDP.Agent.Logging;
using UltimateRDP.Agent.Resources;
using UltimateRDP.Core.Agent;
using UltimateRDP.Core.Protocol;
using UltimateRDP.Core.Transfer;
using Message = UltimateRDP.Core.Protocol.Message;

namespace UltimateRDP.Agent.Drops;

/// <summary>
/// One transfer of dropped files through the channel, into
/// <c>%LOCALAPPDATA%\UltimateRDP\staging\&lt;transferId&gt;</c>. Feeds the messages of the
/// client to a <see cref="TransferReceiver"/>, sends its acknowledgements, and sends the
/// <c>transferResult</c> once it is over. A transfer that receives nothing for
/// <see cref="IdleTimeout"/> stops. What a failed or cancelled transfer wrote is deleted
/// at once; the files of a transfer that succeeded stay for the drop.
/// </summary>
/// <remarks>
/// Messages arrive on the channel thread and are handled there: writing a chunk, at most
/// 256 KiB, takes a fraction of a millisecond. <see cref="Cancel"/> and the idle check may
/// come from other threads; a lock keeps them apart.
/// </remarks>
internal sealed class StagedTransfer
{
    /// <summary>A transfer that receives nothing for this long stops.</summary>
    public static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(60);

    private const double BytesPerMegabyte = 1_000_000;
    private static readonly TimeSpan IdleCheckInterval = TimeSpan.FromSeconds(5);

    private readonly FileLogger _logger;
    private readonly StagingFolder _staging;
    private readonly Func<Message, bool> _send;
    private readonly ClientLanguage _language;
    private readonly TransferReceiver _receiver;
    private readonly Lock _gate = new();
    private readonly Timer _idleTimer;
    private readonly ChannelMeter? _meter;
    private ChannelMeasure? _measure;
    private long _lastMessageTicks = Environment.TickCount64;
    private int _ignoredAfterEnd;
    private bool _resultSent;
    private bool _finishedRaised;
    private LocalText? _stopReason;
    private DropStatus _stopStatus;

    public StagedTransfer(FileLogger logger, StagingFolder staging, string dragId, string transferId, IReadOnlyList<TransferItem> items, Func<Message, bool> send, ClientLanguage language, ChannelMeter? meter = null)
    {
        _logger = logger;
        _meter = meter;
        _staging = staging;
        _send = send;
        _language = language;
        DragId = dragId;
        TransferId = transferId;
        var folder = staging.FolderOf(transferId);
        StagedPaths = [.. items.Select(item => Path.Combine(folder, item.Name))];
        _receiver = new TransferReceiver(transferId, items, staging.CreateTransferStorage(transferId), () => Environment.TickCount64);
        _idleTimer = new Timer(_ => Guarded(CheckIdle), null, IdleCheckInterval, IdleCheckInterval);

        // Last, so that a constructor that throws leaves nothing counting.
        _measure = meter?.Begin();
    }

    /// <summary>
    /// Raised once, outside the lock, when the transfer is over: true when every file
    /// arrived and the copies can be dropped.
    /// </summary>
    public event Action<StagedTransfer, bool>? Finished;

    public string DragId { get; }

    public string TransferId { get; }

    /// <summary>Paths of the copies of the dropped items, in the order of the drag.</summary>
    public IReadOnlyList<string> StagedPaths { get; }

    /// <summary>Handles one transfer message of the client. Called on the channel thread.</summary>
    public void Handle(Message message)
    {
        lock (_gate)
        {
            if (_receiver.IsFinished)
            {
                // The registry stops these once the end handler ran; until then they are logged here.
                _ignoredAfterEnd++;
                _logger.Write(_ignoredAfterEnd == 1 || message is not FileChunkMessage ? LogLevel.Info : LogLevel.Debug, $"Transfer {TransferId}: {message.Type} ignored, the transfer is over ({_ignoredAfterEnd} ignored since its end).");
                return;
            }

            Interlocked.Exchange(ref _lastMessageTicks, Environment.TickCount64);
            if (message is TransferBeginMessage begin && !HasRoomFor(begin))
            {
                StopUnderLock(LocalText.Of(nameof(Strings.TransferNoSpace), Megabytes(FastTransferPolicy.RequiredFreeSpace(begin.Bytes)), Megabytes(_staging.FreeSpace() ?? 0)), DropStatus.Error);
            }
            else
            {
                if (message is TransferBeginMessage announced)
                {
                    _logger.Info($"Transfer {TransferId} of drag {DragId}: receiving {announced.Files} file(s), {announced.Bytes} bytes.");
                }

                if (_receiver.Handle(message) is { } ack)
                {
                    if (message is FileChunkMessage { Offset: 0 } first)
                    {
                        // One line per file: the first thing to look for when data does not get through.
                        _logger.Info($"Transfer {TransferId}: first chunk of file {first.FileIndex} received, {ack.Received} bytes in {first.Data.Length} characters of base64.");
                    }

                    _send(ack);
                }

                if (message is TransferCancelMessage cancel)
                {
                    _logger.Info($"Transfer {TransferId}: cancelled by the client, {cancel.Reason ?? "no reason given"}.");
                }
            }

            SendResultIfOver();
        }

        RaiseFinishedIfOver();
    }

    /// <summary>Stops the transfer for <paramref name="reason"/>. Callable from any thread.</summary>
    public void Cancel(LocalText reason)
    {
        lock (_gate)
        {
            if (_receiver.IsFinished)
            {
                return;
            }

            StopUnderLock(reason, DropStatus.Cancelled);
            SendResultIfOver();
        }

        RaiseFinishedIfOver();
    }

    /// <summary>Millions of bytes, rounded up, for the texts about free space.</summary>
    public static double Megabytes(long bytes) => Math.Ceiling(bytes / BytesPerMegabyte);

    private bool HasRoomFor(TransferBeginMessage begin)
    {
        var required = FastTransferPolicy.RequiredFreeSpace(begin.Bytes);
        var free = _staging.FreeSpace();
        if (free is null)
        {
            _logger.Warn($"Transfer {TransferId}: the free space of the staging folder is unknown; receiving {begin.Bytes} bytes anyway.");
            return true;
        }

        if (free < required)
        {
            _logger.Warn($"Transfer {TransferId}: {begin.Bytes} bytes announced, {required} bytes of free space needed and {free} free. Refused.");
            return false;
        }

        return true;
    }

    private void StopUnderLock(LocalText reason, DropStatus status)
    {
        _stopReason = reason;
        _stopStatus = status;
        _receiver.Cancel();
    }

    private void CheckIdle()
    {
        lock (_gate)
        {
            if (_receiver.IsFinished || Environment.TickCount64 - Interlocked.Read(ref _lastMessageTicks) < IdleTimeout.TotalMilliseconds)
            {
                return;
            }

            StopUnderLock(LocalText.Of(nameof(Strings.TransferIdle), (int)IdleTimeout.TotalSeconds), DropStatus.Error);
            SendResultIfOver();
        }

        RaiseFinishedIfOver();
    }

    private void SendResultIfOver()
    {
        if (!_receiver.IsFinished || _resultSent)
        {
            return;
        }

        _resultSent = true;
        _idleTimer.Dispose();
        var (status, text) = Describe();
        var seconds = _receiver.Seconds;
        var rate = seconds > 0 ? _receiver.ReceivedBytes / BytesPerMegabyte / seconds : 0;
        var outcome = status == DropStatus.Ok ? null : _receiver.FailureDetail ?? text?.ToString();
        _logger.Info(string.Create(
            CultureInfo.InvariantCulture,
            $"Transfer {TransferId} of drag {DragId}: {status}, {_receiver.ReceivedFiles} of {_receiver.ExpectedFiles} file(s), {_receiver.ReceivedBytes} of {_receiver.ExpectedBytes} bytes in {seconds:0.0} s, {rate:0.0} MB/s{(outcome is null ? string.Empty : ". " + outcome)}"));
        _send(new TransferResultMessage(TransferId, status, text?.In(_language.Culture), _receiver.ReceivedBytes, Math.Round(seconds, 3)));
        LogChannelSummary();
    }

    /// <summary>What the channel did during the transfer: the reads of the agent and the counters of the session.</summary>
    private void LogChannelSummary()
    {
        var measure = _measure;
        _measure = null;
        if (_meter is null || measure is null)
        {
            return;
        }

        try
        {
            foreach (var line in _meter.End(TransferId, measure))
            {
                _logger.Info(line);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.Warn($"Transfer {TransferId}: the channel summary could not be written. {ex.Message}");
        }
    }

    private (DropStatus Status, LocalText? Text) Describe()
    {
        if (_stopReason is not null)
        {
            return (_stopStatus, _stopReason);
        }

        var path = _receiver.FailurePath;
        return _receiver.State switch
        {
            TransferState.Succeeded => (DropStatus.Ok, null),
            TransferState.Cancelled => (DropStatus.Cancelled, LocalText.Of(nameof(Strings.TransferCancelled))),
            _ => (DropStatus.Error, _receiver.Failure switch
            {
                TransferFailure.HashMismatch or TransferFailure.SizeMismatch when path is not null => LocalText.Of(nameof(Strings.TransferChecksum), path),
                TransferFailure.BadName => LocalText.Of(nameof(Strings.TransferBadName), path),
                TransferFailure.WriteFailed => LocalText.Of(nameof(Strings.TransferWriteFailed), path, _receiver.FailureDetail),
                TransferFailure.Incomplete => LocalText.Of(nameof(Strings.TransferIncomplete), path),
                TransferFailure.MessageLost when _receiver.LostFileIndex is { } index => LocalText.Of(nameof(Strings.TransferFileMessageLost), _receiver.LostMessage, index),
                TransferFailure.MessageLost => LocalText.Of(nameof(Strings.TransferMessageLost), _receiver.LostMessage),
                _ => LocalText.Of(nameof(Strings.TransferDataLost)),
            }),
        };
    }

    private void RaiseFinishedIfOver()
    {
        bool succeeded;
        lock (_gate)
        {
            if (!_resultSent || _finishedRaised)
            {
                return;
            }

            _finishedRaised = true;
            succeeded = _receiver.State == TransferState.Succeeded;
        }

        try
        {
            Finished?.Invoke(this, succeeded);
        }
        catch (Exception ex)
        {
            _logger.Error($"Transfer {TransferId}: the end handler failed.", ex);
        }
    }

    /// <summary>Runs a timer callback. An error there would end the process; it is logged instead.</summary>
    private void Guarded(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            _logger.Error($"Transfer {TransferId}: unexpected error in a timer.", ex);
        }
    }
}
