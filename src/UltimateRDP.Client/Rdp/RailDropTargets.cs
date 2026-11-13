using System.Globalization;
using System.Runtime.InteropServices;
using UltimateRDP.Client.Logging;
using UltimateRDP.Core.RemoteApp;

namespace UltimateRDP.Client.Rdp;

/// <summary>A RemoteApp session whose windows receive drops, as <see cref="RailDropTargets"/> sees it.</summary>
internal interface IRailSession
{
    /// <summary>A short name of the session for the log.</summary>
    string Description { get; }

    /// <summary>Answers a drag over one of its windows at <paramref name="screenPoint"/>.</summary>
    DragDropEffects RailDragOver(IDataObject? data, Point screenPoint, DragDropEffects allowed, int oleKeyState);

    /// <summary>Handles a drop on one of its windows at <paramref name="screenPoint"/>.</summary>
    DragDropEffects RailDrop(IDataObject? data, Point screenPoint, DragDropEffects allowed, int oleKeyState);

    /// <summary>The drag left its windows, or was cancelled.</summary>
    void RailDragLeave();
}

/// <summary>
/// Registers a drop target on the windows that the Remote Desktop control creates in this
/// process for the programs of RemoteApp sessions, and tells which session each window
/// belongs to.
/// </summary>
/// <remarks>
/// <para>
/// In RemoteApp mode every window of the program on the server becomes a top level window
/// of this process. Three sources find them, together: the OnRemoteWindowDisplayed event of
/// the control, which also says which session a window belongs to; a WinEvent hook limited to
/// the windows of this process (never a global hook, and no code goes into another process);
/// and a scan of the top level windows of this process every two seconds, in case the other
/// two miss one. The windows of Windows Forms, dialogs, tooltips and input method windows of
/// this process are left out.
/// </para>
/// <para>
/// One object serves every RemoteApp tab, since all their windows live in the same process.
/// A window found by the hook or the scan belongs to the session of its owner window when
/// that one is known, or to the only RemoteApp session connected; with several sessions and
/// no owner, the drop is refused and the log says so.
/// </para>
/// <para>
/// Since 0.9.0 only the windows of the program count (<see cref="RailWindowRule"/>): the
/// RAIL_WINDOW windows that can be activated and are not the proxy of the agent. The first
/// real run of 0.8.0 registered drop targets on the UIMainClass window of the control and on
/// the proxy of 32 pixels; such a window now gets one log line that says what it is, and no
/// drop target.
/// </para>
/// <para>
/// Every window gets one log line when it is registered: its handle, class, owner thread
/// compared with the UI thread, styles, rectangle, whether it had a drop target and the
/// result of each call. Everything runs on the UI thread.
/// </para>
/// </remarks>
internal static class RailDropTargets
{
    private const int S_OK = 0;
    private const int ScanIntervalMilliseconds = 2000;
    private const int MaxOwnerDepth = 16;

    private static readonly string[] IgnoredClassPrefixes = ["WindowsForms10.", ".NET-", "GDI+"];

