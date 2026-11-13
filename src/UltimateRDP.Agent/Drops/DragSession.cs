using UltimateRDP.Agent.Interop;
using UltimateRDP.Agent.Localization;
using UltimateRDP.Agent.Logging;
using UltimateRDP.Agent.Resources;
using UltimateRDP.Core.Agent;
using UltimateRDP.Core.Display;
using UltimateRDP.Core.Protocol;

namespace UltimateRDP.Agent.Drops;

/// <summary>How a drag inside the session ended.</summary>
internal enum DragOutcome
{
    /// <summary>The drag ended with a drop; the effect says whether the target took it.</summary>
    Dropped,

    /// <summary>The drag was cancelled: dragLeave, the watchdog, Escape or a newer drag.</summary>
    Cancelled,

    /// <summary>The drag could not start or failed.</summary>
    Failed,
}

/// <summary>Result of a drag inside the session.</summary>
internal sealed record DragCompletion(
    string Id,
    DragOutcome Outcome,
    DragDropEffects Effect,
    TargetWindowInfo? Target,
    LocalText? Message);

/// <summary>
/// Replays one drag of the client inside the session, as a real OLE drag whose source is
/// the agent. A dedicated STA thread initialises OLE, shows the <see cref="ProxyWindow"/>,
/// presses the simulated left button on it and, from its mouse down handler, calls
/// DoDragDrop. Moves from the client move the cursor, so OLE sends DragOver to the window
/// under it; the drop moves to the final point and releases the button; a cancel releases
/// it and posts Escape to the proxy window. Simulated input only happens between the
/// start and the end of the session, and only with the left button. In a RemoteApp session
/// the proxy is a small square under the press point (<see cref="ProxyBounds"/>), since the
/// server shows every visible window of the session on the client.
/// </summary>
internal sealed class DragSession
{
    /// <summary>A drag with no message from the client for this long is cancelled.</summary>
    public static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(5);

    private const int TickMilliseconds = 25;
    private const int PressTimeoutMilliseconds = 500;
    private const int ReleaseWaitMilliseconds = 300;
    private const int WakeAfterReleaseMilliseconds = 1000;
    private const int DropTimeoutMilliseconds = 10_000;
    private const int WmMouseMove = 0x0200;

    private readonly FileLogger _logger;
    private readonly IReadOnlyList<string> _paths;
    private readonly DropSource _source = new();
    private readonly ManualResetEventSlim _finished = new(false);
    private readonly Lock _gate = new();
    private readonly bool _remoteApp;

    private Thread? _thread;
    private ProxyWindow? _proxy;
    private IntPtr _proxyHandle;
    private ApplicationContext? _context;
    private System.Windows.Forms.Timer? _timer;

    private volatile Phase _phase = Phase.Created;
    private User32.Point _startPoint;
    private User32.Point _latestPoint;
    private User32.Point _finalPoint;
    private long _lastMessageTicks;
    private long _phaseTicks;
    private long _dropTicks;
    private int _feedbackAtDrop;
    private bool _pressed;
    private bool _inDoDragDrop;
    private bool _startedRaised;
    private bool _woken;
    private int _moves;
    private TargetWindowInfo? _target;

    public DragSession(FileLogger logger, string id, IReadOnlyList<string> paths, int x, int y, bool remoteApp = false)
    {
        _logger = logger;
        _remoteApp = remoteApp;
        Id = id;
        _paths = paths;
        _startPoint = InputInjector.ToScreen(x, y);
        _latestPoint = _startPoint;
        Touch();
    }

    private enum Phase
    {
        Created,
        Arming,
        Pressed,
        Dragging,
        Done,
    }

    public string Id { get; }

    /// <summary>
    /// Raised once on the drag thread when the drag loop runs (true) or when the drag
    /// could not start (false, with the reason).
    /// </summary>
    public event Action<DragSession, bool, LocalText?>? Started;

    /// <summary>Raised once on the drag thread when the session is over.</summary>
    public event Action<DragSession, DragCompletion>? Completed;

