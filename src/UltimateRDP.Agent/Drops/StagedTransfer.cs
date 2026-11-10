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
/// <para>
/// Messages arrive on the channel thread and are handled there: writing a chunk, at most
/// 256 KiB, takes a fraction of a millisecond. <see cref="Cancel"/> and the timers may
/// come from other threads; a lock keeps them apart.
/// </para>
/// <para>
/// Since 0.5.1 the lines of a client that numbers them go through a
/// <see cref="ReliableInbox"/> first, which hands them to the receiver in order and asks
/// the client for the missing ones. A timer checks the missing lines every
/// <see cref="GapCheckInterval"/>, and only while some are missing. A line that the client
/// sends again after it was processed, its newest one when it waits, gets a fresh
/// acknowledgement. Lines without a number are handled as they arrive.
/// </para>
/// </remarks>
internal sealed class StagedTransfer
{
    /// <summary>A transfer that receives nothing for this long stops.</summary>
    public static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(60);

    private const double BytesPerMegabyte = 1_000_000;
    private static readonly TimeSpan IdleCheckInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan GapCheckInterval = TimeSpan.FromMilliseconds(100);

    private readonly FileLogger _logger;
    private readonly StagingFolder _staging;
    private readonly Func<Message, bool> _send;
    private readonly ClientLanguage _language;
    private readonly TransferReceiver _receiver;
    private readonly ReliableInbox _inbox;
    private readonly Lock _gate = new();
    private readonly Timer _idleTimer;
    private Timer? _gapTimer;
    private readonly ChannelMeter? _meter;
    private ChannelMeasure? _measure;
    private long _lastMessageTicks = Environment.TickCount64;
    private int _ignoredAfterEnd;
    private bool _resultSent;
    private bool _finishedRaised;
    private LocalText? _stopReason;
    private string? _stopDetail;
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
        _inbox = new ReliableInbox(transferId);
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

            var now = Environment.TickCount64;
            Interlocked.Exchange(ref _lastMessageTicks, now);
            if (ReliableInbox.SeqOf(message) is { } seq)
            {
                Reorder(message, seq, now);
            }
            else
            {
                HandleInOrder(message);
            }

