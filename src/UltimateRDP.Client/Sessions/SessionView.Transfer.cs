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
/// <see cref="TransferUpload"/> sends the files, the status bar and a
/// <see cref="DropProgressForm"/> show the progress, and once the agent has every
/// file it drops the copies at that point and sends the usual dropResult.
/// </summary>
/// <remarks>
/// One transfer at a time: while one runs, new drags are refused with a status text and a
/// notice in the progress window. The Session menu and the window can cancel it; closing
/// the tab cancels it too.
/// </remarks>
internal sealed partial class SessionView
{
    /// <summary>Raw bytes per chunk, from the <c>transferChunkBytes</c> setting.</summary>
    private readonly int _transferChunkBytes;

    /// <summary>Most bytes in flight, from the <c>transferWindowBytes</c> setting.</summary>
    private readonly long _transferWindowBytes;

    private TransferUpload? _upload;

    /// <summary>The window of the current or last drop, until it is closed.</summary>
    private DropProgressForm? _progressWindow;

    /// <summary>True while files are being transferred to the agent, or a drop waits for the dragAck that may offer a transfer.</summary>
    public bool CanCancelTransfer => _upload is not null || _held is not null;

    /// <summary>Cancels the transfer in progress, or the held drop, from the menu or the progress window.</summary>
    public void CancelTransfer()
    {
        if (_held is { } held)
        {
            GiveUpHeldDrop(held, LocalText.Of(nameof(Strings.DropCancelled)), "cancelled by the user", sendLeave: true);
            return;
        }

        if (_upload is not { } upload)
        {
            return;
        }

        ClientLog.Info($"Transfer {upload.TransferId}: cancelled by the user.");
        EndTransfer(upload, "The user cancelled the transfer.");
        ShowTransferFailure(upload, LocalText.Of(nameof(Strings.TransferCancelledByUser)));
    }

    private void InitializeTransfer()
    {
        _agent.FileAckReceived += (_, ack) => OnFileAck(ack);
        _agent.TransferResultReceived += (_, result) => OnTransferResult(result);
        _agent.SendFailed += (_, lines) => OnSendFailed(lines);
    }

    /// <summary>
    /// A call of the control failed: a transfer whose lines were lost stops, and a drop whose
    /// line was lost shows that it was not sent. A lost dragEnter or logoff is caught by the
    /// wait for its answer.
    /// </summary>
    private void OnSendFailed(IReadOnlyList<QueuedLine> lines)
    {
        if (_upload is { } upload && lines.Any(line => line.Subject == upload.TransferId || line.Subject == upload.DragId))
        {
            upload.OnSendFailed();
            return;
        }

        if (_pendingDropId is { } dropId && lines.Any(line => line.Type == MessageTypes.Drop && line.Subject == dropId))
        {
            ClientLog.Warn($"Drop {dropId}: its line was not sent.");
            _pendingDropId = null;
            var reason = LocalText.Of(nameof(Strings.DropFailedNotSent));
            ShowDropStatus(reason);
            ShowDropResultInWindow(dropId, reason, failed: true);
        }
    }

    private void StartTransfer(string dragId, string transferId, IReadOnlyList<DropItem> items)
    {
        var upload = new TransferUpload(
            this,
            dragId,
            transferId,
            items,
            _transferChunkBytes,
            _transferWindowBytes,
            _agent.Model,
            message => _agent.SendTransfer(message, transferId),
            (line, fileIndex, position) => _agent.SendTransferLine(line, transferId, fileIndex, position));
        upload.Planned += OnTransferPlanned;
        upload.Failed += OnUploadFailed;
        _upload = upload;
        var window = TransferSender.EffectiveWindow(_transferWindowBytes, TransferSender.ClampChunkSize(_transferChunkBytes));
        ClientLog.Info(Invariant($"Transfer {transferId} of drag {dragId}: started with {items.Count} item(s), chunks of {TransferSender.ClampChunkSize(_transferChunkBytes)} bytes, window of {window} bytes (setting {_transferWindowBytes})."));
        _agent.BeginTransferMeasure(transferId, window);
        ShowDropStatus(LocalText.Of(nameof(Strings.TransferPreparing), items.Count));
        OpenProgressWindow(dragId, items, transfer: true);
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

        if (upload.OnAck(ack) is { } acknowledged)
        {
            _agent.OnTransferAcknowledged(upload.TransferId, acknowledged);
        }

        if (upload.Progress?.TryRefresh(Environment.TickCount64) == true)
        {
            ShowTransferProgress(upload);
        }
    }

