using System.Diagnostics;
using System.Globalization;
using UltimateRDP.Client.Channel;
using UltimateRDP.Client.Localization;
using UltimateRDP.Client.Logging;
using UltimateRDP.Client.Rdp;
using UltimateRDP.Client.Resources;
using UltimateRDP.Core.Paths;
using UltimateRDP.Core.Protocol;
using UltimateRDP.Core.Transfer;

namespace UltimateRDP.Client.Sessions;

/// <summary>
/// Drop handling of the session: answers drags over the control, sends the drag and drop
/// messages to the agent and shows their results.
/// </summary>
/// <remarks>
/// <para>
/// The control, the OLE targets on its child windows and the host window (through
/// <see cref="ProcessDragOver"/> and the other Process methods) all end up in the same
/// handlers, so every path gives the same answer.
/// </para>
/// <para>
/// The drag is live: the first drag over sends dragEnter, the agent starts a drag inside
/// the session and answers dragAck, moves follow as dragMove and the drag ends with drop
/// or dragLeave.
/// </para>
/// <para>
/// When the dragAck asks for a transfer, the drop is deferred and the files follow through
/// the channel; see SessionView.Transfer.cs. Every drop that goes out opens a
/// <see cref="DropProgressForm"/>, which shows the drop until its result.
/// </para>
/// <para>
/// A drop that happens before the dragAck arrived is held until it does, for at most
/// <see cref="DropDecision.MaxWait"/>, so that a quick release does not lose the transfer
/// that the dragAck may offer. The moves that keep the live drag of the agent alive go on
/// meanwhile, and a new drag is refused as during a transfer.
/// </para>
/// </remarks>
internal sealed partial class SessionView
{
    private const int AckTimeoutMilliseconds = 1000;
    private const int MoveIntervalMilliseconds = 33;
    private const int KeepAliveMilliseconds = 1000;

    /// <summary>Longest time spent counting the size of the dropped folders when the drag starts.</summary>
    private static readonly TimeSpan FolderSizeBudget = TimeSpan.FromMilliseconds(250);

    // Moving between the control, its child windows and the host window gives a leave
    // and an enter in a row; a leave only counts when no drag over follows it quickly.
    private const int LeaveDelayMilliseconds = 150;

    private readonly System.Windows.Forms.Timer _ackTimer = new() { Interval = AckTimeoutMilliseconds };
    private readonly System.Windows.Forms.Timer _moveTimer = new() { Interval = MoveIntervalMilliseconds };
    private readonly System.Windows.Forms.Timer _leaveTimer = new() { Interval = LeaveDelayMilliseconds };
    private readonly System.Windows.Forms.Timer _holdTimer = new() { Interval = (int)DropDecision.MaxWait.TotalMilliseconds };
    private DropZoneBanner? _banner;
    private LiveDrag? _live;

    /// <summary>The drop that waits for the dragAck of its drag, if any.</summary>
    private HeldDrop? _held;
    private string? _pendingDropId;
    private LocalText? _dropTextBeforeDrag;

    private enum AckState
    {
        /// <summary>dragEnter sent, no answer yet.</summary>
        Pending,

        /// <summary>The agent runs the drag in the session.</summary>
        Accepted,

        /// <summary>The agent or the client refused the drag.</summary>
        Refused,

        /// <summary>No answer within a second.</summary>
        TimedOut,
    }

    /// <summary>
    /// Answers a drag over the host window at <paramref name="screenPoint"/>. The control
    /// refuses points outside the session area.
    /// </summary>
    public DragDropEffects ProcessDragOver(IDataObject? data, Point screenPoint, DragDropEffects allowed, int oleKeyState = 0) =>
        _rdp.ProcessDragOver(data, screenPoint, allowed, oleKeyState);

    /// <summary>Handles a drop on the host window at <paramref name="screenPoint"/>.</summary>
    public DragDropEffects ProcessDrop(IDataObject? data, Point screenPoint, DragDropEffects allowed, int oleKeyState = 0) =>
        _rdp.ProcessDrop(data, screenPoint, allowed, oleKeyState);