    private static readonly HashSet<string> IgnoredClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "#32770",
        "tooltips_class32",
        "IME",
        "MSCTFIME UI",
        "OleMainThreadWndClass",
        "Credential Dialog Xaml Host",
    };

    private static readonly List<IRailSession> Sessions = [];
    private static readonly Dictionary<IntPtr, RailWindow> Windows = [];

    /// <summary>Windows of the process that are not windows of a program, logged once each.</summary>
    private static readonly HashSet<IntPtr> Excluded = [];
    private static NativeMethods.WinEventProc? _hookProc;
    private static IntPtr _hook;
    private static System.Windows.Forms.Timer? _scanTimer;
    private static uint _uiThread;

    /// <summary>Raised on the UI thread when windows of a program came or went, or after each scan.</summary>
    public static event Action? WindowsChanged;

    /// <summary>True when <paramref name="hwnd"/> is a window of a program, see <see cref="RailWindowRule"/>.</summary>
    public static bool IsProgramWindow(IntPtr hwnd) =>
        Windows.ContainsKey(hwnd)
        || (NativeMethods.IsWindow(hwnd) && RailWindowRule.IsProgramWindow(NativeMethods.ClassName(hwnd), NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GwlExStyle).ToInt64(), NativeMethods.WindowText(hwnd)));

    /// <summary>
    /// A RemoteApp session connected: its windows can now receive drops. The first session
    /// installs the hook and the scan.
    /// </summary>
    public static void Attach(IRailSession session)
    {
        if (Sessions.Contains(session))
        {
            return;
        }

        Sessions.Add(session);
        if (Sessions.Count == 1)
        {
            Start();
        }

        ClientLog.Info($"RemoteApp windows: {session.Description} attached, {Sessions.Count} RemoteApp session(s).");
        Scan("a session attached");
    }

    /// <summary>
    /// A RemoteApp session ended: its windows are revoked, and the last session removes the
    /// hook and the scan.
    /// </summary>
    public static void Detach(IRailSession session)
    {
        if (!Sessions.Remove(session))
        {
            return;
        }

        foreach (var window in Windows.Values.Where(window => window.Session == session).ToList())
        {
            Forget(window, revoke: true);
        }

        if (Sessions.Count == 0)
        {
            Stop();
        }

        ClientLog.Info($"RemoteApp windows: {session.Description} detached, {Sessions.Count} RemoteApp session(s) left.");
    }

    /// <summary>
    /// OnRemoteWindowDisplayed of the control of <paramref name="session"/>: the window is
    /// registered when it appears, and forgotten when it goes, without a revoke, since it may
    /// already be destroyed.
    /// </summary>
    public static void OnWindowDisplayed(IRailSession session, IntPtr hwnd, bool displayed, string attribute)
    {
        if (!displayed)
        {
            if (Windows.TryGetValue(hwnd, out var gone))
            {
                gone.Displayed = false;
            }

            return;
        }

        if (Windows.TryGetValue(hwnd, out var known))
        {
            known.Displayed = true;
            if (known.Session is null)
            {
                known.Session = session;
                ClientLog.Info(Invariant($"RemoteApp window 0x{hwnd.ToInt64():X}: now known to belong to {session.Description}."));
            }

            return;
        }

        Consider(hwnd, "OnRemoteWindowDisplayed " + attribute, session, requireVisible: false);
    }

    /// <summary>
    /// The windows of <paramref name="session"/> that still exist and are visible: when there
    /// are any, closing the tab leaves the program running on the server.
    /// </summary>
    public static int WindowCount(IRailSession session)
    {
        Prune();
        return Windows.Values.Count(window => Resolve(window) == session && NativeMethods.IsWindowVisible(window.Hwnd));
    }

    /// <summary>The window of a RemoteApp session under <paramref name="screenPoint"/>, or IntPtr.Zero.</summary>
    public static IntPtr WindowAt(Point screenPoint)
    {
        var hwnd = NativeMethods.TopLevelWindowAt(screenPoint);
        return Windows.ContainsKey(hwnd) ? hwnd : IntPtr.Zero;
    }

    private static void Start()
    {
        _uiThread = NativeMethods.GetCurrentThreadId();
        _hookProc ??= OnWinEvent;
        _hook = NativeMethods.SetWinEventHook(
            NativeMethods.EventObjectCreate,
            NativeMethods.EventObjectShow,
            IntPtr.Zero,
            _hookProc,
            (uint)Environment.ProcessId,
            0,
            NativeMethods.WinEventOutOfContext);
        ClientLog.Info(_hook == IntPtr.Zero
            ? Invariant($"RemoteApp windows: SetWinEventHook for process {Environment.ProcessId} failed (error {Marshal.GetLastPInvokeError()}); the event of the control and the scan remain.")
            : Invariant($"RemoteApp windows: WinEvent hook installed for the windows of process {Environment.ProcessId} only, on UI thread {_uiThread}; scan every {ScanIntervalMilliseconds} ms."));
        _scanTimer = new System.Windows.Forms.Timer { Interval = ScanIntervalMilliseconds };
        _scanTimer.Tick += (_, _) => Scan(null);
        _scanTimer.Start();
    }

    private static void Stop()
    {
        if (_hook != IntPtr.Zero)
        {
            NativeMethods.UnhookWinEvent(_hook);
            _hook = IntPtr.Zero;
        }

        _scanTimer?.Dispose();
        _scanTimer = null;
        foreach (var window in Windows.Values.ToList())
        {
            Forget(window, revoke: true);
        }

        Excluded.Clear();
        ClientLog.Info("RemoteApp windows: no RemoteApp session left; hook and scan stopped.");
    }

    private static void OnWinEvent(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint eventThread, uint eventTime)
    {
        if (idObject != NativeMethods.ObjIdWindow || idChild != 0 || hwnd == IntPtr.Zero)
        {
            return;
        }

        try
        {
            switch (eventType)
            {
                case NativeMethods.EventObjectShow:
                    if (!Windows.ContainsKey(hwnd))
                    {
                        Consider(hwnd, "WinEvent show", null, requireVisible: true);
                    }

                    break;

                case NativeMethods.EventObjectDestroy:
                    if (Windows.TryGetValue(hwnd, out var window))
                    {
                        Forget(window, revoke: false);
                    }

                    break;
            }
        }
        catch (Exception ex)
        {
            // Called by Windows: an error must not leave this callback.
            ClientLog.Error("RemoteApp windows: the WinEvent callback failed.", ex);
        }
    }

    /// <summary>Looks for windows that the event and the hook missed; logs only what is new.</summary>
    private static void Scan(string? reason)
    {
        if (Sessions.Count == 0)
        {
            return;
        }

        Prune();
        var found = 0;
        foreach (var hwnd in NativeMethods.TopLevelWindowsOfThisProcess())
        {
            if (!Windows.ContainsKey(hwnd) && Consider(hwnd, "scan", null, requireVisible: true))
            {
                found++;
            }
        }

        if (found > 0 || reason is not null)
        {
            ClientLog.Info(Invariant($"RemoteApp windows: scan{(reason is null ? string.Empty : " after " + reason)} found {found} new window(s); {Windows.Count} known."));
        }

        WindowsChanged?.Invoke();
    }

    /// <summary>Removes the windows that no longer exist, without a revoke.</summary>
    private static void Prune()
    {
        foreach (var window in Windows.Values.Where(window => !NativeMethods.IsWindow(window.Hwnd)).ToList())
        {
            Windows.Remove(window.Hwnd);
        }

        Excluded.RemoveWhere(hwnd => !NativeMethods.IsWindow(hwnd));
    }

    /// <summary>
    /// Registers the drop target on <paramref name="hwnd"/> when it is a top level window of
    /// this process that may show a RemoteApp program, and logs what happened. Returns true
    /// when the window is now known.
    /// </summary>
    private static bool Consider(IntPtr hwnd, string source, IRailSession? session, bool requireVisible)
    {
        if (!NativeMethods.IsWindow(hwnd) || NativeMethods.GetAncestor(hwnd, NativeMethods.GaRoot) != hwnd)
        {
            if (session is not null)
            {
                ClientLog.Info(Invariant($"RemoteApp window 0x{hwnd.ToInt64():X} from {source}: {(NativeMethods.IsWindow(hwnd) ? "not a top level window" : "no such window")}; ignored."));
            }

            return false;
        }

        var thread = NativeMethods.GetWindowThreadProcessId(hwnd, out var processId);
        var className = NativeMethods.ClassName(hwnd);
        if (processId != (uint)Environment.ProcessId)
        {
            ClientLog.Info(Invariant($"RemoteApp window 0x{hwnd.ToInt64():X} from {source}: class {className} belongs to process {processId}, not to this one; ignored."));
            return false;
        }

        var visible = NativeMethods.IsWindowVisible(hwnd);
        if (session is null && (IsIgnoredClass(className) || (requireVisible && !visible)))
        {
            return false;
        }

        var exStyleOfWindow = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GwlExStyle).ToInt64();
        var title = NativeMethods.WindowText(hwnd);
        var kind = RailWindowRule.Classify(className, exStyleOfWindow, title);
        if (kind != RailWindowKind.Program)
        {
            if (Excluded.Add(hwnd))
            {
                ClientLog.Info(Invariant($"RemoteApp window 0x{hwnd.ToInt64():X} from {source}: class {className}, exstyle 0x{exStyleOfWindow:X8}, visible {visible}, rect {Describe(NativeMethods.WindowRectangle(hwnd))}: {kind}, not a window of the program; no drop target."));
            }

            return false;
        }

        var window = new RailWindow(hwnd, className, source) { Session = session ?? OwnerSession(hwnd), Displayed = session is not null };
        Windows[hwnd] = window;

        Application.OleRequired();
        var hadTarget = NativeMethods.HasDropTarget(hwnd);
        var revoke = hadTarget ? NativeMethods.RevokeDragDrop(hwnd) : S_OK;
        var register = NativeMethods.RegisterDragDrop(hwnd, window.Target);
        window.Registered = register == S_OK;
        var style = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GwlStyle).ToInt64();
        var exStyle = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GwlExStyle).ToInt64();
        var owner = NativeMethods.GetWindow(hwnd, NativeMethods.GwOwner);
        var threadText = thread == _uiThread ? Invariant($"thread {thread}, the UI thread") : Invariant($"thread {thread}, not the UI thread {_uiThread}");
        ClientLog.Info(Invariant(
            $"RemoteApp window 0x{hwnd.ToInt64():X} from {source}: class {className}, {threadText}, style 0x{style:X8} exstyle 0x{exStyle:X8}, visible {visible}, rect {Describe(NativeMethods.WindowRectangle(hwnd))}, owner 0x{owner.ToInt64():X}, session {window.Session?.Description ?? "(not known yet)"}; hadTarget={hadTarget}{(hadTarget ? Invariant($" revoke=0x{revoke:X8}") : string.Empty)} register=0x{register:X8}."));
        WindowsChanged?.Invoke();
        if (!window.Registered)
        {
            ClientLog.Warn(thread == _uiThread
                ? Invariant($"RemoteApp window 0x{hwnd.ToInt64():X} ({className}): RegisterDragDrop failed with 0x{register:X8} on the UI thread; drops on this window cannot be received.")
                : Invariant($"RemoteApp window 0x{hwnd.ToInt64():X} ({className}): RegisterDragDrop failed with 0x{register:X8}, and the window belongs to thread {thread}, not to the UI thread {_uiThread} that registers; drops on this window cannot be received."));
        }

        return true;
    }

    private static bool IsIgnoredClass(string className) =>
        IgnoredClasses.Contains(className) || IgnoredClassPrefixes.Any(prefix => className.StartsWith(prefix, StringComparison.Ordinal));

    /// <summary>The session of the first owner window that is known, up the chain of owners.</summary>
    private static IRailSession? OwnerSession(IntPtr hwnd)
    {
        var owner = hwnd;
        for (var depth = 0; depth < MaxOwnerDepth; depth++)
        {
            owner = NativeMethods.GetWindow(owner, NativeMethods.GwOwner);
            if (owner == IntPtr.Zero)
            {
                return null;
            }

            if (Windows.TryGetValue(owner, out var known) && known.Session is not null)
            {
                return known.Session;
            }
        }

        return null;
    }

    /// <summary>
    /// The session of a window: the one known for it, the one of its owner, or the only
    /// RemoteApp session connected.
    /// </summary>
    private static IRailSession? Resolve(RailWindow window)
    {
        window.Session ??= OwnerSession(window.Hwnd);
        if (window.Session is not null && Sessions.Contains(window.Session))
        {
            return window.Session;
        }

        return Sessions.Count == 1 ? Sessions[0] : null;
    }

    private static void Forget(RailWindow window, bool revoke)
    {
        Windows.Remove(window.Hwnd);
        WindowsChanged?.Invoke();
        if (revoke && window.Registered && NativeMethods.IsWindow(window.Hwnd))
        {
            NativeMethods.RevokeDragDrop(window.Hwnd);
        }
    }

    private static string Describe(Rectangle rect) => Invariant($"({rect.Left}, {rect.Top}) to ({rect.Right}, {rect.Bottom})");

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);

    /// <summary>A window of a RemoteApp program, with its drop target.</summary>
    private sealed class RailWindow
    {
        public RailWindow(IntPtr hwnd, string className, string source)
        {
            Hwnd = hwnd;
            ClassName = className;
            Source = source;
            Target = new RailWindowDropTarget(SessionForDrag);
        }

        public IntPtr Hwnd { get; }

        public string ClassName { get; }

        public string Source { get; }

        public RailWindowDropTarget Target { get; }

        public IRailSession? Session { get; set; }

        public bool Registered { get; set; }

        public bool Displayed { get; set; }

        /// <summary>True once a drag over this window found no session; logged once.</summary>
        public bool NoSessionLogged { get; set; }

        public IRailSession? SessionForDrag()
        {
            var session = Resolve(this);
            if (session is null && !NoSessionLogged)
            {
                NoSessionLogged = true;
                ClientLog.Warn(Invariant($"RemoteApp window 0x{Hwnd.ToInt64():X} ({ClassName}): {Sessions.Count} RemoteApp sessions are connected and none is known to own this window; drops on it are refused."));
            }

            return session;
        }
    }
}
