using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using MSTSCLib;
using UltimateRDP.Client.Localization;
using UltimateRDP.Client.Logging;
using UltimateRDP.Client.Rdp;
using UltimateRDP.Client.Resources;
using UltimateRDP.Core.Display;
using UltimateRDP.Core.Paths;
using UltimateRDP.Core.Protocol;
using UltimateRDP.Core.RdpFiles;
using UltimateRDP.Core.Transfer;

namespace UltimateRDP.Client.Sessions;

/// <summary>
/// A tab that runs a RemoteApp program: the windows of the program are windows of this
/// process, outside the tab, and the tab shows a <see cref="RemoteAppPanel"/> instead of the
/// session.
/// </summary>
/// <remarks>
/// <para>
/// The control gets RemoteProgramMode and the RemoteProgram2 values before Connect. When no
/// window of the program appeared and the control did not report a start within ten seconds
/// after the logon, the client asks once with ServerStartProgram.
/// </para>
/// <para>
/// Drops come from the windows of the program, found by <see cref="RailDropTargets"/>. Each
/// window of the server is a copy of the window shown on the server, and anything the agent
/// shows, its proxy window too, would appear here as a window of its own. So nothing goes to
/// the agent while the drag moves: at the local drop the client sends dragEnter, holds the
/// drop for the answer as a quick drop of a desktop session (<see cref="DropDecision"/>,
/// with <see cref="DropDecision.RemoteAppWait"/>), and keeps the local drop call open until
/// the agent delivered it, a transfer started, or six seconds passed. The agent replays the
/// drag under the point with a small proxy window.
/// </para>
/// </remarks>
internal sealed partial class SessionView : IRailSession
{
    private const int StartFallbackMilliseconds = 10_000;

    /// <summary>Longest time the local drop call stays open for the replay in the session.</summary>
    private const int DropCallMilliseconds = 6000;

    private const int PumpWaitMilliseconds = 50;

    private readonly System.Windows.Forms.Timer _startTimer = new() { Interval = StartFallbackMilliseconds };
    private RemoteAppLaunch? _remoteApp;
    private RemoteAppPanel? _panel;
    private bool _remoteProgramAnswered;
    private bool _remoteWindowShown;
    private bool _startProgramCalled;
    private bool _remoteAppLoggedOn;
    private bool _railAttached;
    private string? _railDropId;
    private Rectangle _railDropWindow;
    private IReadOnlyList<string>? _railHoverFiles;
    private LocalText? _railHoverProblem;

    /// <summary>True when the tab runs a RemoteApp program.</summary>
    public bool IsRemoteApp => _remoteApp is not null;

    /// <summary>The name of the RemoteApp program, or null for a desktop.</summary>
    public string? RemoteAppName => _remoteApp?.DisplayName;

    /// <summary>
    /// True when closing the tab leaves a RemoteApp program running on the server: the
    /// session is not disconnected and windows of the program are open.
    /// </summary>
    public bool KeepsProgramOpenOnClose => IsRemoteApp && State != SessionState.Disconnected && !_closeRequested && RailDropTargets.WindowCount(this) > 0;

    string IRailSession.Description => $"{RemoteAppName} on {Host}";

    DragDropEffects IRailSession.RailDragOver(IDataObject? data, Point screenPoint, DragDropEffects allowed, int oleKeyState) =>
        _rdp.ProcessRemoteWindowDragOver(data, screenPoint, allowed, oleKeyState);

    DragDropEffects IRailSession.RailDrop(IDataObject? data, Point screenPoint, DragDropEffects allowed, int oleKeyState) =>
        _rdp.ProcessRemoteWindowDrop(data, screenPoint, allowed, oleKeyState);

    void IRailSession.RailDragLeave() => _rdp.ProcessDragLeave();

