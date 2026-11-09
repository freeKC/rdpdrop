using System.Globalization;
using UltimateRDP.Client.Channel;
using UltimateRDP.Client.Localization;
using UltimateRDP.Client.Logging;
using UltimateRDP.Client.Rdp;
using UltimateRDP.Client.Resources;
using UltimateRDP.Core.Paths;
using UltimateRDP.Core.Protocol;

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
/// </remarks>
internal sealed partial class SessionView
{
    private const int AckTimeoutMilliseconds = 1000;
    private const int MoveIntervalMilliseconds = 33;
    private const int KeepAliveMilliseconds = 1000;

    // Moving between the control, its child windows and the host window gives a leave
    // and an enter in a row; a leave only counts when no drag over follows it quickly.
    private const int LeaveDelayMilliseconds = 150;

    private readonly System.Windows.Forms.Timer _ackTimer = new() { Interval = AckTimeoutMilliseconds };
    private readonly System.Windows.Forms.Timer _moveTimer = new() { Interval = MoveIntervalMilliseconds };
    private readonly System.Windows.Forms.Timer _leaveTimer = new() { Interval = LeaveDelayMilliseconds };
    private DropZoneBanner? _banner;
    private LiveDrag? _live;
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
        _agent.DragAckReceived += (_, ack) => OnDragAck(ack);
    }

    /// <summary>Forgets the current drag, after a disconnection.</summary>
    private void ResetDrops()
    {
        _pendingDropId = null;
        _live = null;
        StopDragTimers();
        HideBanner();
    }

    private void DisposeDrops()
    {
        StopDragTimers();
        _ackTimer.Dispose();
        _moveTimer.Dispose();
        _leaveTimer.Dispose();
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
    /// not cancel the drag after five seconds without news.
    /// </summary>
    private void FlushMove(bool keepAlive)
    {
        var live = _live;
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
        LogDrag(Invariant($"Drop after {live.Moves} move(s)"), live.Id, e, remote);
        var drop = new DropMessage(live.Id, remote.X, remote.Y, e.KeyState);
        if (!_agent.SendDrag(drop, live.Id))
        {
            EndLiveDrag(sendLeave: true);
            ShowDropStatus(LocalText.Of(nameof(Strings.DropFailedNotSent)));
            return DragDropEffects.None;
        }

        _pendingDropId = live.Id;
        EndLiveDrag(sendLeave: false);
        ShowDropStatus(LocalText.Of(nameof(Strings.Dragging), live.Items.Count));
        return DragDropEffects.Copy;
    }

    private void OnDragAck(DragAckMessage ack)
    {
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

                if (!string.IsNullOrWhiteSpace(ack.Message))
                {
                    ShowDropStatus(LocalText.Raw(ack.Message));
                }

                return;
            }

            live.State = AckState.Refused;
            live.Problem = string.IsNullOrWhiteSpace(ack.Message)
                ? LocalText.Of(nameof(Strings.DropRefusedByAgent))
                : LocalText.Of(nameof(Strings.DropRefusedReason), ack.Message);
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
    }

    /// <summary>Ends the live drag, telling the agent when <paramref name="sendLeave"/> is set.</summary>
    private void EndLiveDrag(bool sendLeave)
    {
        var live = _live;
        _live = null;
        StopDragTimers();
        if (sendLeave && live is { Sent: true, LeaveSent: false })
        {
            live.LeaveSent = true;
            _agent.SendDrag(new DragLeaveMessage(live.Id), live.Id);
        }
    }

    private void StopDragTimers()
    {
        _ackTimer.Stop();
        _moveTimer.Stop();
        _leaveTimer.Stop();
    }

    private List<DropItem>? MapItems(IReadOnlyList<string> files, out LocalText? problem)
    {
        var items = new List<DropItem>(files.Count);
        foreach (var path in files)
        {
            var rejection = ClientPathMapper.Classify(path, out _);
            if (rejection != PathRejection.None)
            {
                ClientLog.Info($"Drop refused: {ClientPathMapper.Describe(rejection, path)}");
                problem = LocalText.Of(nameof(Strings.DropRefusedReason), PathTexts.Describe(rejection, path));
                return null;
            }

            items.Add(new DropItem(path, Directory.Exists(path)));
        }

        problem = null;
        return items;
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

        public int Moves { get; set; }
    }
}