    /// <summary>Starts the drag thread.</summary>
    public void Start()
    {
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "UltimateRDP drag " + Id,
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    /// <summary>Waits for the end of the session. Returns false on timeout.</summary>
    public bool WaitFinished(TimeSpan timeout) => _finished.Wait(timeout);

    /// <summary>Handles a dragMove from the client. Called on the channel thread.</summary>
    public void Move(int x, int y)
    {
        Touch();
        var point = InputInjector.ToScreen(x, y);
        lock (_gate)
        {
            _latestPoint = point;
            _moves++;
        }

        // Before the drag loop runs, the cursor stays on the press point.
        if (_phase == Phase.Dragging && !_source.DropRequested && !_source.CancelRequested)
        {
            InputInjector.MoveTo(point);
        }
    }

    /// <summary>
    /// Handles the drop from the client: moves the cursor to the final point; the drag
    /// thread releases the button once the target answered there. Called on the channel thread.
    /// </summary>
    public void Drop(int x, int y)
    {
        Touch();
        var point = InputInjector.ToScreen(x, y);
        lock (_gate)
        {
            _latestPoint = point;
            _finalPoint = point;
            _feedbackAtDrop = _source.FeedbackCount;
            _dropTicks = Environment.TickCount64;
        }

        _source.RequestDrop();
        _logger.Info($"Drag {Id}: drop requested at ({point.X}, {point.Y}) after {_moves} move(s).");
        if (_phase == Phase.Dragging)
        {
            InputInjector.MoveTo(point);
        }
    }

    /// <summary>Ends the drag without a drop. Callable from any thread.</summary>
    public void Cancel(LocalText reason)
    {
        if (!_source.RequestCancel(reason))
        {
            return;
        }

        _logger.Info($"Drag {Id}: cancel requested, {reason}.");
        var proxy = _proxy;
        if (proxy is not null && _phase != Phase.Done)
        {
            try
            {
                proxy.BeginInvoke(CancelOnDragThread);
            }
            catch (InvalidOperationException)
            {
                // The window is already gone: the session is ending.
            }
        }
    }

    private void Touch() => Interlocked.Exchange(ref _lastMessageTicks, Environment.TickCount64);

    /// <summary>The virtual screen, or in a RemoteApp session a small square under the press point.</summary>
    private Rectangle ProxyArea()
    {
        var screen = SystemInformation.VirtualScreen;
        var area = ProxyBounds.For(_remoteApp, _startPoint.X, _startPoint.Y, new ScreenRect(screen.X, screen.Y, screen.Width, screen.Height));
        return new Rectangle(area.X, area.Y, area.Width, area.Height);
    }

    private void Run()
    {
        // Application.ThreadException is kept per thread. Without a handler here, an error
        // in a handler of the proxy or of the timer would open the WinForms error dialog
        // on this thread and leave the drag hanging with the button down.
        Application.ThreadException += OnThreadException;
        try
        {
            // DoDragDrop needs OleInitialize; an STA apartment alone is not enough.
            Application.OleRequired();
            using var proxy = new ProxyWindow();
            proxy.Place(ProxyArea());
            proxy.MouseDown += OnProxyMouseDown;
            proxy.QueryContinueDrag += (_, e) => OnQueryContinueDrag(e);
            proxy.GiveFeedback += (_, e) =>
            {
                e.UseDefaultCursors = true;
                _source.OnFeedback();
            };
            _proxyHandle = proxy.Handle;
            _proxy = proxy;

            using var timer = new System.Windows.Forms.Timer { Interval = TickMilliseconds };
            timer.Tick += (_, _) => OnTick();
            _timer = timer;
            _context = new ApplicationContext();
            proxy.BeginInvoke(Arm);
            Application.Run(_context);
        }
        catch (Exception ex)
        {
            _logger.Error($"Drag {Id}: the drag thread failed.", ex);
            ReleaseIfStillDown();
            Complete(DragOutcome.Failed, DragDropEffects.None, LocalText.Of(nameof(Strings.DragThreadFailed), ex.Message));
        }
        finally
        {
            Application.ThreadException -= OnThreadException;
            ReleaseIfStillDown();
            _proxy = null;
            _finished.Set();
        }
    }

    /// <summary>
    /// An error in a handler of the drag thread. Inside DoDragDrop the drag is cancelled and
    /// the mouse down handler completes the session once DoDragDrop returns; anywhere else
    /// the session completes at once.
    /// </summary>
    private void OnThreadException(object? sender, ThreadExceptionEventArgs e)
    {
        _logger.Error($"Drag {Id}: unexpected error on the drag thread.", e.Exception);
        _source.RequestCancel(LocalText.Of(nameof(Strings.CancelUnexpected)));
        ReleaseIfStillDown();
        if (_inDoDragDrop)
        {
            User32.PostMessage(_proxyHandle, User32.WmKeyDown, User32.VkEscape, IntPtr.Zero);
            return;
        }

        if (_phase == Phase.Done)
        {
            // The error came after the end, for example while the window closed: the
            // thread must still leave its message loop.
            _context?.ExitThread();
            return;
        }

        Complete(DragOutcome.Failed, DragDropEffects.None, LocalText.Of(nameof(Strings.DragThreadFailed), e.Exception.Message));
    }

    private void Arm()
    {
        if (_source.CancelRequested)
        {
            Complete(DragOutcome.Cancelled, DragDropEffects.None, null);
            return;
        }

        var bounds = _proxy!.Bounds;
        var screen = SystemInformation.VirtualScreen;
        _logger.Info(_remoteApp
            ? $"Drag {Id}: RemoteApp session, proxy window of {bounds.Width}x{bounds.Height} at {bounds.X},{bounds.Y} in the virtual screen {screen.X},{screen.Y} {screen.Width}x{screen.Height}; press point ({_startPoint.X}, {_startPoint.Y})."
            : $"Drag {Id}: proxy window over the virtual screen {bounds.X},{bounds.Y} {bounds.Width}x{bounds.Height}; press point ({_startPoint.X}, {_startPoint.Y}).");
        _proxy!.Show();
        _proxy.Update();
        SetPhase(Phase.Arming);
        _timer!.Start();
    }

    private void OnTick()
    {
        var now = Environment.TickCount64;
        var inPhase = now - Interlocked.Read(ref _phaseTicks);
        switch (_phase)
        {
            case Phase.Arming:
                TryPress(inPhase);
                break;

            case Phase.Pressed when inPhase > PressTimeoutMilliseconds:
                InputInjector.Release();
                _pressed = false;
                Fail(LocalText.Of(nameof(Strings.PressNotDelivered)));
                break;

            case Phase.Dragging:
                WatchDrag(now);
                break;
        }
    }

    /// <summary>
    /// Presses only once the proxy is really the window under the press point, so that
    /// the press can never reach the application under the cursor.
    /// </summary>
    private void TryPress(long inPhase)
    {
        if (_source.CancelRequested)
        {
            Complete(DragOutcome.Cancelled, DragDropEffects.None, null);
            return;
        }

        var hit = User32.WindowFromPoint(_startPoint);
        if (hit == _proxyHandle && User32.IsWindowVisible(_proxyHandle))
        {
            if (!InputInjector.PressAt(_startPoint))
            {
                Fail(LocalText.Of(nameof(Strings.InputRefused)));
                return;
            }

            _pressed = true;
            SetPhase(Phase.Pressed);
            _logger.Info($"Drag {Id}: WindowFromPoint gave the proxy window after {inPhase} ms; button pressed on it.");
            return;
        }

        if (inPhase > PressTimeoutMilliseconds)
        {
            var other = WindowProbe.Describe(_startPoint);
            Fail(other?.ClassName is { Length: > 0 } className
                ? LocalText.Of(nameof(Strings.WindowAbovePoint), className)
                : LocalText.Of(nameof(Strings.UnknownWindowAbovePoint)));
        }
    }

    private void WatchDrag(long now)
    {
        // Once the client dropped, no more messages come: the watchdog stops, because the
        // target may keep its Drop call open for a long time (a dialog). Only a drop that
        // never reaches the target is cancelled.
        if (!_source.CancelRequested && !_source.DropRequested && now - Interlocked.Read(ref _lastMessageTicks) > IdleTimeout.TotalMilliseconds)
        {
            Cancel(LocalText.Of(nameof(Strings.CancelIdle)));
            return;
        }

        if (_source.DropRequested && _target is null && !_source.CancelRequested && now - ReadDropTicks() > DropTimeoutMilliseconds)
        {
            Cancel(LocalText.Of(nameof(Strings.CancelDropTimeout)));
            return;
        }

        if (_source.DropRequested && !_source.Released && !_source.CancelRequested)
        {
            int feedbackAtDrop;
            long dropTicks;
            User32.Point final;
            lock (_gate)
            {
                feedbackAtDrop = _feedbackAtDrop;
                dropTicks = _dropTicks;
                final = _finalPoint;
            }

            // Release once the target answered a DragOver at the final point, or after a
            // short wait: a target that never saw the drag there would refuse the drop.
            if (_source.FeedbackCount > feedbackAtDrop || now - dropTicks > ReleaseWaitMilliseconds)
            {
                _source.MarkReleased();
                InputInjector.ReleaseAt(final);
                _pressed = false;
                _logger.Info($"Drag {Id}: button released at ({final.X}, {final.Y}).");
            }

            return;
        }

        if (_source.Released && !_woken && now - ReadDropTicks() > WakeAfterReleaseMilliseconds)
        {
            // The release should have woken the drag loop; a posted mouse message makes it
            // ask QueryContinueDrag again in case the button event was lost.
            _woken = true;
            User32.PostMessage(_proxyHandle, WmMouseMove, IntPtr.Zero, IntPtr.Zero);
        }
    }

    private long ReadDropTicks()
    {
        lock (_gate)
        {
            return _dropTicks;
        }
    }

    private void OnProxyMouseDown(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left || _phase != Phase.Pressed)
        {
            return;
        }

        if (_source.CancelRequested)
        {
            InputInjector.Release();
            _pressed = false;
            Complete(DragOutcome.Cancelled, DragDropEffects.None, null);
            return;
        }

        // A hidden window is skipped by WindowFromPoint, so OLE never takes the proxy for
        // the target.
        _proxy!.Hide();
        SetPhase(Phase.Dragging);
        Touch();
        _logger.Info($"Drag {Id}: drag loop started with {_paths.Count} item(s).");
        RaiseStarted(true, null);

        User32.Point latest;
        lock (_gate)
        {
            latest = _latestPoint;

            // A drop that came before the drag loop waits, like any other drop, for the
            // target to answer at the final point before the button is released.
            if (_source.DropRequested)
            {
                _feedbackAtDrop = _source.FeedbackCount;
                _dropTicks = Environment.TickCount64;
            }
        }

        if (latest.X != _startPoint.X || latest.Y != _startPoint.Y)
        {
            InputInjector.MoveTo(latest);
        }

        var effect = DragDropEffects.None;
        LocalText? error = null;
        try
        {
            // Copy and link only: a move accepted by the remote Explorer would delete the
            // file on the local disk through the redirected drive.
            _inDoDragDrop = true;
            effect = _proxy.DoDragDrop(DragDataObject.Create(_paths), DragDropEffects.Copy | DragDropEffects.Link);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.ExternalException or InvalidOperationException)
        {
            error = LocalText.Of(nameof(Strings.DoDragDropFailed), ex.Message);
            _logger.Warn($"Drag {Id}: {error}");
        }
        finally
        {
            _inDoDragDrop = false;
        }

        ReleaseIfStillDown();
        if (error is not null)
        {
            Complete(DragOutcome.Failed, DragDropEffects.None, error);
        }
        else if (_source.CancelRequested || !_source.DropRequested)
        {
            Complete(DragOutcome.Cancelled, DragDropEffects.None, null);
        }
        else
        {
            Complete(DragOutcome.Dropped, effect, null);
        }
    }