    /// <summary>Sets the tab up for RemoteApp when the .rdp file asks for it. Called by the constructor.</summary>
    private void InitializeRemoteApp()
    {
        _remoteApp = RemoteAppLaunch.FromFile(RdpFile, out _);
        if (_remoteApp is null)
        {
            return;
        }

        _rdp.RemoteAppMode = true;
        _display.RemoteApp = true;
        _agent.RemoteApp = true;
        _panel = new RemoteAppPanel { Dock = DockStyle.Fill };
        Controls.Add(_panel);
        _panel.BringToFront();
        _rdp.OnRemoteProgramResult += (_, e) => OnRemoteProgramResult(e.bstrRemoteProgram, e.lError, e.vbIsExecutable);
        _rdp.OnRemoteProgramDisplayed += (_, e) => OnRemoteProgramDisplayed(e.vbDisplayed, e.uDisplayInformation);
        _rdp.OnRemoteWindowDisplayed += (_, e) => OnRemoteWindowDisplayed(e.vbDisplayed, e.hwnd, e.windowAttribute);
        _startTimer.Tick += (_, _) => OnStartFallback();
        ClientLog.Info($"RemoteApp tab for {Host}: {_remoteApp}.");
        UpdatePanel();
    }

    private void DisposeRemoteApp()
    {
        _startTimer.Dispose();
        DetachRail();
    }

    /// <summary>A new connection starts: what the previous one saw is forgotten.</summary>
    private void OnRemoteAppConnecting()
    {
        _remoteProgramAnswered = false;
        _remoteWindowShown = false;
        _startProgramCalled = false;
        _remoteAppLoggedOn = false;
        _startTimer.Stop();
    }

    private void OnRemoteAppConnected()
    {
        if (!_railAttached)
        {
            _railAttached = true;
            RailDropTargets.Attach(this);
        }
    }

    private void OnRemoteAppLoggedOn()
    {
        _remoteAppLoggedOn = true;
        LogMonitorLayout("logged on");
        if (!_remoteProgramAnswered && !_remoteWindowShown)
        {
            _startTimer.Stop();
            _startTimer.Start();
        }
    }

    /// <summary>
    /// The session is disconnected: its windows are gone. A session that was logged on
    /// closes its tab, since the program is no longer on the screen; a connection that never
    /// logged on keeps the tab with the reason, so that the user can read it and try again.
    /// </summary>
    private void OnRemoteAppDisconnected()
    {
        _startTimer.Stop();
        DetachRail();
        var loggedOn = _remoteAppLoggedOn;
        _remoteAppLoggedOn = false;
        if (loggedOn && !_closeRequested && !_reconnectRequested)
        {
            ClientLog.Info($"The RemoteApp session of {RemoteAppName} on {Host} ended; the tab closes.");
            BeginInvoke(BeginClose);
        }
    }

    private void DetachRail()
    {
        if (_railAttached)
        {
            _railAttached = false;
            RailDropTargets.Detach(this);
        }
    }

    private void OnRemoteProgramResult(string program, RemoteProgramResult result, bool isExecutable)
    {
        _remoteProgramAnswered = true;
        _startTimer.Stop();
        var text = Invariant($"Event OnRemoteProgramResult ({Host}): program \"{program}\", result {result} ({(int)result}), executable {isExecutable}");
        if (result == RemoteProgramResult.remoteAppResultOk)
        {
            ClientLog.Info(text);
            return;
        }

        ClientLog.Warn(text);
        ShowDropStatus(result == RemoteProgramResult.remoteAppResultNotInWhitelist
            ? LocalText.Of(nameof(Strings.RemoteAppNotPublished), RemoteAppName ?? program)
            : LocalText.Of(nameof(Strings.RemoteAppStartFailed), RemoteAppName ?? program, result.ToString().Replace("remoteAppResult", string.Empty, StringComparison.Ordinal)));
    }

    private void OnRemoteProgramDisplayed(bool displayed, uint displayInformation)
    {
        ClientLog.Info(Invariant($"Event OnRemoteProgramDisplayed ({Host}): displayed {displayed}, display information 0x{displayInformation:X8}, {RailDropTargets.WindowCount(this)} window(s) known."));
        UpdatePanel();
    }

