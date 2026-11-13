using System.Collections.Concurrent;
using UltimateRDP.Agent.Interop;
using UltimateRDP.Agent.Logging;

namespace UltimateRDP.Agent.Channel;

/// <summary>
/// Hidden top level window of the UI thread. It receives WM_WTSSESSION_CHANGE and tells
/// the channel loop when a client connects to or disconnects from the session; it answers
/// WM_QUERYENDSESSION at once so that the agent never delays a log off, and reports
/// WM_ENDSESSION; and it runs on the UI thread the actions that other threads
/// <see cref="Post"/>. It is a plain window, not a form: nothing is shown, and no part
/// of WinForms beyond the message loop is loaded for it. Create it on the UI thread.
/// </summary>
internal sealed class SessionNotificationWindow : NativeWindow, IDisposable
{
    private const int WmRunPosted = User32.WmApp + 1;

    private readonly ChannelLoop _loop;
    private readonly FileLogger _logger;
    private readonly Action _sessionEnding;
    private readonly ConcurrentQueue<Action> _posted = new();
    private readonly bool _registered;

    /// <param name="loop">The channel loop to wake on connections.</param>
    /// <param name="logger">The log.</param>
    /// <param name="sessionEnding">
    /// Runs on the UI thread when the session ends. It must return quickly: Windows may end
    /// the process as soon as WM_ENDSESSION is answered.
    /// </param>
    public SessionNotificationWindow(ChannelLoop loop, FileLogger logger, Action sessionEnding)
    {
        _loop = loop;
        _logger = logger;
        _sessionEnding = sessionEnding;
        CreateHandle(new CreateParams { Caption = "UltimateRDP session notifications" });
        _registered = WtsApi.WTSRegisterSessionNotification(Handle, WtsApi.NotifyForThisSession);
        if (!_registered)
        {
            _logger.Warn("Cannot register for session notifications; reconnects are detected by the regular retry only.");
        }
    }

    /// <summary>Runs on the UI thread at each WM_TIMER of the window, with the id of the timer.</summary>
    public Action<nuint>? TimerElapsed { get; set; }

    /// <summary>Runs on the UI thread when the session is disconnected, after the channel loop heard of it.</summary>
    public Action? Disconnected { get; set; }

    /// <summary>
    /// Runs <paramref name="action"/> on the UI thread, later. Callable from any thread.
    /// Returns false when the window is gone.
    /// </summary>
    public bool Post(Action action)
    {
        var handle = Handle;
        if (handle == IntPtr.Zero)
        {
            return false;
        }

        _posted.Enqueue(action);
        return User32.PostMessage(handle, WmRunPosted, IntPtr.Zero, IntPtr.Zero);
    }

    public void Dispose()
    {
        if (Handle != IntPtr.Zero)
        {
            if (_registered)
            {
                WtsApi.WTSUnRegisterSessionNotification(Handle);
            }

            DestroyHandle();
        }
    }

    protected override void WndProc(ref Message m)
    {
        switch (m.Msg)
        {
            case User32.WmWtsSessionChange:
                OnSessionChange((int)m.WParam);
                break;

            case User32.WmQueryEndSession:
                // The agent holds nothing that needs saving: it never delays a log off.
                m.Result = 1;
                return;

            case User32.WmEndSession:
                if (m.WParam != IntPtr.Zero)
                {
                    _sessionEnding();
                }

                m.Result = IntPtr.Zero;
                return;

            case WmRunPosted:
                RunPosted();
                return;

            case User32.WmTimer:
                TimerElapsed?.Invoke((nuint)m.WParam);
                return;
        }

        base.WndProc(ref m);
    }

    /// <summary>
    /// An error in the window procedure. NativeWindow catches it and calls this method,
    /// which does nothing by default, so without this override the error would be lost.
    /// </summary>
    protected override void OnThreadException(Exception e) =>
        _logger.Error("Unexpected error in the session window; the agent goes on.", e);

    /// <summary>Runs the posted actions. An error in one is logged and the others still run.</summary>
    private void RunPosted()
    {
        while (_posted.TryDequeue(out var action))
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                _logger.Error("Unexpected error in an action of the UI thread; the agent goes on.", ex);
            }
        }
    }

    private void OnSessionChange(int change)
    {
        switch (change)
        {
            case User32.WtsRemoteConnect:
                _logger.Info("A remote client connected to the session.");
                _loop.OnSessionConnected("a remote client connected");
                break;

            case User32.WtsConsoleConnect:
                _logger.Info("The session was connected to the console.");
                _loop.OnSessionConnected("the session was connected to the console");
                break;

            case User32.WtsSessionLogon:
                _loop.OnSessionConnected("a user logged on");
                break;

            case User32.WtsRemoteDisconnect:
            case User32.WtsConsoleDisconnect:
                _logger.Info("The session was disconnected.");
                _loop.OnSessionDisconnected("the session was disconnected");
                Disconnected?.Invoke();
                break;
        }
    }
}