    /// <summary>Handles the end of a drag over the host window that did not drop.</summary>
    public void ProcessDragLeave() => _rdp.ProcessDragLeave();

    /// <summary>Wires the drop timers and the agent events. Called by the constructor.</summary>
    private void InitializeDrops()
    {
        _ackTimer.Tick += (_, _) => OnAckTimeout();
        _moveTimer.Tick += (_, _) => FlushMove(keepAlive: true);
        _leaveTimer.Tick += (_, _) => OnLeaveConfirmed();
        _holdTimer.Tick += (_, _) => OnHoldTick();
        _agent.DragAckReceived += (_, ack) => OnDragAck(ack);
    }

    /// <summary>Forgets the current drag and stops a transfer, after a disconnection.</summary>
    private void ResetDrops()
    {
        _pendingDropId = null;
        _live = null;
        if (_held is { } held)
        {
            // The session is gone: the agent cannot be told, and its watchdog ends the drag.
            GiveUpHeldDrop(held, LocalText.Of(nameof(Strings.DropInterrupted)), "the session was disconnected", sendLeave: false);
        }

        StopDragTimers();
        HideBanner();
        AbortTransfer();
        AbortDropInWindow();
    }

    private void DisposeDrops()
    {
        _upload?.Stop();
        _held = null;
        CloseProgressWindow();
        StopDragTimers();
        _ackTimer.Dispose();
        _moveTimer.Dispose();
        _leaveTimer.Dispose();
        _holdTimer.Dispose();
        _banner?.Dispose();
        _banner = null;
    }

    /// <summary>
    /// Why a drop cannot be sent now, or null when it can.
    /// </summary>
    private LocalText? DropRefusalReason()
    {
        if (State != SessionState.Connected)
        {
            return LocalText.Of(nameof(Strings.DropRefusedNotConnected));
        }

        if (_upload is not null || _held is not null)
        {
            // One transfer at a time: a new drag would end the one the agent is receiving,
            // or the live drag whose dragAck a held drop waits for.
            return LocalText.Of(nameof(Strings.TransferBusy));
        }

        return _agent.State switch
        {
            AgentState.Ready => null,
            AgentState.Waiting => LocalText.Of(nameof(Strings.DropRefusedAgentWaiting)),
            _ => LocalText.Of(nameof(Strings.DropRefusedNoAgent)),
        };
    }

    private void OnFileDragOver(object? sender, FileDragEventArgs e)
    {
        _leaveTimer.Stop();
        _dropTextBeforeDrag ??= _dropText;
        var reason = DropRefusalReason();
        if (reason is not null)
        {
            EndLiveDrag(sendLeave: true);
            HideBanner();
            e.Effect = DragDropEffects.None;
            NoteBusyRefusal();
            ShowDropStatus(reason);
            return;
        }

        ShowBanner();
        if (_live is null || !_live.Files.SequenceEqual(e.Files))
        {
            EndLiveDrag(sendLeave: true);
            StartLiveDrag(e);
        }
        else
        {
            QueueMove(e);
        }

        e.Effect = _live is { State: AckState.Pending or AckState.Accepted } ? DragDropEffects.Copy : DragDropEffects.None;
        if (_live is { Problem: { } problem })
        {
            ShowDropStatus(problem);
        }
    }

    private void OnFileDragLeft()
    {
        // Confirmed by OnLeaveConfirmed unless a drag over follows at once.
        _leaveTimer.Stop();
        _leaveTimer.Start();
    }

    private void OnLeaveConfirmed()
    {
        _leaveTimer.Stop();
        if (_live is not null)
        {
            ClientLog.Info($"Drag {_live.Id} left the session.");
        }

        EndLiveDrag(sendLeave: true);
        HideBanner();
        if (_dropTextBeforeDrag is not null)
        {
            ShowDropStatus(_dropTextBeforeDrag);
            _dropTextBeforeDrag = null;
        }
    }