    private void OnRemoteWindowDisplayed(bool displayed, _RemotableHandle handle, RemoteWindowDisplayedAttribute attribute)
    {
        // A window handle has 32 significant bits, and the event gives them in the in process
        // member of the union.
        var hwnd = new IntPtr(handle.u.hInproc);
        ClientLog.Info(Invariant($"Event OnRemoteWindowDisplayed ({Host}): displayed {displayed}, window 0x{hwnd.ToInt64():X} (context 0x{handle.fContext:X8}, in process 0x{handle.u.hInproc:X8}, remote 0x{handle.u.hRemote:X8}), attribute {attribute}."));
        if (attribute == RemoteWindowDisplayedAttribute.remoteAppShellIconDisplayed)
        {
            // An icon of the notification area of the server, shown here; it takes no drop.
            return;
        }

        if (displayed)
        {
            _startTimer.Stop();
            if (!_remoteWindowShown)
            {
                _remoteWindowShown = true;
                LogMonitorLayout("first window");
            }
        }

        if (hwnd != IntPtr.Zero)
        {
            RailDropTargets.OnWindowDisplayed(this, hwnd, displayed, attribute.ToString());
        }

        UpdatePanel();
        RaiseStateChanged();
    }

    /// <summary>
    /// Ten seconds after the logon, the control neither reported the start of the program nor
    /// showed a window of it: the client asks the server once.
    /// </summary>
    private void OnStartFallback()
    {
        _startTimer.Stop();
        if (_remoteApp is not { } launch || _remoteProgramAnswered || _remoteWindowShown || _startProgramCalled || State != SessionState.Connected)
        {
            return;
        }

        _startProgramCalled = true;
        var program = launch.Program ?? string.Empty;
        var file = launch.File ?? string.Empty;
        var args = launch.Args ?? string.Empty;
        ClientLog.Info(Invariant($"RemoteApp ({Host}): no OnRemoteProgramResult and no window {StartFallbackMilliseconds / 1000} s after the logon; calling ServerStartProgram(\"{program}\", \"{file}\", \"\", {launch.ExpandWorkingDir}, \"{args}\", {launch.ExpandArgs}) once."));
        try
        {
            _rdp.RemoteProgram.ServerStartProgram(program, file, string.Empty, launch.ExpandWorkingDir, args, launch.ExpandArgs);
            ClientLog.Info($"RemoteApp ({Host}): ServerStartProgram returned.");
        }
        catch (Exception ex) when (ex is COMException or ArgumentException or InvalidOperationException or AxHost.InvalidActiveXStateException)
        {
            ClientLog.Warn(Invariant($"RemoteApp ({Host}): ServerStartProgram failed (0x{ex.HResult:X8}: {ex.Message})."));
        }
    }

    /// <summary>
    /// Logs the monitors of the session as the control sees them, against the local virtual
    /// screen: drops are only right when the layout is the same.
    /// </summary>
    private void LogMonitorLayout(string reason)
    {
        var screen = SystemInformation.VirtualScreen;
        var local = new ScreenRect(screen.X, screen.Y, screen.Width, screen.Height);
        try
        {
            if (_rdp.GetOcx() is not IMsRdpClientNonScriptable6 monitors)
            {
                ClientLog.Info($"RemoteApp monitors ({reason}): IMsRdpClientNonScriptable6 not available; local virtual screen {local}.");
                return;
            }

            monitors.GetRemoteMonitorsBoundingBox(out var left, out var top, out var right, out var bottom);
            var count = monitors.RemoteMonitorCount;
            var matchesLocal = monitors.RemoteMonitorLayoutMatchesLocal;
            var match = ScreenMapping.Compare(left, top, right, bottom, local);
            var line = Invariant($"RemoteApp monitors ({reason}): remote box ({left}, {top}) to ({right}, {bottom}), {count} monitor(s), RemoteMonitorLayoutMatchesLocal {matchesLocal}; local virtual screen {local}, {SystemInformation.MonitorCount} monitor(s); layout {match}.");
            if (match == MonitorLayoutMatch.Differs || !matchesLocal)
            {
                ClientLog.Warn(line + " Drops may land away from the cursor; reconnect after a change of monitors.");
            }
            else
            {
                ClientLog.Info(line);
            }
        }
        catch (Exception ex) when (ex is COMException or ArgumentException or InvalidCastException or AxHost.InvalidActiveXStateException)
        {
            ClientLog.Info(Invariant($"RemoteApp monitors ({reason}): not available (0x{ex.HResult:X8}); local virtual screen {local}."));
        }
    }

