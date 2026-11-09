using UltimateRDP.Agent.Channel;
using UltimateRDP.Agent.Drops;
using UltimateRDP.Agent.Interop;
using UltimateRDP.Agent.Logging;
using UltimateRDP.Agent.Settings;
using UltimateRDP.Core.Agent;

namespace UltimateRDP.Agent.Tray;

/// <summary>
/// Application context of the agent, on the UI thread: the channel loop, the hidden
/// session window, and the tray icon when the <c>trayIcon</c> setting asks for it.
/// </summary>
/// <remarks>
/// Most sessions of a server are opened with the Microsoft client and never see an
/// UltimateRDP client. For them the agent stays invisible and light: the tray icon, its
/// menu and the drag infrastructure are only created when they are needed, that is when
/// a client speaks on the channel (or at once with <c>"trayIcon": "always"</c>).
/// </remarks>
internal sealed class TrayContext : ApplicationContext
{
    /// <summary>Wait for the drag and the channel thread when the agent exits normally.</summary>
    private static readonly TimeSpan StopWait = TimeSpan.FromSeconds(2);

    /// <summary>Wait for each of them when the session ends: a log off must never wait on the agent.</summary>
    private static readonly TimeSpan SessionEndWait = TimeSpan.FromMilliseconds(500);

    private readonly FileLogger _logger;
    private readonly AgentSettings _settings;
    private readonly ChannelLoop _loop;
    private readonly SessionNotificationWindow _window;
    private readonly Lock _servicesGate = new();
    private AgentServices? _services;
    private bool _servicesStopped;
    private AgentTrayIcon? _tray;
    private bool _clientConnected;
    private bool _exiting;

    public TrayContext(FileLogger logger, AgentSettings settings, EventWaitHandle? exitEvent)
    {
        _logger = logger;
        _settings = settings;
        _loop = new ChannelLoop(logger, exitEvent);
        _window = new SessionNotificationWindow(_loop, logger, OnSessionEnding);
        _loop.ClientPresenceChanged += present => _window.Post(() => OnClientPresence(present));
        _loop.ExitRequested += () => _window.Post(() => Exit("a request from --uninstall"));

        StagingFolder.DeleteOldFoldersInBackground(logger, AgentInfo.StagingDirectory);
        UpdateTray();
        _loop.Start(Handle);
    }

    /// <summary>
    /// Runs the message loop of the UI thread until the agent exits. While no tray icon
    /// exists, a plain Windows message loop is enough for the hidden session window; the
    /// WinForms loop, which loads GDI+ at once, only starts with the icon, whose menu needs
    /// it.
    /// </summary>
    public void Run()
    {
        if (_tray is null && !RunPlainLoop())
        {
            return;
        }

        if (!_exiting)
        {
            Application.Run(this);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            StopAll(StopWait);
            _tray?.Dispose();
            _tray = null;
            _window.Dispose();
        }

        base.Dispose(disposing);
    }

    /// <summary>
    /// Dispatches the messages of the thread until the agent exits or the tray icon is
    /// created. Returns false when the loop ended for good (exit or WM_QUIT), true when the
    /// WinForms loop must take over.
    /// </summary>
    private bool RunPlainLoop()
    {
        while (!_exiting && _tray is null)
        {
            var result = User32.GetMessage(out var message, IntPtr.Zero, 0, 0);
            if (result == 0)
            {
                Exit("a WM_QUIT message");
                return false;
            }

            if (result < 0)
            {
                // Cannot happen with these arguments; the WinForms loop takes over.
                _logger.Warn("GetMessage failed; switching to the WinForms message loop.");
                return true;
            }

            User32.TranslateMessage(message);
            User32.DispatchMessage(message);
        }

        return !_exiting;
    }

    /// <summary>Handles one line from the client, on the channel thread.</summary>
    private void Handle(byte[] payload)
    {
        AgentServices services;
        lock (_servicesGate)
        {
            if (_servicesStopped)
            {
                return;
            }

            services = _services ??= new AgentServices(_logger, _settings, _loop.TrySend);
        }

        services.Handle(payload);
    }

    private void OnClientPresence(bool present)
    {
        _clientConnected = present;
        UpdateTray();
    }

    private void UpdateTray()
    {
        if (_exiting)
        {
            return;
        }

        if (TrayIconModes.IsVisible(_settings.TrayIcon, _clientConnected))
        {
            if (_tray is null)
            {
                _tray = new AgentTrayIcon(_logger, _settings, () => Exit("the Exit menu"));
                WakeMessageLoop();
            }

            _tray.Visible = true;
        }
        else if (_tray is not null)
        {
            _tray.Visible = false;
        }
    }

    /// <summary>
    /// The session ends (WM_ENDSESSION). Windows may end the process as soon as this
    /// returns, so everything happens here and quickly: the simulated button is released,
    /// the drag is cancelled, the channel is closed. The log is already on disk, since
    /// every line is written at once.
    /// </summary>
    private void OnSessionEnding()
    {
        _logger.Info("The session is ending; the agent exits.");
        InputInjector.ReleaseIfDown();
        StopAll(SessionEndWait);
        Exit("the end of the session");
    }

    /// <summary>Stops the drag first, while its result can still be sent, then the channel.</summary>
    private void StopAll(TimeSpan wait)
    {
        AgentServices? services;
        lock (_servicesGate)
        {
            if (_servicesStopped)
            {
                return;
            }

            _servicesStopped = true;
            services = _services;
        }

        services?.Stop(wait);
        _loop.Stop(wait);
    }

    private void Exit(string reason)
    {
        if (_exiting)
        {
            return;
        }

        _exiting = true;
        _logger.Info($"Exiting after {reason}.");
        if (_tray is not null)
        {
            _tray.Visible = false;
        }

        ExitThread();
        WakeMessageLoop();
    }

    /// <summary>
    /// A message sent with SendMessage, such as WM_ENDSESSION, is handled inside GetMessage,
    /// which then keeps waiting for a posted message. Posting an empty action makes the
    /// plain loop check its state again.
    /// </summary>
    private void WakeMessageLoop() => _window.Post(static () => { });
}