    private void OnFileDropped(object? sender, FileDragEventArgs e)
    {
        _leaveTimer.Stop();
        HideBanner();
        _dropTextBeforeDrag = null;
        var reason = DropRefusalReason();
        if (reason is not null)
        {
            ClientLog.Info(Invariant($"Drop of {e.Files.Count} item(s) refused: {reason.ToEnglish()}"));
            NoteBusyRefusal();
            EndLiveDrag(sendLeave: true);
            ShowDropStatus(reason);
            return;
        }

        if (_live is { } live && live.Files.SequenceEqual(e.Files))
        {
            e.Effect = DropLiveDrag(live, e);
            return;
        }

        // Every drop ends a live drag; without one for these files there is nothing to drop.
        ClientLog.Info(Invariant($"Drop of {e.Files.Count} item(s) refused: no live drag for these files."));
        EndLiveDrag(sendLeave: true);
        ShowDropStatus(LocalText.Of(nameof(Strings.DropRefusedNoLiveDrag)));
    }

    private void StartLiveDrag(FileDragEventArgs e)
    {
        CloseEndedProgressWindow();
        var id = Guid.NewGuid().ToString("N");
        var items = MapItems(e.Files, out var pathProblem);
        if (items is null)
        {
            // Nothing is sent: the agent cannot read such paths either.
            _live = new LiveDrag(id, e.Files, []) { State = AckState.Refused, Problem = pathProblem };
            return;
        }

        var remote = ToRemote(e.ClientPoint);
        var live = new LiveDrag(id, e.Files, items)
        {
            LastPoint = remote,
            LastKeys = e.KeyState,
            LastSent = Environment.TickCount64,
        };
        _live = live;
        LogDrag("Drag enter", id, e, remote);
        if (!_agent.SendDrag(new DragEnterMessage(id, remote.X, remote.Y, items, e.KeyState), id))
        {
            live.State = AckState.Refused;
            live.Problem = LocalText.Of(nameof(Strings.DropFailedNotSent));
            return;
        }

        live.Sent = true;
        _ackTimer.Stop();
        _ackTimer.Start();
        _moveTimer.Start();
        ShowDropStatus(LocalText.Of(nameof(Strings.Dragging), items.Count));
    }

    private void QueueMove(FileDragEventArgs e)
    {
        var live = _live;
        if (live is not { Sent: true, State: AckState.Pending or AckState.Accepted })
        {
            return;
        }

        var remote = ToRemote(e.ClientPoint);
        if (remote == live.LastPoint && e.KeyState == live.LastKeys && live.Queued is null)
        {
            return;
        }

        live.Queued = (remote, e.KeyState);
        FlushMove(keepAlive: false);
    }

    /// <summary>
    /// Sends the queued move once the last message is at least a thirtieth of a second
    /// old. While the cursor rests, a move is repeated every second so that the agent does
    /// not cancel the drag after five seconds without news. The moves go on while a drop
    /// is held for the dragAck, since the live drag of the agent runs until the drop.
    /// </summary>
    private void FlushMove(bool keepAlive)
    {
        var live = _live ?? _held?.Live;
        if (live is not { Sent: true, State: AckState.Pending or AckState.Accepted })
        {
            _moveTimer.Stop();
            return;
        }

        var elapsed = Environment.TickCount64 - live.LastSent;
        if (live.Queued is { } queued && elapsed >= MoveIntervalMilliseconds)
        {
            live.Queued = null;
            live.LastPoint = queued.Point;
            live.LastKeys = queued.Keys;
        }
        else if (!keepAlive || elapsed < KeepAliveMilliseconds)
        {
            return;
        }

        live.LastSent = Environment.TickCount64;
        live.Moves++;
        _agent.SendDrag(new DragMoveMessage(live.Id, live.LastPoint.X, live.LastPoint.Y, live.LastKeys), live.Id);
    }