    private void UpdatePanel()
    {
        if (_panel is null || _remoteApp is null)
        {
            return;
        }

        _panel.ShowState(
            _remoteApp.DisplayName,
            LocalText.Of(nameof(Strings.RemoteAppHint), _remoteApp.DisplayName).ToString(),
            ConnectionText,
            AgentText,
            DropText);
    }

    /// <summary>
    /// A drag over a window of the program: nothing goes to the agent before the drop; the
    /// answer only says whether a drop would be taken.
    /// </summary>
    private void OnRemoteAppDragOver(FileDragEventArgs e)
    {
        _dropTextBeforeDrag ??= _dropText;
        var reason = DropRefusalReason() ?? HoverProblem(e.Files);
        if (reason is not null)
        {
            e.Effect = DragDropEffects.None;
            NoteBusyRefusal();
            if (!Equals(_dropText, reason))
            {
                ShowDropStatus(reason);
            }

            return;
        }

        e.Effect = DragDropEffects.Copy;
    }

    private void OnRemoteAppDragLeft()
    {
        if (_dropTextBeforeDrag is not null)
        {
            ShowDropStatus(_dropTextBeforeDrag);
            _dropTextBeforeDrag = null;
        }
    }

    /// <summary>The paths the agent could not read, checked once per list of files, or null.</summary>
    private LocalText? HoverProblem(IReadOnlyList<string> files)
    {
        if (_railHoverFiles is not null && _railHoverFiles.SequenceEqual(files))
        {
            return _railHoverProblem;
        }

        _railHoverFiles = files;
        _railHoverProblem = null;
        foreach (var path in files)
        {
            var rejection = ClientPathMapper.Classify(path, out _);
            if (rejection != PathRejection.None)
            {
                _railHoverProblem = LocalText.Of(nameof(Strings.DropRefusedReason), PathTexts.Describe(rejection, path));
                break;
            }
        }

        return _railHoverProblem;
    }

    /// <summary>
    /// The drop on a window of the program: the drag is replayed in the session at the drop
    /// point, and the local call stays open while the agent replays it.
    /// </summary>
    private void OnRemoteAppDropped(FileDragEventArgs e)
    {
        _dropTextBeforeDrag = null;
        var reason = DropRefusalReason();
        if (reason is not null)
        {
            ClientLog.Info(Invariant($"RemoteApp drop of {e.Files.Count} item(s) refused: {reason.ToEnglish()}"));
            NoteBusyRefusal();
            ShowDropStatus(reason);
            return;
        }

        CloseEndedProgressWindow();
        var items = MapItems(e.Files, out var problem);
        if (items is null)
        {
            ShowDropStatus(problem ?? LocalText.Of(nameof(Strings.DropRefused)));
            return;
        }

        var id = Guid.NewGuid().ToString("N");
        var remote = e.ClientPoint;
        var window = NativeMethods.TopLevelWindowAt(e.ScreenPoint);
        _railDropId = id;
        _railDropWindow = NativeMethods.WindowRectangle(window);
        var live = new LiveDrag(id, e.Files, items)
        {
            LastPoint = remote,
            LastKeys = e.KeyState,
            LastSent = Environment.TickCount64,
        };
        LogDrag("RemoteApp drop, replayed in the session", id, e, remote);
        ClientLog.Info(Invariant($"RemoteApp drop {id}: screen ({e.ScreenPoint.X}, {e.ScreenPoint.Y}), local window 0x{window.ToInt64():X} {NativeMethods.ClassName(window)} at {DescribeRect(_railDropWindow)}, virtual screen {SystemInformation.VirtualScreen}; dragEnter and drop go to the agent now."));
        if (!_agent.SendDrag(new DragEnterMessage(id, remote.X, remote.Y, items, e.KeyState), id))
        {
            ShowDropStatus(LocalText.Of(nameof(Strings.DropFailedNotSent)));
            return;
        }

        live.Sent = true;
        e.Effect = HoldDrop(live, e, remote, DropDecision.RemoteAppWait);
        KeepDropCallOpen(id);
    }