    private void OnQueryContinueDrag(QueryContinueDragEventArgs e)
    {
        var action = _source.Decide(e.EscapePressed);
        if (action == DragAction.Drop && _target is null)
        {
            _target = WindowProbe.Describe(InputInjector.CursorOr(_finalPoint));
            _logger.Info($"Drag {Id}: dropping on {_target?.ClassName ?? "no window"} \"{_target?.Title}\" ({_target?.ProcessName}){(_target?.Rect is { } rect ? ", window at " + rect : string.Empty)}.");
        }

        e.Action = action;
    }

    private void CancelOnDragThread()
    {
        switch (_phase)
        {
            case Phase.Created:
            case Phase.Arming:
                Complete(DragOutcome.Cancelled, DragDropEffects.None, null);
                break;

            case Phase.Pressed:
                // The press is on the visible proxy, so the release goes there too.
                InputInjector.Release();
                _pressed = false;
                Complete(DragOutcome.Cancelled, DragDropEffects.None, null);
                break;

            case Phase.Dragging:
                // The release wakes the drag loop through its capture; Escape is posted to
                // the proxy window, never sent as input, so no key reaches the application
                // in the foreground.
                ReleaseIfStillDown();
                User32.PostMessage(_proxyHandle, User32.WmKeyDown, User32.VkEscape, IntPtr.Zero);
                break;
        }
    }

