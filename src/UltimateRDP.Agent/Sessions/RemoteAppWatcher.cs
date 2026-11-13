using System.Globalization;
using UltimateRDP.Agent.Interop;
using UltimateRDP.Agent.Logging;
using UltimateRDP.Core.Agent;

namespace UltimateRDP.Agent.Sessions;

/// <summary>
/// Keeps the agent from holding a RemoteApp session open. Such a session ends when no
/// program of the user is left in it; the agent would count as one. So in a RemoteApp
/// session the agent lists the processes of its session every 15 seconds and exits once
/// nothing but the processes Windows ignores is left, as <see cref="RemoteAppSessionRule"/>
/// and <see cref="RemoteAppExitWatch"/> decide.
/// </summary>
/// <remarks>
/// Runs on the UI thread, with a timer of the hidden session window: no thread is added. At
/// start the agent lists the processes of its session once, to know its shell. In a desktop
/// session that is all, nothing is logged above the debug level and the timer stops. When
/// neither shell runs yet, as for a task started at logon before the shell, the agent looks
/// again at each tick for <see cref="RemoteAppSessionRule.ClassifyPeriod"/>.
/// </remarks>
internal sealed class RemoteAppWatcher
{
    /// <summary>The id of the timer on the session window.</summary>
    public const nuint TimerId = 0x55;

    private readonly FileLogger _logger;
    private readonly RemoteAppSession _state;
    private readonly Func<bool> _busy;
    private readonly Action<string> _exit;
    private readonly RemoteAppExitWatch _watch = new();
    private readonly string _ownName = Path.GetFileName(Environment.ProcessPath ?? "UltimateRDP.Agent.exe");
    private readonly uint _sessionId;
    private IntPtr _window;
    private long _startTicks;
    private bool _timerRunning;
    private bool _remoteApp;
    private bool _listFailureLogged;
    private int _listFailures;
    private bool _exited;
    private IReadOnlyList<string> _sysProcs = [];

    /// <param name="logger">The log.</param>
    /// <param name="state">Told when the session turns out to be a RemoteApp session.</param>
    /// <param name="busy">True while a drag or a transfer runs.</param>
    /// <param name="exit">Ends the agent, with the reason for the log.</param>
    public RemoteAppWatcher(FileLogger logger, RemoteAppSession state, Func<bool> busy, Action<string> exit)
    {
        _logger = logger;
        _state = state;
        _busy = busy;
        _exit = exit;
        _sessionId = Kernel32.ProcessIdToSessionId((uint)Environment.ProcessId, out var session) ? session : uint.MaxValue;
    }

    /// <summary>Looks at the shell of the session once, and starts the timer when needed.</summary>
    public void Start(IntPtr window)
    {
        _window = window;
        _startTicks = Environment.TickCount64;
        if (_sessionId == uint.MaxValue)
        {
            _logger.Debug("The session of the agent is not known; no RemoteApp check.");
            return;
        }

        Classify();
    }

    /// <summary>WM_TIMER of <see cref="TimerId"/>.</summary>
    public void OnTimer()
    {
        if (_exited)
        {
            return;
        }

        if (_remoteApp)
        {
            Check();
        }
        else
        {
            Classify();
        }
    }

    /// <summary>The session was disconnected: in a RemoteApp session without application, the agent exits at once.</summary>
    public void OnDisconnected()
    {
        if (!_remoteApp || _exited || ListApplications() is not { } applications)
        {
            return;
        }

        var check = _watch.OnDisconnected(applications);
        _logger.Info($"RemoteApp session disconnected: {check.Describe()}.");
        if (check.Verdict == RemoteAppVerdict.Exit)
        {
            ExitNow("the session was disconnected and no application is left");
        }
    }

    /// <summary>Stops the timer, when the agent exits.</summary>
    public void Stop() => SetTimer(false);

