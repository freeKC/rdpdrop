using Timer = System.Threading.Timer;
using UltimateRDP.Agent.Localization;
using UltimateRDP.Agent.Logging;
using UltimateRDP.Agent.Resources;
using UltimateRDP.Agent.Settings;
using UltimateRDP.Core.Agent;
using UltimateRDP.Core.Protocol;
using UltimateRDP.Core.Transfer;
using Message = UltimateRDP.Core.Protocol.Message;

namespace UltimateRDP.Agent.Drops;

/// <summary>
/// Runs the live drag protocol of the agent: prepares a drag on <c>dragEnter</c> (checks
/// and optionally stages the items, then starts a <see cref="DragSession"/>), answers
/// with <c>dragAck</c>, forwards <c>dragMove</c>, <c>drop</c> and <c>dragLeave</c>, and
/// sends the <c>dropResult</c>. A drop that nothing under the cursor accepts is ignored.
/// One drag at a time: a new <c>dragEnter</c> cancels the current one. The texts sent to
/// the client are in its language; the log keeps them in English.
/// </summary>
/// <remarks>
/// When the <c>fastTransfer</c> setting asks for it, the <c>dragAck</c> asks the client to
/// transfer the files through the channel. The live drag then runs as usual over the
/// redirected paths; the deferred <c>drop</c> cancels it and remembers the point, a
/// <see cref="StagedTransfer"/> receives the files, and once they all arrived a short
/// drag drops the copies at that point and the usual <c>dropResult</c> follows.
/// </remarks>
internal sealed class NativeDropCoordinator : IDisposable
{
    /// <summary>Without a final answer by then, a provisional accepted dragAck is sent.</summary>
    private static readonly TimeSpan ProvisionalAckDelay = TimeSpan.FromMilliseconds(700);

    /// <summary>How long a new drag waits for the previous one to end.</summary>
    private static readonly TimeSpan PreviousDragWait = TimeSpan.FromSeconds(2);

    /// <summary>How long the agent waits, when it stops, for the drag in progress to end.</summary>
    private static readonly TimeSpan StopWait = TimeSpan.FromSeconds(2);

    private const int RememberedDrags = 16;

    private readonly FileLogger _logger;
    private readonly Func<Message, bool> _send;
    private readonly AgentSettings _settings;
    private readonly DriveRedirectionProbe _probe;
    private readonly StagingFolder _staging;
    private readonly ClientLanguage _language;
    private readonly Lock _gate = new();
    private readonly Queue<string> _endedOrder = new();
    private readonly Dictionary<string, LocalText> _ended = [];
    private readonly Queue<string> _endedTransfers = new();
    private NativeDrag? _current;

    public NativeDropCoordinator(FileLogger logger, Func<Message, bool> send, AgentSettings settings, DriveRedirectionProbe probe, StagingFolder staging, ClientLanguage language)
    {
        _logger = logger;
        _send = send;
        _settings = settings;
        _probe = probe;
        _staging = staging;
        _language = language;
    }

    /// <summary>Starts a drag in the session. Called on the channel thread; returns at once.</summary>
    public void OnDragEnter(DragEnterMessage enter)
    {
        _logger.Info($"Drag {enter.Id}: enter with {enter.Items.Count} item(s) at ({enter.X}, {enter.Y}), keys {KeyStates.Describe(enter.KeyState)}.");
        NativeDrag? previous;
        var drag = new NativeDrag(enter);
        lock (_gate)
        {
            previous = _current;
            if (previous?.Id == enter.Id)
            {
                // A repeated dragEnter must not start a second drag that nothing would end.
                _logger.Info($"Drag {enter.Id}: repeated enter ignored, the drag is already known.");
                return;
            }

            _current = drag;
        }

        if (previous is not null)
        {
            // Under the lock of the previous drag, so that its preparation either sees the
            // flag or has already published the session cancelled here.
            DragSession? previousSession;
            StagedTransfer? previousTransfer;
            lock (previous.Gate)
            {
                previous.Abandoned = true;
                previousSession = previous.Session;
                previousTransfer = previous.Transfer;
            }

            previousTransfer?.Cancel(LocalText.Of(nameof(Strings.TransferReplaced)));
            previousSession?.Cancel(LocalText.Of(nameof(Strings.CancelNewerDrag)));
        }

        drag.AckTimer = new Timer(_ => Guarded(drag, () => SendProvisionalAck(drag)), null, ProvisionalAckDelay, Timeout.InfiniteTimeSpan);
        var worker = new Thread(() => Prepare(drag, previous))
        {
            IsBackground = true,
            Name = "UltimateRDP prepare " + enter.Id,
        };
        worker.Start();
    }

