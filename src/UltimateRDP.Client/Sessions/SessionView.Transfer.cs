using System.Globalization;
using UltimateRDP.Client.Localization;
using UltimateRDP.Client.Logging;
using UltimateRDP.Client.Resources;
using UltimateRDP.Core.Agent;
using UltimateRDP.Core.Direct;
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
/// <para>
/// One transfer at a time: while one runs, new drags are refused with a status text and a
/// notice in the progress window. The Session menu and the window can cancel it; closing
/// the tab cancels it too.
/// </para>
/// <para>
/// Since 0.6.0, when the dragAck offers a direct connection and the <c>directTransfer</c>
/// setting is <c>auto</c>, a <see cref="DirectUpload"/> sends the files outside the channel
/// first. When it cannot be used before the agent acknowledged anything, the same drop goes
/// on through the channel with a <see cref="TransferUpload"/>, as in 0.5.2.
/// </para>
/// </remarks>
internal sealed partial class SessionView
{
    /// <summary>Raw bytes per chunk, from the <c>transferChunkBytes</c> setting.</summary>
    private readonly int _transferChunkBytes;

    /// <summary>Most bytes in flight, from the <c>transferWindowBytes</c> setting.</summary>
    private readonly long _transferWindowBytes;

    /// <summary>Whether a direct connection offered by the agent is used, from the <c>directTransfer</c> setting.</summary>
    private readonly DirectTransferMode _directTransferMode;

    /// <summary>The most connections of a direct transfer, from the <c>directConnections</c> setting.</summary>
    private readonly int _directConnections;

    private TransferUpload? _upload;

    /// <summary>The transfer through the direct connection, while it runs.</summary>
    private DirectUpload? _direct;

    /// <summary>The window of the current or last drop, until it is closed.</summary>
    private DropProgressForm? _progressWindow;

    /// <summary>True while files are being transferred to the agent, or a drop waits for the dragAck that may offer a transfer.</summary>
    public bool CanCancelTransfer => _upload is not null || _direct is not null || _held is not null;

