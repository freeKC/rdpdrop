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
using UltimateRDP.Core.RemoteApp;
using UltimateRDP.Core.Transfer;

namespace UltimateRDP.Client.Sessions;

/// <summary>
/// A tab that runs a RemoteApp program: the windows of the program are windows of this
/// process, outside the tab, and the tab shows a <see cref="RemoteAppPanel"/> instead of the
/// session.
/// </summary>
/// <remarks>
/// <para>
/// The control gets RemoteProgramMode and the RemoteProgram2 values before Connect, and the
/// container handles full screen. Since 0.9.0 the client calls ServerStartProgram at the
/// logon, unless the control already reported a result or a window of the program, and once
/// more ten seconds later when nothing came (<see cref="RemoteProgramStart"/>). The window of
/// the control is hidden, as mstsc keeps its own frame hidden in RemoteApp mode: only the
/// windows of the program show.
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
    /// <summary>The memory of the process is logged this long after the first window of the program.</summary>
    private const int MemoryLogMilliseconds = 60_000;

    /// <summary>Longest time the local drop call stays open for the replay in the session.</summary>
    private const int DropCallMilliseconds = 6000;

    private const int PumpWaitMilliseconds = 50;

    private readonly System.Windows.Forms.Timer _startTimer = new() { Interval = RemoteProgramStart.RetryMilliseconds };
    private readonly System.Windows.Forms.Timer _memoryTimer = new() { Interval = MemoryLogMilliseconds };
    private readonly RemoteProgramStart _start = new();
    private RemoteAppLaunch? _remoteApp;
    private RemoteAppPanel? _panel;
    private bool _remoteWindowShown;
    private bool _remoteAppLoggedOn;
    private bool _railAttached;
    private string? _railDropId;
    private Rectangle _railDropWindow;
    private IReadOnlyList<string>? _railHoverFiles;
    private LocalText? _railHoverProblem;

    /// <summary>Raised once per connection, when the first window of the program appears.</summary>
    public event EventHandler? RemoteProgramShown;

    /// <summary>True when the tab runs a RemoteApp program.</summary>
    public bool IsRemoteApp => _remoteApp is not null;

    /// <summary>The name of the RemoteApp program, or null for a desktop.</summary>
    public string? RemoteAppName => _remoteApp?.DisplayName;

    /// <summary>The windows of the program open on this computer.</summary>
    public int RemoteWindowCount => IsRemoteApp ? RailDropTargets.WindowCount(this) : 0;

    /// <summary>True once a window of the program appeared in this connection.</summary>
    public bool RemoteProgramHasShown => _remoteWindowShown;

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
        _panel.DisconnectRequested += (_, _) => Disconnect();
        Controls.Add(_panel);
        _panel.BringToFront();
        _rdp.OnRemoteProgramResult += (_, e) => OnRemoteProgramResult(e.bstrRemoteProgram, e.lError, e.vbIsExecutable);
        _rdp.OnRemoteProgramDisplayed += (_, e) => OnRemoteProgramDisplayed(e.vbDisplayed, e.uDisplayInformation);
        _rdp.OnRemoteWindowDisplayed += (_, e) => OnRemoteWindowDisplayed(e.vbDisplayed, e.hwnd, e.windowAttribute);
        _rdp.OnRequestGoFullScreen += (_, _) => ClientLog.Info($"Event OnRequestGoFullScreen ({Host}) in RemoteApp mode, ignored: the windows of the control stay in the hidden client window.");
        _rdp.OnRequestLeaveFullScreen += (_, _) => ClientLog.Info($"Event OnRequestLeaveFullScreen ({Host}) in RemoteApp mode, ignored.");
        _rdp.OnRequestContainerMinimize += (_, _) => ClientLog.Info($"Event OnRequestContainerMinimize ({Host}) in RemoteApp mode, ignored.");
        _rdp.HandleCreated += (_, _) => HideControlWindow("its window was created");
        RailDropTargets.WindowsChanged += OnRailWindowsChanged;
        _startTimer.Tick += (_, _) => OnStartRetry();
        _memoryTimer.Tick += (_, _) =>
        {
            _memoryTimer.Stop();
            LogMemory(Invariant($"{MemoryLogMilliseconds / 1000} s after the first window"));
        };
        ClientLog.Info($"RemoteApp tab for {Host}: {_remoteApp}.");
        UpdatePanel();
    }

    private void DisposeRemoteApp()
    {
        _startTimer.Dispose();
        _memoryTimer.Dispose();
        RailDropTargets.WindowsChanged -= OnRailWindowsChanged;
        DetachRail();
    }

    /// <summary>A new connection starts: what the previous one saw is forgotten.</summary>
    private void OnRemoteAppConnecting()
    {
        _start.Reset();
        _remoteWindowShown = false;
        _remoteAppLoggedOn = false;
        _startTimer.Stop();
        _memoryTimer.Stop();
        HideControlWindow("a connection starts");
    }

    /// <summary>
    /// Hides the window of the control, as mstsc keeps its frame hidden in RemoteApp mode: the
    /// tab shows the panel, and the control never shows its empty picture with scroll bars. The
    /// windows of the program are top level windows of their own and do not depend on it.
    /// </summary>
    private void HideControlWindow(string reason)
    {
        if (!IsRemoteApp || !_rdp.IsHandleCreated)
        {
            return;
        }

        var wasVisible = NativeMethods.IsWindowVisible(_rdp.Handle);
        NativeMethods.ShowWindow(_rdp.Handle, NativeMethods.SwHide);
        if (wasVisible)
        {
            ClientLog.Info($"RemoteApp ({Host}): the window of the control 0x{_rdp.Handle.ToInt64():X} is hidden, {reason}.");
        }
    }

    private void OnRailWindowsChanged()
    {
        if (!IsDisposed)
        {
            UpdatePanel();
        }
    }

    private void OnRemoteAppConnected()
    {
        HideControlWindow("the session connected");
        if (!_railAttached)
        {
            _railAttached = true;
            RailDropTargets.Attach(this);
        }
    }

    private void OnRemoteAppLoggedOn()
    {
        _remoteAppLoggedOn = true;
        HideControlWindow("the session logged on");
        LogMonitorLayout("logged on");
        LogProcessWindows("logged on");
        LogMemory("logged on");
        if (_start.OnLoggedOn() == RemoteProgramStep.CallServerStartProgram)
        {
            StartProgram("at the logon, since the RemoteProgram2 values alone do not start it on every server");
            _startTimer.Stop();
            _startTimer.Start();
        }
        else
        {
            ClientLog.Info($"RemoteApp ({Host}): ServerStartProgram is not called at the logon: the control already reported {(_start.Answered ? "the result of a start" : "a window of the program")}.");
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
        _memoryTimer.Stop();
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
        _start.OnResult();
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

        if (hwnd != IntPtr.Zero)
        {
            RailDropTargets.OnWindowDisplayed(this, hwnd, displayed, attribute.ToString());
        }

        if (displayed && !_remoteWindowShown && hwnd != IntPtr.Zero && RailDropTargets.IsProgramWindow(hwnd))
        {
            _start.OnWindow();
            _startTimer.Stop();
            _remoteWindowShown = true;
            LogMonitorLayout("first window");
            LogProcessWindows("first window of the program");
            LogMemory("first window");
            _memoryTimer.Stop();
            _memoryTimer.Start();
            RemoteProgramShown?.Invoke(this, EventArgs.Empty);
        }

        UpdatePanel();
        RaiseStateChanged();
    }

    /// <summary>
    /// Ten seconds after the first call, the control neither reported the result of a start
    /// nor showed a window of the program: the client asks the server once more.
    /// </summary>
    private void OnStartRetry()
    {
        _startTimer.Stop();
        if (State != SessionState.Connected)
        {
            return;
        }

        if (_start.OnRetryTimer() == RemoteProgramStep.CallServerStartProgram)
        {
            StartProgram(Invariant($"again: no OnRemoteProgramResult and no window {RemoteProgramStart.RetryMilliseconds / 1000} s after the first call"));
        }
    }

    /// <summary>Calls ServerStartProgram with the program, the file and the arguments of the .rdp file.</summary>
    private void StartProgram(string why)
    {
        if (_remoteApp is not { } launch)
        {
            return;
        }

        var program = launch.Program ?? string.Empty;
        var file = launch.File ?? string.Empty;
        var args = launch.Args ?? string.Empty;
        ClientLog.Info(Invariant($"RemoteApp ({Host}): calling ServerStartProgram(\"{program}\", \"{file}\", \"\", {launch.ExpandWorkingDir}, \"{args}\", {launch.ExpandArgs}), call {_start.Calls} of at most {RemoteProgramStart.MaxCalls}, {why}."));
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
            LocalText.Of(nameof(Strings.RemoteAppWindowCount), RemoteWindowCount).ToString(),
            ConnectionText,
            AgentText,
            DropText,
            canDisconnect: State != SessionState.Disconnected && !_closeRequested);
    }

    /// <summary>
    /// One line per top level window of the process: class, title, visible, cloaked, styles,
    /// rectangle, owner and what <see cref="RailWindowRule"/> makes of it, so that a real run
    /// shows which windows the user can see. The title is cut at 40 characters.
    /// </summary>
    private void LogProcessWindows(string reason)
    {
        var windows = NativeMethods.TopLevelWindowsOfThisProcess();
        var form = FindForm();
        ClientLog.Info(Invariant($"RemoteApp ({Host}): {windows.Count} top level window(s) of the process, {reason}; the control is 0x{(_rdp.IsHandleCreated ? _rdp.Handle.ToInt64() : 0):X}, visible {_rdp.IsHandleCreated && NativeMethods.IsWindowVisible(_rdp.Handle)}; the main window is 0x{(form is { IsHandleCreated: true } ? form.Handle.ToInt64() : 0):X}."));
        foreach (var hwnd in windows)
        {
            var className = NativeMethods.ClassName(hwnd);
            var title = NativeMethods.WindowText(hwnd);
            if (title.Length > 40)
            {
                title = title[..40] + "...";
            }

            var exStyle = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GwlExStyle).ToInt64();
            var style = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GwlStyle).ToInt64();
            ClientLog.Info(Invariant($"RemoteApp ({Host}) window 0x{hwnd.ToInt64():X}: class {className}, title \"{title}\", visible {NativeMethods.IsWindowVisible(hwnd)}, cloaked {NativeMethods.IsCloaked(hwnd)}, style 0x{style:X8} exstyle 0x{exStyle:X8}, rect {DescribeRect(NativeMethods.WindowRectangle(hwnd))}, owner 0x{NativeMethods.GetWindow(hwnd, NativeMethods.GwOwner).ToInt64():X}, {RailWindowRule.Classify(className, exStyle, title)}."));
        }
    }

    /// <summary>The memory of the process, to compare with the 98 MB at launch and 721 MB a minute after the first window that 0.8.0 showed.</summary>
    private void LogMemory(string reason)
    {
        using var process = Process.GetCurrentProcess();
        ClientLog.Info(Invariant($"RemoteApp ({Host}) memory, {reason}: working set {process.WorkingSet64 / 1_000_000} MB, private {process.PrivateMemorySize64 / 1_000_000} MB, managed heap {GC.GetTotalMemory(false) / 1_000_000} MB; desktop {_rdp.DesktopWidth}x{_rdp.DesktopHeight}."));
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