    /// <summary>Forwards a move. Called on the channel thread.</summary>
    public void OnDragMove(DragMoveMessage move)
    {
        var drag = Find(move.Id);
        if (drag is null)
        {
            _logger.Debug($"Drag {move.Id}: move ignored, no such drag.");
            return;
        }

        if (move.KeyState != drag.KeyState)
        {
            _logger.Info($"Drag {move.Id}: keys {KeyStates.Describe(move.KeyState)} (logged only, not sent to the session).");
            drag.KeyState = move.KeyState;
        }

        DragSession? session;
        lock (drag.Gate)
        {
            drag.LastX = move.X;
            drag.LastY = move.Y;
            session = drag.Session;
        }

        session?.Move(move.X, move.Y);
    }

    /// <summary>Cancels the drag. Called on the channel thread.</summary>
    public void OnDragLeave(DragLeaveMessage leave)
    {
        var drag = Find(leave.Id);
        if (drag is null)
        {
            _logger.Info($"Drag {leave.Id}: leave ignored, no such drag.");
            return;
        }

        _logger.Info($"Drag {leave.Id}: the drag left the session on the client.");
        DragSession? session;
        lock (drag.Gate)
        {
            drag.Left = true;
            session = drag.Session;
        }

        session?.Cancel(LocalText.Of(nameof(Strings.CancelLeft)));
    }

    /// <summary>
    /// Ends the drag with the id of <paramref name="drop"/> by a drop inside the session.
    /// A drop that matches no drag gets an error result. Called on the channel thread.
    /// </summary>
    public void OnDrop(DropMessage drop)
    {
        var drag = Find(drop.Id);
        if (drag is null)
        {
            LocalText? reason;
            lock (_gate)
            {
                _ended.TryGetValue(drop.Id, out reason);
            }

            if (reason is null)
            {
                reason = LocalText.Of(nameof(Strings.NoDragForDrop));
                _logger.Warn($"Drag {drop.Id}: drop at ({drop.X}, {drop.Y}) ignored, no drag in progress with this id.");
            }
            else
            {
                _logger.Info($"Drag {drop.Id}: drop after the drag already ended ({reason}).");
            }

            _send(new DropResultMessage(drop.Id, DropStatus.Error, Message: ForClient(reason), Effect: DropEffect.None));
            return;
        }

        if (drop.Deferred == true && drag.TransferId is { } transferId && drag.TransferItems is { } transferItems)
        {
            DeferDrop(drag, drop, transferId, transferItems);
            return;
        }

        if (drop.Deferred == true)
        {
            _logger.Warn($"Drag {drop.Id}: the client deferred the drop, but no transfer was offered; dropping the redirected paths.");
        }
        else if (drag.TransferId is not null)
        {
            _logger.Info($"Drag {drop.Id}: the client dropped before it knew of the transfer; dropping the redirected paths.");
        }

        _logger.Info($"Drag {drop.Id}: drop at ({drop.X}, {drop.Y}), keys {KeyStates.Describe(drop.KeyState)}.");
        DragSession? session;
        lock (drag.Gate)
        {
            drag.Drop = drop;
            session = drag.Session;
        }

        session?.Drop(drop.X, drop.Y);
    }