    private void Classify()
    {
        var names = WtsApi.SessionProcessNames(_sessionId, out var error);
        var shell = names is null ? SessionShell.Unknown : RemoteAppSessionRule.Classify(names);
        switch (shell)
        {
            case SessionShell.Desktop:
                SetTimer(false);
                _logger.Debug("Explorer runs in this session: a desktop session, no RemoteApp check.");
                return;

            case SessionShell.RemoteApp:
                OnRemoteAppFound(names!);
                return;
        }

        if (names is null && !_listFailureLogged)
        {
            _listFailureLogged = true;
            _logger.Warn(Invariant($"Cannot list the processes of session {_sessionId} (error {error}); the agent cannot tell whether it is a RemoteApp session yet."));
        }

        if (Environment.TickCount64 - _startTicks >= (long)RemoteAppSessionRule.ClassifyPeriod.TotalMilliseconds)
        {
            SetTimer(false);
            _logger.Info(Invariant($"Neither Explorer nor the RemoteApp shell runs in session {_sessionId} after {RemoteAppSessionRule.ClassifyPeriod.TotalMinutes:0} minutes; the agent treats it as a desktop session."));
            return;
        }

        SetTimer(true);
    }

    private void OnRemoteAppFound(IReadOnlyList<string> names)
    {
        _remoteApp = true;
        var sysProcs = SysProcs.Names(out var sysProcsError);
        _sysProcs = sysProcs ?? [];
        var listed = _sysProcs.Any(name => RemoteAppSessionRule.Normalize(name) == RemoteAppSessionRule.Normalize(SysProcs.AgentValueName));
        _logger.Info(Invariant($"RemoteApp session detected (rdpshell or rdpinit present, no explorer) in session {_sessionId}, {names.Count} process(es). The agent exits once no application is left, checked every {RemoteAppSessionRule.CheckInterval.TotalSeconds:0} s; the tray icon stays hidden unless trayIcon is always."));
        _logger.Info(sysProcs is null
            ? $"SysProcs cannot be read ({sysProcsError}); only the fixed list of session processes is ignored."
            : $"SysProcs lists {_sysProcs.Count} name(s), {(listed ? "the agent included" : "not the agent")}: {string.Join(", ", _sysProcs)}.");
        _state.SetDetected();
        if (!SetTimer(true))
        {
            // Without the check the agent could keep the session open: it leaves at once.
            ExitNow("the RemoteApp check cannot run without its timer");
        }
    }

    private void Check()
    {
        if (ListApplications() is not { } applications)
        {
            // An agent that cannot see the session must not keep it open.
            if (++_listFailures >= RemoteAppSessionRule.EmptyChecksToExit)
            {
                ExitNow(Invariant($"the processes of the session could not be listed {_listFailures} times in a row"));
            }

            return;
        }

        _listFailures = 0;
        var check = _watch.Check(applications, _busy());
        if (check.Changed)
        {
            _logger.Info($"RemoteApp session: {check.Describe()}.");
        }

        if (check.Verdict == RemoteAppVerdict.Exit)
        {
            ExitNow("no application is left");
        }
    }

    /// <summary>The applications of the session, or null when the processes cannot be listed; a failure is logged once.</summary>
    private IReadOnlyList<string>? ListApplications()
    {
        var names = WtsApi.SessionProcessNames(_sessionId, out var error);
        if (names is null)
        {
            if (!_listFailureLogged)
            {
                _listFailureLogged = true;
                _logger.Warn(Invariant($"Cannot list the processes of session {_sessionId} (error {error}); the RemoteApp check waits for the next time."));
            }

            return null;
        }

        return RemoteAppSessionRule.Applications(names, _sysProcs, _ownName);
    }

    private void ExitNow(string reason)
    {
        _exited = true;
        SetTimer(false);
        _logger.Info($"RemoteApp session: {reason}; the agent exits so that the session can end.");
        _exit(reason);
    }

    /// <summary>Starts or stops the timer. Returns false when it should run and could not be started.</summary>
    private bool SetTimer(bool running)
    {
        if (running == _timerRunning)
        {
            return true;
        }

        if (!running)
        {
            _timerRunning = false;
            User32.KillTimer(_window, TimerId);
            return true;
        }

        if (_window == IntPtr.Zero || User32.SetTimer(_window, TimerId, (uint)RemoteAppSessionRule.CheckInterval.TotalMilliseconds, IntPtr.Zero) == 0)
        {
            _logger.Warn("Cannot start the timer of the RemoteApp check.");
            return false;
        }

        _timerRunning = true;
        return true;
    }

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
