using System.Globalization;
using UltimateRDP.Client.Localization;
using UltimateRDP.Client.Logging;
using UltimateRDP.Client.Resources;
using UltimateRDP.Core.Protocol;
using UltimateRDP.Core.Transfer;

namespace UltimateRDP.Client.Sessions;

/// <summary>
/// Transfer of the dropped files through the channel. When the dragAck of the agent asks
/// for it, the drop is deferred: the agent ends the live drag and keeps the point, a
/// <see cref="TransferUpload"/> sends the files, the status bar shows the progress, and
/// once the agent has every file it drops the copies at that point and sends the usual
/// dropResult.
/// </summary>
/// <remarks>
/// One transfer at a time: while one runs, new drags are refused with a status text. The
/// Session menu can cancel it.
/// </remarks>
internal sealed partial class SessionView
{
    private const double BytesPerMegabyte = 1_000_000;

    /// <summary>Raw bytes per chunk, from the <c>transferChunkBytes</c> setting.</summary>
    private readonly int _transferChunkBytes;

    private TransferUpload? _upload;

    /// <summary>True while files are being transferred to the agent.</summary>
    public bool CanCancelTransfer => _upload is not null;

    /// <summary>Cancels the transfer in progress, from the menu.</summary>
    public void CancelTransfer()
    {
        if (_upload is not { } upload)
        {
            return;
        }

        ClientLog.Info($"Transfer {upload.TransferId}: cancelled by the user.");
        EndTransfer(upload, "The user cancelled the transfer.");
        ShowDropStatus(LocalText.Of(nameof(Strings.TransferCancelledByUser)));
    }

    private void InitializeTransfer()
    {
        _agent.FileAckReceived += (_, ack) => OnFileAck(ack);
        _agent.TransferResultReceived += (_, result) => OnTransferResult(result);
    }

    private void StartTransfer(string dragId, string transferId, IReadOnlyList<DropItem> items)
    {
        var upload = new TransferUpload(
            this,
            dragId,
            transferId,
            items,
            _transferChunkBytes,
            _agent.Model,
            message => _agent.SendTransfer(message, transferId),
            line => _agent.SendTransferLine(line, transferId));
        upload.Planned += OnTransferPlanned;
        upload.Failed += OnUploadFailed;
        _upload = upload;
        ClientLog.Info($"Transfer {transferId} of drag {dragId}: started with {items.Count} item(s).");
        ShowDropStatus(LocalText.Of(nameof(Strings.TransferPreparing), items.Count));
        upload.Start();
        RaiseStateChanged();
    }

    private void OnTransferPlanned(TransferUpload upload)
    {
        if (upload != _upload)
        {
            return;
        }

        ClientLog.Info(Invariant($"Transfer {upload.TransferId}: sending {upload.FileCount} file(s), {upload.TotalBytes} bytes."));
        ShowTransferProgress(upload);
    }

    private void OnFileAck(FileAckMessage ack)
    {
        if (_upload is not { } upload || ack.TransferId != upload.TransferId)
        {
            return;
        }

        upload.OnAck(ack);
        if (upload.Progress?.TryRefresh(Environment.TickCount64) == true)
        {
            ShowTransferProgress(upload);
        }
    }

    /// <summary>
    /// "Transferring 3 file(s): 42 % (12.3 MB/s, 1 min left)". While a drag hovers over
    /// the session, the text waits, so that the reason the drag is refused stays visible.
    /// </summary>
    private void ShowTransferProgress(TransferUpload upload)
    {
        if (upload.Progress is not { } progress)
        {
            return;
        }

        var text = progress.BytesPerSecond is { } rate && progress.Remaining is { } remaining
            ? LocalText.Of(nameof(Strings.TransferProgressRate), upload.FileCount, progress.Percent, rate / BytesPerMegabyte, TimeLeftText(remaining))
            : LocalText.Of(nameof(Strings.TransferProgress), upload.FileCount, progress.Percent);
        if (_dropTextBeforeDrag is not null)
        {
            _dropTextBeforeDrag = text;
            return;
        }

        ShowDropStatus(text);
    }