    /// <summary>
    /// Handles a message of a transfer: transferBegin, fileBegin, fileChunk, fileEnd or
    /// transferCancel. Called on the channel thread.
    /// </summary>
    public void OnTransferMessage(Message message, string transferId)
    {
        StagedTransfer? transfer;
        bool known;
        lock (_gate)
        {
            transfer = _current?.Transfer is { } current && current.TransferId == transferId ? current : null;
            known = _endedTransfers.Contains(transferId);
            if (transfer is null && !known)
            {
                RememberTransfer(transferId);
            }
        }

        if (transfer is not null)
        {
            transfer.Handle(message);
            return;
        }

        // The chunks still on their way after the end of a transfer are dropped quietly;
        // a transfer that this agent never started gets one answer.
        if (!known)
        {
            _logger.Warn($"Transfer {transferId}: {message.Type} ignored, no such transfer.");
            _send(new TransferResultMessage(transferId, DropStatus.Error, ForClient(LocalText.Of(nameof(Strings.TransferUnknown)))));
        }
    }

    public void Dispose() => Stop(StopWait);

    /// <summary>
    /// Cancels the drag in progress, if any, and waits at most <paramref name="wait"/> for
    /// it to end, so that the simulated button is released before the agent exits.
    /// </summary>
    public void Stop(TimeSpan wait)
    {
        NativeDrag? current;
        lock (_gate)
        {
            current = _current;
            _current = null;
        }

        current?.AckTimer?.Dispose();
        current?.Transfer?.Cancel(LocalText.Of(nameof(Strings.CancelAgentStopping)));
        if (current?.Session is { } session)
        {
            // The drag thread releases the simulated button when it ends; waiting a little
            // keeps the agent from exiting with the button still down in the session.
            session.Cancel(LocalText.Of(nameof(Strings.CancelAgentStopping)));
            if (!session.WaitFinished(wait))
            {
                _logger.Warn($"Drag {current.Id}: the drag did not end before the agent stopped.");
            }
        }
    }

    private NativeDrag? Find(string id)
    {
        lock (_gate)
        {
            return _current is { } current && current.Id == id ? current : null;
        }
    }

    private void Prepare(NativeDrag drag, NativeDrag? previous)
    {
        try
        {
            if (previous?.Session is { } previousSession && !previousSession.WaitFinished(PreviousDragWait))
            {
                Refuse(drag, LocalText.Of(nameof(Strings.PreviousDropBusy)));
                return;
            }

            if (drag.Enter.Items.Count == 0)
            {
                Refuse(drag, LocalText.Of(nameof(Strings.DragHoldsNothing)));
                return;
            }

            var items = ItemMapper.Map(drag.Enter.Items, _probe, out var error);
            if (items is null)
            {
                Refuse(drag, error ?? LocalText.Of(nameof(Strings.ItemsCannotBeDropped)));
                return;
            }

            IReadOnlyList<string> paths = [.. items.Select(item => item.Path)];
            if (DecideTransfer(drag) is { } refusal)
            {
                Refuse(drag, refusal);
                return;
            }

            if (drag.TransferId is null && _settings.StageFiles)
            {
                drag.Staging = true;
                try
                {
                    paths = _staging.Stage(drag.Id, items);
                    drag.Staged = true;
                }
                catch (Exception ex) when (ItemMapper.IsFileError(ex))
                {
                    Refuse(drag, LocalText.Of(nameof(Strings.StagingFailed), ex.Message));
                    return;
                }
            }

            if (drag.Left || drag.Abandoned)
            {
                drag.AckTimer?.Dispose();
                if (drag.Staged)
                {
                    _staging.ScheduleDelete(drag.Id);
                }

                Forget(drag, LocalText.Of(nameof(Strings.DragCancelledBeforeStart)));
                return;
            }

            var session = new DragSession(_logger, drag.Id, paths, drag.Enter.X, drag.Enter.Y);
            session.Started += (_, started, reason) => OnStarted(drag, started, reason);
            session.Completed += (_, completion) => OnCompleted(drag, completion);
            // Messages that arrived during the preparation are replayed on the session; the
            // lock makes sure each later message sees the session instead.
            lock (drag.Gate)
            {
                drag.Session = session;
                if (drag.LastX is { } x && drag.LastY is { } y)
                {
                    session.Move(x, y);
                }

                if (drag.Transfer is not null)
                {
                    session.Cancel(LocalText.Of(nameof(Strings.CancelDeferred)));
                }
                else if (drag.Drop is { } drop)
                {
                    session.Drop(drop.X, drop.Y);
                }

                if (drag.Left || drag.Abandoned)
                {
                    session.Cancel(LocalText.Of(nameof(Strings.CancelLeft)));
                }
            }

            _logger.Info($"Drag {drag.Id}: starting the drag in the session with {paths.Count} {(drag.Staged ? "staged copies" : "redirected paths")}{(drag.TransferId is null ? string.Empty : ", transfer " + drag.TransferId + " offered")}.");
            session.Start();
        }
        catch (Exception ex)
        {
            _logger.Error($"Drag {drag.Id}: preparing the drag failed.", ex);
            Refuse(drag, LocalText.Of(nameof(Strings.UnexpectedError), ex.Message));
        }
    }