    /// <summary>
    /// "Transferring 3 file(s): 42 % (12.3 MB/s, 1 min left)" in the status bar, and the
    /// same progress in the window. While a drag hovers over the session, the status text
    /// waits, so that the reason the drag is refused stays visible.
    /// </summary>
    private void ShowTransferProgress(TransferUpload upload)
    {
        if (upload.Progress is not { } progress)
        {
            return;
        }

        if (ProgressWindowOf(upload) is { } window)
        {
            var index = TransferProgress.CurrentFile(upload.FileSizes, progress.DoneBytes);
            if (index >= 0)
            {
                window.ShowProgress(upload.Files[index].RelativePath, index + 1, upload.Files.Count, progress);
            }
        }

        var text = progress.BytesPerSecond is { } rate && progress.Remaining is { } remaining
            ? LocalText.Of(nameof(Strings.TransferProgressRate), upload.FileCount, progress.Percent, rate / TransferProgressText.BytesPerMegabyte, TimeLeftText(remaining))
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
                var rate = TransferProgressText.MegabytesPerSecond(result.Bytes, seconds);
                ShowDropStatus(LocalText.Of(nameof(Strings.TransferDropping), upload.FileCount, rate));
                ProgressWindowOf(upload)?.ShowTransferred(upload.FileCount, TimeSpan.FromSeconds(seconds), rate);
                break;

            case DropStatus.Cancelled:
                _pendingDropId = null;
                ShowTransferFailure(upload, string.IsNullOrWhiteSpace(result.Message)
                    ? LocalText.Of(nameof(Strings.TransferCancelledByUser))
                    : LocalText.Of(nameof(Strings.CancelledReason), result.Message));
                break;

            default:
                _pendingDropId = null;
                ShowTransferFailure(upload, string.IsNullOrWhiteSpace(result.Message)
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
        ShowTransferFailure(upload, reason);
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
        _agent.EndTransferMeasure(upload.TransferId);
        upload.Stop();
        _upload = null;
        ShowTransferFailure(upload, LocalText.Of(nameof(Strings.TransferInterrupted)));
        RaiseStateChanged();
    }

    /// <summary>
    /// The tab is closing: the transfer is cancelled while the agent can still be told, so
    /// that it deletes what it received at once, and the window closes.
    /// </summary>
    private void CancelTransferForClose()
    {
        if (_held is { } held)
        {
            GiveUpHeldDrop(held, LocalText.Of(nameof(Strings.DropCancelled)), "the tab of the session is closing", sendLeave: true);
        }

        if (_upload is { } upload)
        {
            ClientLog.Info($"Transfer {upload.TransferId}: cancelled, the tab of the session is closing.");
            EndTransfer(upload, "The tab of the session was closed.");
        }

        CloseProgressWindow();
    }

    /// <summary>
    /// Ends the transfer on the client. With <paramref name="cancelReason"/>, the agent is
    /// told to stop and delete what it received; the drop is then over too.
    /// </summary>
    private void EndTransfer(TransferUpload upload, string? cancelReason)
    {
        _agent.EndTransferMeasure(upload.TransferId);
        upload.Stop();
        _upload = null;
        if (cancelReason is not null)
        {
            _pendingDropId = null;
            _agent.SendTransfer(new TransferCancelMessage(upload.TransferId, cancelReason), upload.TransferId);
        }

        RaiseStateChanged();
    }

    /// <summary>Shows why the transfer ended, in the status bar and in red in its window.</summary>
    private void ShowTransferFailure(TransferUpload upload, LocalText reason)
    {
        ShowDropStatus(reason);
        ProgressWindowOf(upload)?.ShowFailure(reason);
    }

    /// <summary>
    /// Opens the window of a new drop, centered over the session, without taking the focus:
    /// preparing a <paramref name="transfer"/>, or dropping through drive redirection. A
    /// window already open for the same drag, the one of a held drop, moves on to that stage.
    /// </summary>
    private void OpenProgressWindow(string dragId, IReadOnlyList<DropItem> items, bool transfer)
    {
        var firstItem = items.Count > 0 ? TransferPaths.ItemName(items[0].ClientPath) : string.Empty;
        if (ProgressWindowOf(dragId) is { IsRunning: true } open)
        {
            ShowStage(open, firstItem, items.Count, transfer);
            return;
        }

        CloseProgressWindow();
        var window = new DropProgressForm(dragId, Host);
        window.CancelRequested += (_, _) =>
        {
            if ((_upload?.DragId ?? _held?.Live.Id) == window.DragId)
            {
                CancelTransfer();
            }
        };
        window.FormClosed += (_, _) =>
        {
            if (_progressWindow == window)
            {
                _progressWindow = null;
            }
        };
        _progressWindow = window;
        ShowStage(window, firstItem, items.Count, transfer);
        window.ShowOver(FindForm(), SessionScreenArea());
    }

    private static void ShowStage(DropProgressForm window, string firstItem, int itemCount, bool transfer)
    {
        if (transfer)
        {
            window.ShowPreparing(firstItem, itemCount);
        }
        else
        {
            window.ShowDropping(firstItem, itemCount);
        }
    }

    /// <summary>The window of <paramref name="upload"/>, when it is still open.</summary>
    private DropProgressForm? ProgressWindowOf(TransferUpload upload) => ProgressWindowOf(upload.DragId);

    /// <summary>The window of the drop of <paramref name="dragId"/>, when it is still open.</summary>
    private DropProgressForm? ProgressWindowOf(string dragId) =>
        _progressWindow is { IsDisposed: false } window && window.DragId == dragId ? window : null;

    /// <summary>A new drag was refused because a transfer runs or a drop is held: the window says so too.</summary>
    private void NoteBusyRefusal()
    {
        if ((_upload?.DragId ?? _held?.Live.Id) is { } dragId)
        {
            ProgressWindowOf(dragId)?.ShowBusyNotice();
        }
    }

    /// <summary>
    /// A drop is over: its window shows the result, then closes, or keeps a failure until
    /// the user closes it.
    /// </summary>
    private void ShowDropResultInWindow(string dragId, LocalText result, bool failed)
    {
        if (_progressWindow is { IsDisposed: false } window && window.DragId == dragId)
        {
            window.ShowDropResult(result, failed);
        }
    }

    /// <summary>The session was disconnected while a drop waited for its result.</summary>
    private void AbortDropInWindow()
    {
        if (_progressWindow is { IsDisposed: false, IsWaitingForDrop: true } window)
        {
            window.ShowFailure(LocalText.Of(nameof(Strings.DropInterrupted)));
        }
    }

    /// <summary>A new drag starts: the window of an ended transfer has nothing more to say.</summary>
    private void CloseEndedProgressWindow()
    {
        if (_progressWindow is { IsRunning: false })
        {
            CloseProgressWindow();
        }
    }

    private void CloseProgressWindow()
    {
        var window = _progressWindow;
        _progressWindow = null;
        if (window is { IsDisposed: false })
        {
            window.Close();
        }
    }

    /// <summary>The screen area of the session, to center the window over it.</summary>
    private Rectangle SessionScreenArea() =>
        _rdp.IsHandleCreated
            ? _rdp.FullScreenBounds ?? _rdp.RectangleToScreen(_rdp.ClientRectangle)
            : RectangleToScreen(ClientRectangle);

    private static void LogSummary(TransferUpload upload, TransferResultMessage result)
    {
        var seconds = (Environment.TickCount64 - upload.StartTicks) / 1000.0;
        var rate = TransferProgressText.MegabytesPerSecond(result.Bytes, seconds);
        var agentRate = TransferProgressText.MegabytesPerSecond(result.Bytes, result.Seconds);
        ClientLog.Info(Invariant(
            $"Transfer {upload.TransferId} of drag {upload.DragId}: {result.Status}, {upload.FileCount} file(s), {result.Bytes} of {upload.TotalBytes} bytes in {seconds:0.0} s, {rate:0.0} MB/s from the drop; {result.Seconds:0.0} s and {agentRate:0.0} MB/s measured by the agent."));
    }
}
