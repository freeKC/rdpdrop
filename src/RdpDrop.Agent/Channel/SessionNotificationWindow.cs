using RdpDrop.Agent.Interop;
using RdpDrop.Agent.Logging;

namespace RdpDrop.Agent.Channel;

/// <summary>
/// Hidden window that receives WM_WTSSESSION_CHANGE and tells the channel loop when a
/// client connects to or disconnects from the session. Create it on the UI thread.
/// </summary>
internal sealed class SessionNotificationWindow : NativeWindow, IDisposable
{
    private readonly ChannelLoop _loop;
    private readonly FileLogger _logger;
    private readonly bool _registered;

    public SessionNotificationWindow(ChannelLoop loop, FileLogger logger)
    {
        _loop = loop;
        _logger = logger;
        CreateHandle(new CreateParams { Caption = "RdpDrop session notifications" });
        _registered = WtsApi.WTSRegisterSessionNotification(Handle, WtsApi.NotifyForThisSession);
        if (!_registered)
        {
            _logger.Warn("Cannot register for session notifications; reconnects are detected by the regular retry only.");
        }
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
        if (m.Msg == User32.WmWtsSessionChange)
        {
            OnSessionChange((int)m.WParam);
        }

        base.WndProc(ref m);
    }

    private void OnSessionChange(int change)
    {
        switch (change)
        {
            case User32.WtsRemoteConnect:
            case User32.WtsConsoleConnect:
                _logger.Info(change == User32.WtsRemoteConnect ? "A remote client connected to the session." : "The session was connected to the console.");
                _loop.SetSessionDisconnected(false);
                _loop.RestartNow("the session was connected");
                break;

            case User32.WtsRemoteDisconnect:
            case User32.WtsConsoleDisconnect:
                _logger.Info("The session was disconnected.");
                _loop.SetSessionDisconnected(true);
                _loop.RestartNow("the session was disconnected");
                break;
        }
    }
}