    /// <summary>
    /// Runs the messages of the UI thread inside the local drop call until the agent
    /// delivered the drop, a transfer started, the drag was refused, or
    /// <see cref="DropCallMilliseconds"/> passed. While the call is open, the source of the
    /// drag holds the mouse, so that the windows of the program do not send the moves of
    /// the mouse to the server during the replay. The log says how far the mouse moved.
    /// </summary>
    private void KeepDropCallOpen(string id)
    {
        var watch = Stopwatch.StartNew();
        var start = Cursor.Position;
        string? outcome;
        while ((outcome = DropCallOutcome(id)) is null && watch.ElapsedMilliseconds < DropCallMilliseconds)
        {
            NativeMethods.MsgWaitForMultipleObjectsEx(0, IntPtr.Zero, PumpWaitMilliseconds, NativeMethods.QsAllInput, NativeMethods.MwmoInputAvailable);
            Application.DoEvents();
        }

        if (IsDisposed)
        {
            return;
        }

        var end = Cursor.Position;
        ClientLog.Info(Invariant($"RemoteApp drop {id}: the local drop call stayed open {watch.ElapsedMilliseconds} ms, {outcome ?? $"until the limit of {DropCallMilliseconds} ms"}; the mouse moved by ({end.X - start.X}, {end.Y - start.Y}) meanwhile."));
    }

    /// <summary>Why the local drop call of <paramref name="id"/> may return, or null while it waits.</summary>
    private string? DropCallOutcome(string id)
    {
        if (IsDisposed || _closeRequested || State != SessionState.Connected)
        {
            return "the session closed";
        }

        if (_held?.Live.Id == id)
        {
            return null;
        }

        if (_upload?.DragId == id || _direct?.DragId == id)
        {
            return "a transfer started";
        }

        return _pendingDropId == id ? null : "the agent answered";
    }

    /// <summary>
    /// The result of a RemoteApp drop: the rectangle of the target on the server, compared
    /// with the window under the drop point here, shows whether the point was right.
    /// </summary>
    private void LogRemoteAppTarget(DropResultMessage result)
    {
        if (result.Id != _railDropId)
        {
            return;
        }

        if (result.TargetWindow?.Rect is not { } rect)
        {
            ClientLog.Info($"RemoteApp drop {result.Id}: the agent gave no rectangle of the target; local window at {DescribeRect(_railDropWindow)}.");
            return;
        }

        var dx = rect.Left - _railDropWindow.Left;
        var dy = rect.Top - _railDropWindow.Top;
        var same = dx == 0 && dy == 0 && rect.Right - _railDropWindow.Right == 0 && rect.Bottom - _railDropWindow.Bottom == 0;
        var line = Invariant($"RemoteApp drop {result.Id}: the target is at {rect} on the server and the window under the drop point at {DescribeRect(_railDropWindow)} here: {(same ? "the same rectangle" : $"they differ by ({dx}, {dy}) at the top left corner")}.");
        if (same)
        {
            ClientLog.Info(line);
        }
        else
        {
            ClientLog.Warn(line);
        }
    }

    /// <summary>Where the progress window of a RemoteApp drop goes: over the window dropped on, or the monitor of the cursor.</summary>
    private Rectangle RemoteAppDropArea() =>
        _railDropWindow.IsEmpty ? Screen.FromPoint(Cursor.Position).WorkingArea : _railDropWindow;

    private static string DescribeRect(Rectangle rect) => Invariant($"({rect.Left}, {rect.Top}) to ({rect.Right}, {rect.Bottom})");
}