    private void ReleaseIfStillDown()
    {
        if (_pressed && !_source.Released)
        {
            _source.MarkReleased();
            InputInjector.Release();
        }

        _pressed = false;
    }

    private void Fail(LocalText reason)
    {
        _logger.Warn($"Drag {Id}: {reason}");
        Complete(DragOutcome.Failed, DragDropEffects.None, reason);
    }

    /// <summary>Raises <see cref="Started"/> once, whatever the path that ends the start.</summary>
    private void RaiseStarted(bool started, LocalText? reason)
    {
        if (_startedRaised)
        {
            return;
        }

        _startedRaised = true;
        try
        {
            Started?.Invoke(this, started, reason);
        }
        catch (Exception ex)
        {
            _logger.Error($"Drag {Id}: the start handler failed.", ex);
        }
    }

    private void Complete(DragOutcome outcome, DragDropEffects effect, LocalText? message)
    {
        if (_phase == Phase.Done)
        {
            return;
        }

        var wasDragging = _phase == Phase.Dragging;
        SetPhase(Phase.Done);
        _timer?.Stop();
        _logger.Info($"Drag {Id}: {outcome}, effect {effect}{(_source.CancelReason is null ? string.Empty : ", " + _source.CancelReason)}.");

        // A session that ends before its drag loop ran always answers the start, so that
        // the client gets a refusal instead of waiting.
        if (!wasDragging)
        {
            RaiseStarted(false, outcome == DragOutcome.Cancelled ? LocalText.Of(nameof(Strings.DragCancelledBeforeStart)) : message);
        }

        var cancelText = outcome == DragOutcome.Cancelled
            ? LocalText.Of(nameof(Strings.DragCancelledReason), _source.CancelReason ?? LocalText.Of(nameof(Strings.NoReasonGiven)))
            : null;

        try
        {
            Completed?.Invoke(this, new DragCompletion(Id, outcome, effect, _target, message ?? cancelText));
        }
        catch (Exception ex)
        {
            _logger.Error($"Drag {Id}: the completion handler failed.", ex);
        }

        // Leave the handler that called this before the window goes away.
        try
        {
            _proxy?.BeginInvoke(() =>
            {
                _proxy?.Close();
                _context?.ExitThread();
            });
        }
        catch (InvalidOperationException)
        {
            // The window is already gone, after a failure of the drag thread.
            _context?.ExitThread();
        }
    }

    private void SetPhase(Phase phase)
    {
        _phase = phase;
        Interlocked.Exchange(ref _phaseTicks, Environment.TickCount64);
    }
}