    /// <summary>Cancels the transfer in progress, or the held drop, from the menu or the progress window.</summary>
    public void CancelTransfer()
    {
        if (_held is { } held)
        {
            GiveUpHeldDrop(held, LocalText.Of(nameof(Strings.DropCancelled)), "cancelled by the user", sendLeave: true);
            return;
        }

        if (_direct is { } direct)
        {
            ClientLog.Info($"Transfer {direct.TransferId}: cancelled by the user, on the direct connection.");
            EndDirect(direct, "The user cancelled the transfer.");
            ShowTransferFailure(direct, LocalText.Of(nameof(Strings.TransferCancelledByUser)));
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
        _agent.TransferResendReceived += (_, request) => OnTransferResend(request);
        _agent.SendFailed += (_, lines) => OnSendFailed(lines);
        _agent.LargeLineComing = () => _upload?.LargeLineComing == true;
    }

    /// <summary>The agent misses lines of the transfer: they go again, together, in order.</summary>
    private void OnTransferResend(TransferResendMessage request)
    {
        if (_upload is not { } upload || request.TransferId != upload.TransferId)
        {
            ClientLog.Info(Invariant($"Transfer {request.TransferId}: the agent asked for lines {request.From} to {request.To}, but this transfer is not in progress. Ignored."));
            return;
        }

        var lines = upload.OnResend(request);
        if (lines.Count > 0 && !_agent.SendTransferLines(lines))
        {
            upload.OnSendFailed();
        }
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

        if (_direct is { } direct && lines.Any(line => line.Subject == direct.TransferId || line.Subject == direct.DragId))
        {
            // Without its deferred drop the agent cannot drop what comes through the direct connection.
            OnDirectFailed(direct, LocalText.Of(nameof(Strings.TransferNotSent)));
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

    /// <summary>
    /// Starts the transfer of a deferred drop: through the direct connection when the agent
    /// offered one and the setting allows it, through the channel otherwise.
    /// </summary>
    private void StartTransfer(string dragId, string transferId, IReadOnlyList<DropItem> items, DirectTransferOffer? direct)
    {
        if (direct is not null && _directTransferMode == DirectTransferMode.Auto)
        {
            StartDirectTransfer(dragId, transferId, items, direct);
            return;
        }

        if (direct is not null)
        {
            ClientLog.Info(Invariant($"Transfer {transferId}: the agent offered a direct connection on port {direct.Port}; the directTransfer setting of the client is never, so the files go through the channel."));
        }

        StartChannelTransfer(dragId, transferId, items);
    }

    private void StartDirectTransfer(string dragId, string transferId, IReadOnlyList<DropItem> items, DirectTransferOffer direct)
    {
        var upload = new DirectUpload(this, dragId, transferId, items, direct, Host, _directConnections);
        upload.Planned += OnDirectPlanned;
        upload.Acknowledged += OnDirectAcknowledged;
        upload.FellBack += OnDirectFellBack;
        upload.Failed += OnDirectFailed;
        upload.Completed += OnDirectCompleted;
        _direct = upload;
        ClientLog.Info(Invariant($"Transfer {transferId} of drag {dragId}: started with {items.Count} item(s) through the direct connection, port {direct.Port} of {Host}{(direct.Host is { } hint ? ", address hint " + hint : string.Empty)}, up to {Math.Min(direct.Connections ?? 1, _directConnections)} connection(s) (the agent accepts {direct.Connections}, the setting allows {_directConnections}), certificate pinned to SHA-256 {direct.CertSha256}; the channel takes the files when the connection cannot be used."));
        ShowDropStatus(LocalText.Joined(LocalText.Of(nameof(Strings.TransferPreparing), items.Count), LocalText.Of(nameof(Strings.TransferDirect))));
        OpenProgressWindow(dragId, items, transfer: true);
        ProgressWindowOf(dragId)?.ShowDirect(true);
        upload.Start();
        RaiseStateChanged();
    }

    private void OnDirectPlanned(DirectUpload upload)
    {
        if (upload != _direct)
        {
            return;
        }

        // The window and the status bar keep the preparing stage until the first
        // acknowledgement: a slow open of a file must not look like a transfer at 0 B/s.
        ClientLog.Info(Invariant($"Transfer {upload.TransferId}: {upload.FileCount} file(s), {upload.TotalBytes} bytes, for the direct connection."));
    }

    private void OnDirectAcknowledged(DirectUpload upload)
    {
        if (upload == _direct && upload.Progress?.TryRefresh(Environment.TickCount64) == true)
        {
            ShowTransferProgress(upload);
        }
    }

    /// <summary>The direct connection cannot be used: the same drop goes on through the channel.</summary>
    private void OnDirectFellBack(DirectUpload upload, string reason)
    {
        if (upload != _direct)
        {
            return;
        }

        ClientLog.Info($"Transfer {upload.TransferId}: the direct connection cannot be used, {reason}; the files go through the channel.");
        _direct = null;
        ProgressWindowOf(upload)?.ShowDirect(false);
        StartChannelTransfer(upload.DragId, upload.TransferId, upload.Items);
    }

    /// <summary>The direct transfer failed after data got through, or the agent refused it: the agent is told through the channel.</summary>
    private void OnDirectFailed(DirectUpload upload, LocalText reason)
    {
        if (upload != _direct)
        {
            return;
        }

        ClientLog.Warn($"Transfer {upload.TransferId}: stopped on the direct connection, {reason.ToEnglish()}");
        LogDirectSummary(upload, null);
        EndDirect(upload, reason.ToEnglish());
        ShowTransferFailure(upload, reason);
    }

    /// <summary>The agent has every file: it drops the copies and the dropResult follows through the channel.</summary>
    private void OnDirectCompleted(DirectUpload upload, DirectResult result)
    {
        if (upload != _direct)
        {
            return;
        }

        LogDirectSummary(upload, result);
        _direct = null;
        RaiseStateChanged();
        var seconds = result.Milliseconds > 0 ? result.Milliseconds / 1000.0 : (Environment.TickCount64 - upload.StartTicks) / 1000.0;
        var rate = TransferProgressText.MegabytesPerSecond(result.Bytes, seconds);
        ShowDropStatus(LocalText.Of(nameof(Strings.TransferDropping), upload.FileCount, rate));
        ProgressWindowOf(upload)?.ShowTransferred(upload.FileCount, TimeSpan.FromSeconds(seconds), rate);
    }

    /// <summary>
    /// Ends the direct transfer on the client. With <paramref name="cancelReason"/>, the
    /// agent is told through the channel to forget the drop; the drop is then over too.
    /// </summary>
    private void EndDirect(DirectUpload upload, string? cancelReason)
    {
        upload.Stop(cancelReason ?? "The transfer ended on the agent.");
        _direct = null;
        if (cancelReason is not null)
        {
            _pendingDropId = null;
            _agent.SendTransfer(new TransferCancelMessage(upload.TransferId, cancelReason), upload.TransferId);
        }

        RaiseStateChanged();
    }

    private void StartChannelTransfer(string dragId, string transferId, IReadOnlyList<DropItem> items)
    {
        var encoding = ChunkEncodings.Choose(_agent.ClientToAgentModel);
        var upload = new TransferUpload(
            this,
            dragId,
            transferId,
            items,
            _transferChunkBytes,
            _transferWindowBytes,
            _agent.Model,
            encoding,
            _agent.ReliableTransfer,
            _agent.SendTransferLines);
        upload.Planned += OnTransferPlanned;
        upload.Failed += OnUploadFailed;
        _upload = upload;
        var window = TransferSender.EffectiveWindow(_transferWindowBytes, TransferSender.ClampChunkSize(_transferChunkBytes));
        ClientLog.Info(Invariant($"Transfer {transferId} of drag {dragId}: started with {items.Count} item(s), chunks of {TransferSender.ClampChunkSize(_transferChunkBytes)} bytes, window of {window} bytes (setting {_transferWindowBytes}), chunks in {encoding}, {WireCost(encoding, _agent.ClientToAgentModel)}, {(_agent.ReliableTransfer ? "numbered lines" : "lines not numbered")}."));
        _agent.BeginTransferMeasure(transferId, window);
        ShowDropStatus(LocalText.Of(nameof(Strings.TransferPreparing), items.Count));
        OpenProgressWindow(dragId, items, transfer: true);
        upload.Start();
        RaiseStateChanged();
    }

    /// <summary>How the data of the client reaches the agent and what a file byte costs on the wire, for the log.</summary>
    private static string WireCost(string encoding, ChannelByteModel clientToAgent) => clientToAgent == ChannelByteModel.Unknown
        ? "the agent did not say how the data of the client reaches it"
        : Invariant($"data of the client reaches the agent in the {clientToAgent} model, {ChunkEncodings.WireBytesPerFileByte(encoding, clientToAgent):0.###} wire bytes per file byte");

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
    private void ShowTransferProgress(IUploadView upload)
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
        if (upload.IsDirect)
        {
            text = LocalText.Joined(text, LocalText.Of(nameof(Strings.TransferDirect)));
        }

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
        if (_direct is { } direct && result.TransferId == direct.TransferId)
        {
            // The agent ended the drop through the channel while the files went directly.
            ClientLog.Info($"Transfer {direct.TransferId}: the agent ended the transfer through the channel with {result.Status}; the direct connection stops.");
            EndDirect(direct, cancelReason: null);
            _pendingDropId = null;
            ShowTransferFailure(direct, string.IsNullOrWhiteSpace(result.Message)
                ? LocalText.Of(nameof(Strings.TransferFailedNoDetail))
                : LocalText.Of(result.Status == DropStatus.Cancelled ? nameof(Strings.CancelledReason) : nameof(Strings.TransferFailedReason), result.Message));
            return;
        }

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
        if (_direct is { } direct)
        {
            ClientLog.Info($"Transfer {direct.TransferId}: stopped by the disconnection, on the direct connection.");
            direct.Stop("The session was disconnected.");
            _direct = null;
            ShowTransferFailure(direct, LocalText.Of(nameof(Strings.TransferInterrupted)));
            RaiseStateChanged();
        }

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

        if (_direct is { } direct)
        {
            ClientLog.Info($"Transfer {direct.TransferId}: cancelled, the tab of the session is closing.");
            EndDirect(direct, "The tab of the session was closed.");
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
    private void ShowTransferFailure(IUploadView upload, LocalText reason)
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
            if ((_upload?.DragId ?? _direct?.DragId ?? _held?.Live.Id) == window.DragId)
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
    private DropProgressForm? ProgressWindowOf(IUploadView upload) => ProgressWindowOf(upload.DragId);

    /// <summary>The window of the drop of <paramref name="dragId"/>, when it is still open.</summary>
    private DropProgressForm? ProgressWindowOf(string dragId) =>
        _progressWindow is { IsDisposed: false } window && window.DragId == dragId ? window : null;

    /// <summary>A new drag was refused because a transfer runs or a drop is held: the window says so too.</summary>
    private void NoteBusyRefusal()
    {
        if ((_upload?.DragId ?? _direct?.DragId ?? _held?.Live.Id) is { } dragId)
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
            $"Transfer {upload.TransferId} of drag {upload.DragId}: {result.Status} through the channel, {upload.FileCount} file(s), {result.Bytes} of {upload.TotalBytes} bytes in {seconds:0.0} s, {rate:0.0} MB/s from the drop; {result.Seconds:0.0} s and {agentRate:0.0} MB/s measured by the agent; {upload.DescribeResends()}."));
    }

    /// <summary>The summary of a transfer through the direct connection: the path, the port, the TLS protocol and the rate.</summary>
    private static void LogDirectSummary(DirectUpload upload, DirectResult? result)
    {
        var seconds = (Environment.TickCount64 - upload.StartTicks) / 1000.0;
        var bytes = result?.Bytes ?? upload.Progress?.DoneBytes ?? 0;
        var rate = TransferProgressText.MegabytesPerSecond(bytes, seconds);
        var agent = result is null
            ? "no final frame of the agent"
            : Invariant($"{result.Milliseconds / 1000.0:0.0} s and {TransferProgressText.MegabytesPerSecond(result.Bytes, result.Milliseconds / 1000.0):0.0} MB/s measured by the agent");
        ClientLog.Info(Invariant(
            $"Transfer {upload.TransferId} of drag {upload.DragId}: {(result is null ? "Failed" : result.Status)} through the direct connection to {upload.Endpoint ?? "(not connected)"}, port {upload.Port}, {upload.Tls ?? "no TLS session"}, {upload.ConnectionsUsed} connection(s), {upload.FileCount} file(s), {bytes} of {upload.TotalBytes} bytes in {seconds:0.0} s, {rate:0.0} MB/s from the drop; {agent}."));
    }
}