    private DragDropEffects DropLiveDrag(LiveDrag live, FileDragEventArgs e)
    {
        if (live.State is AckState.Refused or AckState.TimedOut)
        {
            ClientLog.Info($"Drop {live.Id} not sent: {live.Problem?.ToEnglish()}");
            EndLiveDrag(sendLeave: true);
            ShowDropStatus(live.Problem ?? LocalText.Of(nameof(Strings.DropRefused)));
            return DragDropEffects.None;
        }

        var remote = ToRemote(e.ClientPoint);
        if (live.State == AckState.Pending)
        {
            // Sent now, the drop would go through drive redirection even when the dragAck
            // on its way offers a transfer.
            return HoldDrop(live, e, remote);
        }

        LogDrag(Invariant($"Drop after {live.Moves} move(s){(live.TransferId is null ? string.Empty : ", deferred for transfer " + live.TransferId)}"), live.Id, e, remote);
        EndLiveDrag(sendLeave: false);
        return SendDrop(live, remote, e.KeyState) ? DragDropEffects.Copy : DragDropEffects.None;
    }

    /// <summary>
    /// Sends the drop of <paramref name="live"/>, which no longer is the live drag, deferred
    /// when the agent offered a transfer, then starts the transfer or waits for the result.
    /// Returns false when the drop could not be sent.
    /// </summary>
    private bool SendDrop(LiveDrag live, Point remote, int keyState)
    {
        var drop = new DropMessage(live.Id, remote.X, remote.Y, keyState, Deferred: live.TransferId is null ? null : true);
        if (!_agent.SendDrag(drop, live.Id))
        {
            SendLeave(live);
            var reason = LocalText.Of(nameof(Strings.DropFailedNotSent));
            ShowDropStatus(reason);
            ProgressWindowOf(live.Id)?.ShowFailure(reason);
            return false;
        }

        _pendingDropId = live.Id;
        if (live.TransferId is { } transferId)
        {
            StartTransfer(live.Id, transferId, live.Items);
        }
        else
        {
            ShowDropStatus(LocalText.Of(nameof(Strings.Dragging), live.Items.Count));
            OpenProgressWindow(live.Id, live.Items, transfer: false);
        }

        return true;
    }

    /// <summary>
    /// The drop happened before the dragAck arrived: it waits for it, for at most
    /// <see cref="DropDecision.MaxWait"/>, with the window in its preparing stage. The live
    /// drag of the agent is kept alive by the moves meanwhile, now at the drop point.
    /// </summary>
    private DragDropEffects HoldDrop(LiveDrag live, FileDragEventArgs e, Point remote)
    {
        LogDrag(Invariant($"Drop after {live.Moves} move(s), held until the agent answers the drag"), live.Id, e, remote);
        _ackTimer.Stop();
        _leaveTimer.Stop();
        _live = null;
        _held = new HeldDrop(live, remote, e.KeyState, new DropDecision(live.Id, Environment.TickCount64));
        live.Queued = (remote, live.LastKeys);
        FlushMove(keepAlive: false);
        _moveTimer.Start();
        _holdTimer.Interval = (int)DropDecision.MaxWait.TotalMilliseconds;
        _holdTimer.Stop();
        _holdTimer.Start();
        ShowDropStatus(LocalText.Of(nameof(Strings.Dragging), live.Items.Count));
        OpenProgressWindow(live.Id, live.Items, transfer: true);
        RaiseStateChanged();
        return DragDropEffects.Copy;
    }

    private void OnHoldTick()
    {
        if (_held is not { } held)
        {
            _holdTimer.Stop();
            return;
        }

        var now = Environment.TickCount64;
        if (!held.Decision.Expire(now))
        {
            // A timer may tick a little early; it waits for the rest.
            _holdTimer.Interval = (int)Math.Max(1, held.Decision.RemainingMilliseconds(now));
            return;
        }

        ResolveHeldDrop(held);

        // As after a missing dragAck during a drag: an agent that exited or restarted in
        // the session goes unnoticed otherwise.
        _agent.Recheck("no dragAck for a held drop");
    }

    /// <summary>The dragAck of a held drop arrived: the decision is taken at once.</summary>
    private void OnHeldDragAck(HeldDrop held, DragAckMessage ack)
    {
        if (!held.Decision.OnAck(ack, Environment.TickCount64))
        {
            return;
        }

        var live = held.Live;
        if (ack.Accepted)
        {
            live.State = AckState.Accepted;
            live.TransferId = held.Decision.TransferId;
        }
        else
        {
            live.State = AckState.Refused;
            live.Problem = RefusalText(ack);
        }

        ResolveHeldDrop(held);
    }