    /// <summary>
    /// Runs on a timer thread. Every dragAck of a drag is sent under its AckGate, so a
    /// provisional accept can never reach the client after the refusal that follows it.
    /// </summary>
    private void SendProvisionalAck(NativeDrag drag)
    {
        lock (drag.AckGate)
        {
            if (drag.Abandoned || !drag.TryMarkAck())
            {
                return;
            }

            var text = drag.Staging
                ? LocalText.Of(nameof(Strings.StagingProgress), drag.Enter.Items.Count)
                : LocalText.Of(nameof(Strings.StartingDrag));
            _logger.Info($"Drag {drag.Id}: provisional dragAck, {text}");
            _send(AcceptedAck(drag, ForClient(text)));
        }
    }

    private void OnStarted(NativeDrag drag, bool started, LocalText? reason)
    {
        drag.AckTimer?.Dispose();
        lock (drag.AckGate)
        {
            if (drag.Transfer is not null)
            {
                // The client already dropped; the transfer goes on whatever the live drag did.
                drag.TryMarkAck();
                return;
            }

            if (started)
            {
                // A provisional accept sent before the transfer was decided did not offer it.
                if (drag.TryMarkAck() || (drag.TransferId is not null && !drag.TransferOffered))
                {
                    _logger.Info($"Drag {drag.Id}: dragAck accepted{(drag.TransferId is null ? string.Empty : ", transfer offered")}.");
                    _send(AcceptedAck(drag, null));
                }

                return;
            }

            // A refusal is always sent, even after a provisional accept.
            drag.TryMarkAck();
            if (!drag.Left && !drag.Abandoned)
            {
                _logger.Info($"Drag {drag.Id}: dragAck refused, {reason}");
                _send(new DragAckMessage(drag.Id, false, ForClient(reason)));
            }
        }
    }

    private void OnCompleted(NativeDrag drag, DragCompletion completion)
    {
        drag.AckTimer?.Dispose();
        if (drag.Transfer is not null)
        {
            // The live drag of a deferred drop: the drop comes once the files arrived.
            _logger.Info($"Drag {drag.Id}: the live drag ended ({completion.Outcome}); the drop waits for transfer {drag.TransferId}.");
            return;
        }

        if (drag.Staged)
        {
            _staging.ScheduleDelete(drag.Id);
        }

        SendDropResult(drag, completion);
    }