            SendResultIfOver();
        }

        RaiseFinishedIfOver();
    }

    /// <summary>Hands a numbered line to the inbox, then the lines it gives back to the receiver. Under the lock.</summary>
    private void Reorder(Message message, long seq, long now)
    {
        var expected = _inbox.Expected;
        var step = _inbox.Accept(message, now);
        if (step.Duplicate)
        {
            _logger.Debug($"Transfer {TransferId}: line {seq} ({message.Type}) arrived again, ignored.");
            if (seq == _inbox.Expected - 1 && _receiver.CurrentAck() is { } current)
            {
                // The client waits and sent its newest line again: an acknowledgement may have been lost.
                _logger.Info($"Transfer {TransferId}: line {seq}, the last one processed, came again; acknowledging it again.");
                _send(current with { Seq = seq });
            }

            return;
        }

        if (step.Released > 0)
        {
            _logger.Info($"Transfer {TransferId}: the missing lines from {expected} arrived; {step.Released} line(s) that waited behind them follow.");
        }

        foreach (var ready in step.Ready)
        {
            HandleInOrder(ready);
            if (_receiver.IsFinished)
            {
                StopGapTimer();
                return;
            }
        }

        if (step.Request is { } request)
        {
            _logger.Info($"Transfer {TransferId}: line {seq} came while line {_inbox.Expected} was expected; asking the client for lines {request.From} to {request.To} (request {_inbox.Requests}).");
            _send(request);
        }

        AfterInboxStep(step);
    }

    /// <summary>Starts or stops the check of the missing lines, and stops the transfer when the inbox gave up. Under the lock.</summary>
    private void AfterInboxStep(InboxStep step)
    {
        if (step.Failed)
        {
            _logger.Warn($"Transfer {TransferId}: {_inbox.FailureDetail}");
            StopGapTimer();
            StopUnderLock(LocalText.Of(nameof(Strings.TransferLinesLost)), DropStatus.Error, _inbox.FailureDetail);
            return;
        }

        if (_inbox.HasGap)
        {
            _gapTimer ??= new Timer(_ => Guarded(CheckGap), null, GapCheckInterval, GapCheckInterval);
        }
        else
        {
            StopGapTimer();
        }
    }

    /// <summary>Asks again for the missing lines after a silence, or gives up. On a timer thread.</summary>
    private void CheckGap()
    {
        lock (_gate)
        {
            if (_receiver.IsFinished)
            {
                StopGapTimer();
                return;
            }

            var step = _inbox.Check(Environment.TickCount64);
            if (step.Request is { } request)
            {
                _logger.Info($"Transfer {TransferId}: lines {request.From} to {request.To} still missing; asking the client again (request {_inbox.Requests}).");
                _send(request);
            }

            AfterInboxStep(step);
            SendResultIfOver();
        }

        RaiseFinishedIfOver();
    }

    /// <summary>Stops the check of the missing lines. Under the lock.</summary>
    private void StopGapTimer()
    {
        _gapTimer?.Dispose();
        _gapTimer = null;
    }

    /// <summary>Handles one message in the order of the transfer. Under the lock.</summary>
    private void HandleInOrder(Message message)
    {
        if (message is TransferBeginMessage begin && !HasRoomFor(begin))
        {
            StopUnderLock(LocalText.Of(nameof(Strings.TransferNoSpace), Megabytes(FastTransferPolicy.RequiredFreeSpace(begin.Bytes)), Megabytes(_staging.FreeSpace() ?? 0)), DropStatus.Error);
            return;
        }

        if (message is TransferBeginMessage announced)
        {
            _logger.Info($"Transfer {TransferId} of drag {DragId}: receiving {announced.Files} file(s), {announced.Bytes} bytes, chunks in {ChunkEncodings.OrDefault(announced.ChunkEncoding)}{(announced.Seq is null ? ", lines not numbered" : ", numbered lines")}.");
        }

        if (_receiver.Handle(message) is { } ack)
        {
            if (message is FileChunkMessage { Offset: 0 } first)
            {
                // One line per file: the first thing to look for when data does not get through.
                _logger.Info($"Transfer {TransferId}: first chunk of file {first.FileIndex} received, {ack.Received} bytes in {first.Data.Length} characters of {ChunkEncodings.OrDefault(first.Enc)}.");
            }

            // Every line up to this one was processed: the client may forget them.
            _send(ReliableInbox.SeqOf(message) is { } seq ? ack with { Seq = seq } : ack);
        }

        if (message is TransferCancelMessage cancel)
        {
            _logger.Info($"Transfer {TransferId}: cancelled by the client, {cancel.Reason ?? "no reason given"}.");
        }
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

    private void StopUnderLock(LocalText reason, DropStatus status, string? detail = null)
    {
        _stopReason = reason;
        _stopDetail = detail;
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
        StopGapTimer();
        var (status, text) = Describe();
        var seconds = _receiver.Seconds;
        var rate = seconds > 0 ? _receiver.ReceivedBytes / BytesPerMegabyte / seconds : 0;
        var outcome = status == DropStatus.Ok ? null : _receiver.FailureDetail ?? _stopDetail ?? text?.ToString();
        var lines = _inbox.Requests > 0 || _inbox.DuplicateLines > 0
            ? string.Create(CultureInfo.InvariantCulture, $"; {_inbox.Requests} resend request(s), {_inbox.ReleasedLines} line(s) held until the missing ones came, {_inbox.DuplicateLines} line(s) received twice")
            : string.Empty;
        _logger.Info(string.Create(
            CultureInfo.InvariantCulture,
            $"Transfer {TransferId} of drag {DragId}: {status}, {_receiver.ReceivedFiles} of {_receiver.ExpectedFiles} file(s), {_receiver.ReceivedBytes} of {_receiver.ExpectedBytes} bytes in {seconds:0.0} s, {rate:0.0} MB/s{lines}{(outcome is null ? string.Empty : ". " + outcome)}"));
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