    private static LocalText TimeLeftText(TimeSpan remaining)
    {
        var left = TimeLeft.From(remaining);
        return left.Unit switch
        {
            TimeLeftUnit.Seconds => LocalText.Of(nameof(Strings.TimeLeftSeconds), left.Seconds),
            TimeLeftUnit.Minutes => LocalText.Of(nameof(Strings.TimeLeftMinutes), left.Minutes),
            _ => LocalText.Of(nameof(Strings.TimeLeftHours), left.Hours, left.Minutes),
        };
    }

    private void OnTransferResult(TransferResultMessage result)
    {
        if (_upload is not { } upload || result.TransferId != upload.TransferId)
        {
            ClientLog.Info($"Transfer result {result.TransferId} matches no transfer in progress. Ignored.");
            return;
        }

        LogSummary(upload, result);
        EndTransfer(upload, cancelReason: null);
        switch (result.Status)
        {
            case DropStatus.Ok:
                // The drop of the copies follows; its dropResult replaces this text.
                var seconds = result.Seconds > 0 ? result.Seconds : (Environment.TickCount64 - upload.StartTicks) / 1000.0;
                ShowDropStatus(LocalText.Of(nameof(Strings.TransferDropping), upload.FileCount, seconds > 0 ? result.Bytes / BytesPerMegabyte / seconds : 0));
                break;

            case DropStatus.Cancelled:
                _pendingDropId = null;
                ShowDropStatus(string.IsNullOrWhiteSpace(result.Message)
                    ? LocalText.Of(nameof(Strings.TransferCancelledByUser))
                    : LocalText.Of(nameof(Strings.CancelledReason), result.Message));
                break;

            default:
                _pendingDropId = null;
                ShowDropStatus(string.IsNullOrWhiteSpace(result.Message)
                    ? LocalText.Of(nameof(Strings.TransferFailedNoDetail))
                    : LocalText.Of(nameof(Strings.TransferFailedReason), result.Message));
                break;
        }
    }

    /// <summary>The client could not go on: the agent is told, and the reason shown.</summary>
    private void OnUploadFailed(TransferUpload upload, LocalText reason)
    {
        if (upload != _upload)
        {
            return;
        }

        ClientLog.Warn($"Transfer {upload.TransferId}: stopped on the client, {reason.ToEnglish()}");
        EndTransfer(upload, reason.ToEnglish());
        ShowDropStatus(reason);
        if (upload.AckMissing)
        {
            // A hello tells whether short messages still get through, and finds a restarted agent.
            _agent.Recheck("no transfer acknowledgement");
        }
    }

    /// <summary>The session was disconnected: the transfer is lost, and the agent deletes what it received.</summary>
    private void AbortTransfer()
    {
        if (_upload is not { } upload)
        {
            return;
        }

        ClientLog.Info($"Transfer {upload.TransferId}: stopped by the disconnection.");
        upload.Stop();
        _upload = null;
        ShowDropStatus(LocalText.Of(nameof(Strings.TransferInterrupted)));
        RaiseStateChanged();
    }

    /// <summary>
    /// Ends the transfer on the client. With <paramref name="cancelReason"/>, the agent is
    /// told to stop and delete what it received; the drop is then over too.
    /// </summary>
    private void EndTransfer(TransferUpload upload, string? cancelReason)
    {
        upload.Stop();
        _upload = null;
        if (cancelReason is not null)
        {
            _pendingDropId = null;
            _agent.SendTransfer(new TransferCancelMessage(upload.TransferId, cancelReason), upload.TransferId);
        }

        RaiseStateChanged();
    }

    private static void LogSummary(TransferUpload upload, TransferResultMessage result)
    {
        var seconds = (Environment.TickCount64 - upload.StartTicks) / 1000.0;
        var rate = seconds > 0 ? result.Bytes / BytesPerMegabyte / seconds : 0;
        var agentRate = result.Seconds > 0 ? result.Bytes / BytesPerMegabyte / result.Seconds : 0;
        ClientLog.Info(Invariant(
            $"Transfer {upload.TransferId} of drag {upload.DragId}: {result.Status}, {upload.FileCount} file(s), {result.Bytes} of {upload.TotalBytes} bytes in {seconds:0.0} s, {rate:0.0} MB/s from the drop; {result.Seconds:0.0} s and {agentRate:0.0} MB/s measured by the agent."));
    }
}