    /// <summary>Sends the dropResult, or the refusal of a drag the watchdog ended, and forgets the drag.</summary>
    private void SendDropResult(NativeDrag drag, DragCompletion completion)
    {
        var drop = drag.Drop;
        var target = completion.Target;
        switch (completion.Outcome)
        {
            case DragOutcome.Dropped when completion.Effect.HasFlag(DragDropEffects.Copy) || completion.Effect.HasFlag(DragDropEffects.Link):
                var effect = completion.Effect.HasFlag(DragDropEffects.Copy) ? DropEffect.Copy : DropEffect.Link;
                object where = target?.Title ?? target?.ClassName ?? (object)LocalText.Of(nameof(Strings.WindowUnderCursor));
                Forget(drag, LocalText.Of(nameof(Strings.DragEndedWithDrop)));
                _send(new DropResultMessage(drag.Id, DropStatus.Ok, Message: ForClient(LocalText.Of(nameof(Strings.DroppedOn), where)), Effect: effect, TargetWindow: target));
                break;

            case DragOutcome.Dropped:
                Forget(drag, LocalText.Of(nameof(Strings.DragEndedWithDrop)));
                _logger.Info($"Drag {drag.Id}: nothing under the cursor accepted the files; the drop is ignored.");
                _send(new DropResultMessage(drag.Id, DropStatus.Ok, Message: ForClient(LocalText.Of(nameof(Strings.NothingAccepted))), Effect: DropEffect.None, TargetWindow: target));
                break;

            case DragOutcome.Cancelled when drop is not null:
                Forget(drag, completion.Message ?? LocalText.Of(nameof(Strings.DragCancelled)));
                _send(new DropResultMessage(drag.Id, DropStatus.Cancelled, Message: ForClient(completion.Message), Effect: DropEffect.None));
                break;

            case DragOutcome.Cancelled:
                // The client is still dragging when the watchdog fires; tell it the drag is over.
                Forget(drag, completion.Message ?? LocalText.Of(nameof(Strings.DragCancelled)));
                lock (drag.AckGate)
                {
                    if (!drag.Left && !drag.Abandoned && drag.HasAck)
                    {
                        _send(new DragAckMessage(drag.Id, false, ForClient(completion.Message)));
                    }
                }

                break;

            default:
                Forget(drag, completion.Message ?? LocalText.Of(nameof(Strings.DragFailed)));
                if (drop is not null)
                {
                    _send(new DropResultMessage(drag.Id, DropStatus.Error, Message: ForClient(completion.Message), Effect: DropEffect.None));
                }

                break;
        }
    }

    private void Refuse(NativeDrag drag, LocalText reason)
    {
        drag.AckTimer?.Dispose();
        _logger.Info($"Drag {drag.Id}: refused, {reason}");
        if (drag.Staged)
        {
            _staging.ScheduleDelete(drag.Id);
        }

        Forget(drag, reason);
        if (drag.Abandoned)
        {
            drag.TryMarkAck();
            return;
        }

        lock (drag.AckGate)
        {
            drag.TryMarkAck();
            _send(new DragAckMessage(drag.Id, false, ForClient(reason)));
        }

        if (drag.Drop is not null)
        {
            _send(new DropResultMessage(drag.Id, DropStatus.Error, Message: ForClient(reason), Effect: DropEffect.None));
        }
    }