    /// <summary>
    /// Ends the wait of <paramref name="held"/>: the drop goes out, deferred when a transfer
    /// was offered, or the refusal of the agent is shown.
    /// </summary>
    private void ResolveHeldDrop(HeldDrop held)
    {
        var live = held.Live;
        var decision = held.Decision;
        ReleaseHold();
        ClientLog.Info(Invariant($"Drop {live.Id} held {decision.WaitedMilliseconds(Environment.TickCount64)} ms for the agent answer: {decision.Describe()}."));
        if (!decision.SendsDrop)
        {
            SendLeave(live);
            var problem = live.Problem ?? LocalText.Of(nameof(Strings.DropRefused));
            ShowDropStatus(problem);
            ProgressWindowOf(live.Id)?.ShowFailure(problem);
            return;
        }

        SendDrop(live, held.Point, held.KeyState);
    }

    /// <summary>
    /// Gives up a held drop before the agent answered: cancelled by the user, the tab
    /// closing or the session ending. The agent is told when <paramref name="sendLeave"/> is set.
    /// </summary>
    private void GiveUpHeldDrop(HeldDrop held, LocalText reason, string why, bool sendLeave)
    {
        held.Decision.Abandon(Environment.TickCount64);
        ReleaseHold();
        ClientLog.Info(Invariant($"Drop {held.Live.Id} held {held.Decision.WaitedMilliseconds(Environment.TickCount64)} ms for the agent answer: {held.Decision.Describe()}, {why}."));
        if (sendLeave)
        {
            SendLeave(held.Live);
        }

        ShowDropStatus(reason);
        ProgressWindowOf(held.Live.Id)?.ShowFailure(reason);
    }

    /// <summary>Forgets the held drop and stops its timers.</summary>
    private void ReleaseHold()
    {
        _held = null;
        _holdTimer.Stop();
        if (_live is null)
        {
            _moveTimer.Stop();
        }

        RaiseStateChanged();
    }

    private static LocalText RefusalText(DragAckMessage ack) =>
        string.IsNullOrWhiteSpace(ack.Message)
            ? LocalText.Of(nameof(Strings.DropRefusedByAgent))
            : LocalText.Of(nameof(Strings.DropRefusedReason), ack.Message);

    private void OnDragAck(DragAckMessage ack)
    {
        if (_held is { } held && held.Live.Id == ack.Id)
        {
            OnHeldDragAck(held, ack);
            return;
        }

        var live = _live;
        if (live is not null && live.Id == ack.Id)
        {
            if (ack.Accepted)
            {
                if (live.State == AckState.Pending)
                {
                    live.State = AckState.Accepted;
                    _ackTimer.Stop();
                }

                if (ack.Transfer == true && ack.TransferId is { Length: > 0 } transferId)
                {
                    live.TransferId = transferId;
                }

                if (!string.IsNullOrWhiteSpace(ack.Message))
                {
                    ShowDropStatus(LocalText.Raw(ack.Message));
                }

                return;
            }

            live.State = AckState.Refused;
            live.Problem = RefusalText(ack);
            _ackTimer.Stop();
            ShowDropStatus(live.Problem);
            return;
        }

        if (ack.Id == _pendingDropId && !ack.Accepted && !string.IsNullOrWhiteSpace(ack.Message))
        {
            // The drop is on its way; its result follows and says the same.
            ShowDropStatus(LocalText.Of(nameof(Strings.DropRefusedReason), ack.Message));
        }
    }

    private void OnAckTimeout()
    {
        _ackTimer.Stop();
        var live = _live;
        if (live is not { State: AckState.Pending })
        {
            return;
        }

        ClientLog.Warn(Invariant($"Drag {live.Id}: no dragAck within {AckTimeoutMilliseconds} ms."));
        live.State = AckState.TimedOut;
        live.Problem = LocalText.Of(nameof(Strings.AgentNoAck));
        ShowDropStatus(live.Problem);

        // The agent may still start the drag late; tell it the drag is over.
        if (!live.LeaveSent)
        {
            live.LeaveSent = true;
            _agent.SendDrag(new DragLeaveMessage(live.Id), live.Id);
        }

        // An agent that exited or restarted in the session goes unnoticed otherwise.
        _agent.Recheck("no dragAck");
    }

    /// <summary>Ends the live drag, telling the agent when <paramref name="sendLeave"/> is set.</summary>
    private void EndLiveDrag(bool sendLeave)
    {
        var live = _live;
        _live = null;
        StopDragTimers();
        if (sendLeave && live is not null)
        {
            SendLeave(live);
        }
    }

    /// <summary>Tells the agent that the drag of <paramref name="live"/> is over, once.</summary>
    private void SendLeave(LiveDrag live)
    {
        if (live is { Sent: true, LeaveSent: false })
        {
            live.LeaveSent = true;
            _agent.SendDrag(new DragLeaveMessage(live.Id), live.Id);
        }
    }

    private void StopDragTimers()
    {
        _ackTimer.Stop();
        _leaveTimer.Stop();
        if (_held is null)
        {
            // A held drop keeps its moves going, even while a new drag is refused.
            _moveTimer.Stop();
        }
    }

    private List<DropItem>? MapItems(IReadOnlyList<string> files, out LocalText? problem)
    {
        var items = new List<DropItem>(files.Count);
        var counting = Stopwatch.StartNew();
        foreach (var path in files)
        {
            var rejection = ClientPathMapper.Classify(path, out _);
            if (rejection != PathRejection.None)
            {
                ClientLog.Info($"Drop refused: {ClientPathMapper.Describe(rejection, path)}");
                problem = LocalText.Of(nameof(Strings.DropRefusedReason), PathTexts.Describe(rejection, path));
                return null;
            }

            // The folders of one drag share the budget, so that many folders never hold the UI longer.
            var budget = FolderSizeBudget - counting.Elapsed;
            items.Add(MeasureItem(path, budget > TimeSpan.Zero ? budget : TimeSpan.Zero));
        }

        problem = null;
        return items;
    }