    /// <summary>
    /// Runs a timer callback. An error there would end the process; it is logged instead.
    /// </summary>
    private void Guarded(NativeDrag drag, Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            _logger.Error($"Drag {drag.Id}: unexpected error in a timer.", ex);
        }
    }

    /// <summary>A text for the client, in its language; null stays null.</summary>
    private string? ForClient(LocalText? text) => text?.In(_language.Culture);

    /// <summary>Clears the drag if it is the current one and remembers why it ended, for a late drop.</summary>
    private void Forget(NativeDrag drag, LocalText reason)
    {
        lock (_gate)
        {
            if (_current == drag)
            {
                _current = null;
            }

            if (_ended.TryAdd(drag.Id, reason))
            {
                _endedOrder.Enqueue(drag.Id);
                while (_endedOrder.Count > RememberedDrags)
                {
                    _ended.Remove(_endedOrder.Dequeue());
                }
            }
        }
    }

    /// <summary>
    /// Decides, from the fastTransfer setting and the sizes the client gave, whether the
    /// files of the drag come through the channel. Returns the reason to refuse the drag
    /// when the staging volume lacks the space, null otherwise.
    /// </summary>
    private LocalText? DecideTransfer(NativeDrag drag)
    {
        var items = drag.Enter.Items;
        if (!FastTransferPolicy.ShouldTransfer(_settings.FastTransfer, _settings.FastTransferMinBytes, items))
        {
            return null;
        }

        List<TransferItem> transferItems = [.. items.Select(item => new TransferItem(TransferPaths.ItemName(item.ClientPath), item.IsDirectory))];
        if (transferItems.Any(item => !TransferPaths.IsValidName(item.Name))
            || transferItems.DistinctBy(item => item.Name, StringComparer.OrdinalIgnoreCase).Count() != transferItems.Count)
        {
            _logger.Info($"Drag {drag.Id}: two items share a name, so the files are read through drive redirection.");
            return null;
        }

        var total = FastTransferPolicy.TotalSize(items);
        var required = FastTransferPolicy.RequiredFreeSpace(total);
        var free = _staging.FreeSpace();
        if (free is { } available && available < required)
        {
            _logger.Warn($"Drag {drag.Id}: {total} bytes to transfer, {required} bytes of free space needed and {available} free in the staging folder.");
            return LocalText.Of(nameof(Strings.TransferNoSpace), StagedTransfer.Megabytes(required), StagedTransfer.Megabytes(available));
        }

        // The items first: whoever sees the id sees the items too.
        drag.TransferItems = transferItems;
        drag.TransferId = Guid.NewGuid().ToString("N");
        _logger.Info($"Drag {drag.Id}: {total} bytes in {items.Count} item(s), fast transfer {FastTransferPolicy.ToSetting(_settings.FastTransfer)} from {_settings.FastTransferMinBytes} bytes: the files come through the channel as transfer {drag.TransferId}.");
        return null;
    }

    /// <summary>The accepted dragAck, which offers the transfer when one was decided.</summary>
    private static DragAckMessage AcceptedAck(NativeDrag drag, string? message)
    {
        if (drag.TransferId is { } transferId)
        {
            drag.TransferOffered = true;
            return new DragAckMessage(drag.Id, true, message, Transfer: true, TransferId: transferId);
        }

        return new DragAckMessage(drag.Id, true, message);
    }

    /// <summary>
    /// The client dropped and sends the files: the live drag ends without a drop, the
    /// point is kept, and a <see cref="StagedTransfer"/> waits for the files.
    /// </summary>
    private void DeferDrop(NativeDrag drag, DropMessage drop, string transferId, IReadOnlyList<TransferItem> items)
    {
        StagedTransfer transfer;
        try
        {
            transfer = new StagedTransfer(_logger, _staging, drag.Id, transferId, items, _send, _language);
        }
        catch (Exception ex) when (ItemMapper.IsFileError(ex))
        {
            _logger.Warn($"Drag {drag.Id}: transfer {transferId} cannot start. {ex.Message}");
            var reason = LocalText.Of(nameof(Strings.UnexpectedError), ex.Message);
            lock (_gate)
            {
                RememberTransfer(transferId);
            }

            drag.Session?.Cancel(reason);
            Forget(drag, reason);
            _send(new TransferResultMessage(transferId, DropStatus.Error, ForClient(reason)));
            return;
        }

        transfer.Finished += (_, succeeded) => OnTransferFinished(drag, transfer, succeeded);
        _logger.Info($"Drag {drag.Id}: deferred drop at ({drop.X}, {drop.Y}); the live drag ends and the drop waits for transfer {transferId}.");
        DragSession? session;
        lock (drag.Gate)
        {
            drag.Drop = drop;
            drag.Transfer = transfer;
            session = drag.Session;
        }

        session?.Cancel(LocalText.Of(nameof(Strings.CancelDeferred)));
    }

    /// <summary>
    /// Runs once the transfer is over, on the thread that ended it. Every file arrived: the
    /// copies are dropped on a thread of their own. Otherwise the drag is over; the client
    /// already has the transferResult.
    /// </summary>
    private void OnTransferFinished(NativeDrag drag, StagedTransfer transfer, bool succeeded)
    {
        lock (_gate)
        {
            RememberTransfer(transfer.TransferId);
        }

        if (!succeeded)
        {
            Forget(drag, LocalText.Of(nameof(Strings.DragCancelled)));
            return;
        }

        var worker = new Thread(() => DropCopies(drag, transfer))
        {
            IsBackground = true,
            Name = "UltimateRDP final drop " + drag.Id,
        };
        worker.Start();
    }

    /// <summary>
    /// Drops the copies at the point of the deferred drop: a short drag pressed and
    /// released there, as the live drag does for its final point. Its result is the usual
    /// dropResult.
    /// </summary>
    private void DropCopies(NativeDrag drag, StagedTransfer transfer)
    {
        var drop = drag.Drop!;
        try
        {
            if (drag.Session is { } live && !live.WaitFinished(PreviousDragWait))
            {
                _logger.Warn($"Drag {drag.Id}: the live drag did not end; the copies of transfer {transfer.TransferId} are not dropped.");
                FailDropOfCopies(drag, transfer, LocalText.Of(nameof(Strings.PreviousDropBusy)));
                return;
            }

            var session = new DragSession(_logger, drag.Id, transfer.StagedPaths, drop.X, drop.Y);
            session.Completed += (_, completion) => OnCopiesDropped(drag, transfer, completion);
            session.Drop(drop.X, drop.Y);
            lock (drag.Gate)
            {
                drag.Session = session;
            }

            if (drag.Abandoned)
            {
                session.Cancel(LocalText.Of(nameof(Strings.CancelNewerDrag)));
            }

            _logger.Info($"Drag {drag.Id}: dropping the {transfer.StagedPaths.Count} copied item(s) of transfer {transfer.TransferId} at ({drop.X}, {drop.Y}).");
            session.Start();
        }
        catch (Exception ex)
        {
            _logger.Error($"Drag {drag.Id}: dropping the copies failed.", ex);
            FailDropOfCopies(drag, transfer, LocalText.Of(nameof(Strings.UnexpectedError), ex.Message));
        }
    }

    private void OnCopiesDropped(NativeDrag drag, StagedTransfer transfer, DragCompletion completion)
    {
        _staging.ScheduleDelete(transfer.TransferId);
        SendDropResult(drag, completion);
    }

    private void FailDropOfCopies(NativeDrag drag, StagedTransfer transfer, LocalText reason)
    {
        _staging.ScheduleDelete(transfer.TransferId);
        Forget(drag, reason);
        _send(new DropResultMessage(drag.Id, DropStatus.Error, Message: ForClient(reason), Effect: DropEffect.None));
    }

    /// <summary>Remembers a transfer that ended, so that its late messages are dropped quietly. Under the lock.</summary>
    private void RememberTransfer(string transferId)
    {
        if (_endedTransfers.Contains(transferId))
        {
            return;
        }

        _endedTransfers.Enqueue(transferId);
        while (_endedTransfers.Count > RememberedDrags)
        {
            _endedTransfers.Dequeue();
        }
    }

    /// <summary>State of one drag, shared by the channel, preparation and drag threads.</summary>
    private sealed class NativeDrag(DragEnterMessage enter)
    {
        private int _ackSent;

        public DragEnterMessage Enter { get; } = enter;

        public string Id => Enter.Id;

        /// <summary>Guards <see cref="Session"/> against the messages that arrive while it is created.</summary>
        public Lock Gate { get; } = new();

        /// <summary>Keeps the dragAck messages of the drag in the order they were decided.</summary>
        public Lock AckGate { get; } = new();

        public volatile DragSession? Session;

        public volatile DropMessage? Drop;

        public volatile bool Left;

        public volatile bool Abandoned;

        public volatile bool Staging;

        public volatile bool Staged;

        /// <summary>Set when the files come through the channel; offered in the accepted dragAck.</summary>
        public volatile string? TransferId;

        /// <summary>The dropped items as the transfer names them, set before <see cref="TransferId"/>.</summary>
        public volatile IReadOnlyList<TransferItem>? TransferItems;

        /// <summary>True once an accepted dragAck offered the transfer.</summary>
        public volatile bool TransferOffered;

        /// <summary>The transfer that receives the files, from the deferred drop on.</summary>
        public volatile StagedTransfer? Transfer;

        public int KeyState = enter.KeyState;

        public int? LastX;

        public int? LastY;

        public Timer? AckTimer;

        public bool HasAck => Volatile.Read(ref _ackSent) != 0;

        /// <summary>Returns true for the first caller only.</summary>
        public bool TryMarkAck() => Interlocked.Exchange(ref _ackSent, 1) == 0;
    }
}