    /// <summary>
    /// The item with its size, which lets the agent decide whether to transfer the files
    /// through the channel. A folder is counted for at most <paramref name="budget"/>.
    /// </summary>
    private static DropItem MeasureItem(string path, TimeSpan budget)
    {
        try
        {
            if (Directory.Exists(path))
            {
                return new DropItem(path, true, TransferUpload.MeasureFolder(path, budget));
            }

            var file = new FileInfo(path);
            return new DropItem(path, false, file.Exists ? file.Length : null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            return new DropItem(path, Directory.Exists(path));
        }
    }

    private Point ToRemote(Point clientPoint) =>
        DropCoordinateMapper.ToRemote(clientPoint, _rdp.ViewSize, _display.RemoteDesktopSize);

    private void LogDrag(string what, string id, FileDragEventArgs e, Point remote)
    {
        var viewSize = _rdp.ViewSize;
        var desktopSize = _display.RemoteDesktopSize;
        ClientLog.Info(Invariant(
            $"{what} {id} on {Host}: {e.Files.Count} item(s) at view ({e.ClientPoint.X}, {e.ClientPoint.Y}) of {viewSize.Width}x{viewSize.Height}{(IsFullScreen ? " full screen" : string.Empty)}, remote ({remote.X}, {remote.Y}) of {desktopSize.Width}x{desktopSize.Height}, keys {KeyStates.Describe(e.KeyState)}, model {_agent.Model}: {string.Join("; ", e.Files)}"));
    }

    private void ShowBanner()
    {
        if (!_rdp.IsHandleCreated)
        {
            return;
        }

        var area = _rdp.FullScreenBounds ?? _rdp.RectangleToScreen(_rdp.ClientRectangle);
        _banner ??= new DropZoneBanner();
        _banner.ShowOver(area, FindForm(), topMost: IsFullScreen);
    }

    private void HideBanner()
    {
        if (_banner is { Visible: true })
        {
            _banner.Hide();
        }
    }

    private void OnDropResult(DropResultMessage result)
    {
        if (_upload is { } upload && upload.DragId == result.Id)
        {
            // The agent ended the drag before it had the files, so the transfer cannot go on;
            // the reason of the agent stays in the status bar and in the window.
            ClientLog.Info($"Transfer {upload.TransferId}: the drag ended on the agent with {result.Status}; the transfer stops.");
            EndTransfer(upload, "The drag ended on the agent before the transfer.");
            ProgressWindowOf(upload)?.ShowFailure(DescribeResult(result));
        }
        else
        {
            ShowDropResultInWindow(result.Id, DescribeResult(result), failed: result.Status != DropStatus.Ok);
        }

        if (_pendingDropId is not null && result.Id != _pendingDropId)
        {
            ClientLog.Info($"Result {result.Id} belongs to an earlier drop; the status bar keeps the latest one.");
            return;
        }

        _pendingDropId = null;
        ShowDropStatus(DescribeResult(result));
    }

    /// <remarks>The messages of the agent are already in the language of the client.</remarks>
    private static LocalText DescribeResult(DropResultMessage result) => (result.Status, result.Effect) switch
    {
        (DropStatus.Ok, DropEffect.Copy or DropEffect.Link) => LocalText.Of(
            nameof(Strings.DroppedOn),
            WindowName(result.TargetWindow),
            LocalText.Of(result.Effect == DropEffect.Copy ? nameof(Strings.EffectCopy) : nameof(Strings.EffectLink))),
        (DropStatus.Ok, _) => !string.IsNullOrWhiteSpace(result.Message)
            ? LocalText.Raw(result.Message)
            : LocalText.Of(nameof(Strings.NothingAccepted)),
        (DropStatus.Cancelled, _) => !string.IsNullOrWhiteSpace(result.Message)
            ? LocalText.Of(nameof(Strings.CancelledReason), result.Message)
            : LocalText.Of(nameof(Strings.DropCancelled)),
        _ => string.IsNullOrWhiteSpace(result.Message)
            ? LocalText.Of(nameof(Strings.DropFailedNoDetail))
            : LocalText.Of(nameof(Strings.DropFailedReason), result.Message),
    };

    private static LocalText WindowName(TargetWindowInfo? window) =>
        !string.IsNullOrWhiteSpace(window?.Title) ? LocalText.Raw(window.Title)
        : !string.IsNullOrWhiteSpace(window?.ProcessName) ? LocalText.Raw(window.ProcessName)
        : !string.IsNullOrWhiteSpace(window?.ClassName) ? LocalText.Raw(window.ClassName)
        : LocalText.Of(nameof(Strings.WindowUnderCursor));

    /// <summary>State of the drag in progress over the session.</summary>
    private sealed class LiveDrag(string id, IReadOnlyList<string> files, IReadOnlyList<DropItem> items)
    {
        public string Id { get; } = id;

        public IReadOnlyList<string> Files { get; } = files;

        public IReadOnlyList<DropItem> Items { get; } = items;

        public AckState State { get; set; } = AckState.Pending;

        /// <summary>Text shown while the drag is refused.</summary>
        public LocalText? Problem { get; set; }

        /// <summary>True once dragEnter went out.</summary>
        public bool Sent { get; set; }

        public bool LeaveSent { get; set; }

        public Point LastPoint { get; set; }

        public int LastKeys { get; set; }

        public long LastSent { get; set; }

        public (Point Point, int Keys)? Queued { get; set; }

        /// <summary>Set when the agent asked for the files through the channel.</summary>
        public string? TransferId { get; set; }

        public int Moves { get; set; }
    }

    /// <summary>A drop that waits for the dragAck of its drag, with what the drop message needs.</summary>
    private sealed class HeldDrop(LiveDrag live, Point point, int keyState, DropDecision decision)
    {
        /// <summary>The drag, which is no longer the live drag of the view.</summary>
        public LiveDrag Live { get; } = live;

        /// <summary>The drop point in the remote desktop.</summary>
        public Point Point { get; } = point;

        /// <summary>The keys at the drop.</summary>
        public int KeyState { get; } = keyState;

        public DropDecision Decision { get; } = decision;
    }
}
