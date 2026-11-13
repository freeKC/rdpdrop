using UltimateRDP.Client.Localization;
using UltimateRDP.Client.Logging;
using UltimateRDP.Client.Rdp;
using UltimateRDP.Client.Resources;
using UltimateRDP.Client.Sessions;
using UltimateRDP.Core.Launchers;
using UltimateRDP.Core.Local;
using UltimateRDP.Core.RdpFiles;
using UltimateRDP.Core.RemoteApp;

namespace UltimateRDP.Client;

/// <summary>
/// The main window kept out of sight, since 0.9.0: a RemoteApp program started from a .bat
/// file or a shortcut shows only its own windows, with a notification icon, a loader until
/// its first window appears (<see cref="Loader.LoaderForm"/>), and the process exits by itself
/// once its last session ended.
/// </summary>
/// <remarks>
/// <para>
/// The form is never hidden in the sense of Windows Forms: a control whose form was never
/// shown gets no window, and the Remote Desktop control needs one to connect. Out of sight,
/// the form is a tool window, which has no taskbar button and no place in Alt+Tab, far outside
/// every monitor. "Show UltimateRDP" makes it a normal window again on the monitor of the
/// mouse; closing it then puts it back out of sight while RemoteApp programs run.
/// </para>
/// <para>
/// In this mode a session that ends on purpose (<see cref="SessionEnd.IsNormal"/>) ends
/// without a word. Another end shows a message, and a connection that failed before the logon
/// also shows the main window, where its tab says why and can connect again.
/// </para>
/// </remarks>
internal sealed partial class MainForm
{
    /// <summary>Where the form waits out of sight: outside every monitor, as Windows puts minimized windows.</summary>
    private static readonly Point OffScreen = new(-32000, -32000);

    private readonly HashSet<SessionView> _wasConnected = [];
    private ClientTray? _tray;
    private bool _offScreen;
    private bool _exitRequested;
    private bool _messageShowing;

    /// <summary>The owner of message boxes: none while the form is out of sight, since a box centered on it would be too.</summary>
    private IWin32Window? DialogOwner => _offScreen ? null : this;

    protected override CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;
            if (_offScreen)
            {
                parameters.ExStyle = (int)((parameters.ExStyle | NativeMethods.ExToolWindow) & ~NativeMethods.ExAppWindow);
            }

            return parameters;
        }
    }

    protected override bool ShowWithoutActivation => _offScreen;

    /// <summary>True while the window takes the files of a second start: not once it closes.</summary>
    public bool AcceptsRequests => !_closeRequested && !_exitRequested && !IsDisposed;

    /// <summary>The host and the RemoteApp program of a source, for the loader; null when it cannot be read.</summary>
    public static (string Server, string? Program)? Describe(SessionSource source) =>
        RdpFileLoader.TryLoad(source, out _, out var file, out _)
            ? (RdpAddress.Split(file.FullAddress ?? string.Empty).Host, RemoteAppLaunch.FromFile(file, out _)?.DisplayName)
            : null;

    /// <summary>
    /// A second start of the client handed its file or its connection: a RemoteApp program
    /// opens without the main window unless <c>--show</c> asks for it, a desktop shows it
    /// unless <c>--hidden</c> asks otherwise. A visible main window stays visible. UI thread.
    /// </summary>
    public void OpenFromRequest(OpenRequest request)
    {
        if (_closeRequested)
        {
            return;
        }

        var connection = request.Connection ?? ConnectionOptions.None;
        if (request.Path is null && connection.Server is null)
        {
            ClientLog.Info("A second start without a file or a server: the main window shows.");
            ShowMainWindow("a second start without a file");
            return;
        }

        var source = new SessionSource(request.Path, connection);
        var hidden = new LaunchOptions(request.Path, request.Window, request.AskCredentials, null).StartsHidden(Describe(source)?.Program is not null);
        ClientLog.Info($"A second start hands {source} to this one, {(hidden ? "without the main window" : "with the main window")}, ask for the password {request.AskCredentials}.");
        if (hidden)
        {
            EnterTrayMode();
        }
        else
        {
            ShowMainWindow("a second start for a desktop");
        }

        OpenSession(source, request.AskCredentials);
    }

    /// <summary>Starts out of sight with the notification icon. Called by the constructor, before the window exists.</summary>
    private void StartOutOfSight()
    {
        _offScreen = true;
        Bounds = new Rectangle(OffScreen, _initialBounds.Size);
        EnterTrayMode();
        ClientLog.Info("The main window starts out of sight, with a notification icon.");
    }

    /// <summary>The notification icon, created once and kept until the process exits.</summary>
    private void EnterTrayMode()
    {
        _tray ??= new ClientTray(
            () => [.. Sessions.Where(session => !session.IsClosing).Select(session => (session.Title, session.Host))],
            () => ShowMainWindow("the menu of the notification icon"),
            DisconnectAllFromTray,
            ExitFromTray);
    }

    /// <summary>Shows the main window, on the monitor of the mouse when it was out of sight.</summary>
    private void ShowMainWindow(string reason)
    {
        if (_offScreen && IsHandleCreated)
        {
            ClientLog.Info($"The main window shows: {reason}.");
            NativeMethods.ShowWindow(Handle, NativeMethods.SwHide);
            _offScreen = false;
            NativeMethods.ChangeExStyle(Handle, 0, NativeMethods.ExToolWindow);
            Bounds = InitialBounds();
            NativeMethods.ShowWindow(Handle, NativeMethods.SwShow);
        }
        else if (_offScreen)
        {
            _offScreen = false;
            Bounds = _initialBounds;
        }

        if (WindowState == FormWindowState.Minimized)
        {
            WindowState = FormWindowState.Normal;
        }

        Activate();
        PlaceLoaders();
    }

    /// <summary>Puts the main window out of sight again, while RemoteApp programs run.</summary>
    private void HideMainWindow(string reason)
    {
        if (_offScreen || !IsHandleCreated)
        {
            return;
        }

        ClientLog.Info($"The main window goes out of sight: {reason}.");
        if (WindowState != FormWindowState.Normal)
        {
            WindowState = FormWindowState.Normal;
        }

        NativeMethods.ShowWindow(Handle, NativeMethods.SwHide);
        _offScreen = true;
        NativeMethods.ChangeExStyle(Handle, NativeMethods.ExToolWindow, NativeMethods.ExAppWindow);
        Bounds = new Rectangle(OffScreen, Size);
        NativeMethods.ShowWindow(Handle, NativeMethods.SwShowNoActivate);
        PlaceLoaders();
    }

    /// <summary>
    /// In the mode of the notification icon, the close box of the main window puts it out of
    /// sight while RemoteApp programs run, rather than ending them. True when it did.
    /// </summary>
    private bool HideInsteadOfClosing(FormClosingEventArgs e)
    {
        if (_tray is null || _exitRequested || _closeRequested || e.CloseReason != CloseReason.UserClosing
            || !Sessions.Any(session => session.IsRemoteApp && session.State != SessionState.Disconnected))
        {
            return false;
        }

        e.Cancel = true;
        HideMainWindow("the close box, while RemoteApp programs run");
        return true;
    }

    private void DisconnectAllFromTray()
    {
        ClientLog.Info("Disconnect chosen in the menu of the notification icon.");
        foreach (var session in Sessions.ToList())
        {
            session.BeginClose();
        }
    }

    private void ExitFromTray()
    {
        ClientLog.Info("Exit chosen in the menu of the notification icon.");
        _exitRequested = true;
        Close();
    }

    /// <summary>
    /// Follows the end of each connection out of sight: a desktop that ended on purpose closes
    /// its tab, an end that was not on purpose shows a message, and a connection that failed
    /// before the logon also shows the main window. Called at every change of state.
    /// </summary>
    private void OnSessionStateForTray(SessionView session)
    {
        _tray?.UpdateText();
        if (session.State != SessionState.Disconnected)
        {
            _wasConnected.Add(session);
            return;
        }

        if (session.ConnectFailed && _tray is not null && _offScreen && !_closeRequested)
        {
            // The control refused the connection before it started: nothing would come.
            session.ConnectFailed = false;
            FinishLoader(session, "the session ended");
            BeginInvoke(() => ShowEndMessage(session));
            return;
        }

        if (!_wasConnected.Remove(session))
        {
            return;
        }

        FinishLoader(session, "the session ended");
        if (_tray is null || !_offScreen || _closeRequested || session.IsClosing)
        {
            return;
        }

        if (!session.LastEndWasNormal)
        {
            BeginInvoke(() => ShowEndMessage(session));
        }
        else if (!session.IsRemoteApp || !session.LastEndWasAfterLogon)
        {
            ClientLog.Info($"The session of {session.Title} ended on purpose while the main window is out of sight; its tab closes.");
            BeginInvoke(session.BeginClose);
        }
    }

    private void ShowEndMessage(SessionView session)
    {
        if (_messageShowing)
        {
            // Never a stack of messages: the tab keeps the reason of this one.
            ClientLog.Info($"The session of {session.Title} ended while a message already shows; no second message, the tab keeps the reason: {session.ConnectionText}");
            return;
        }

        ClientLog.Info($"The session of {session.Title} ended while the main window is out of sight: {session.ConnectionText}");
        _messageShowing = true;
        try
        {
            MessageBox.Show(
                LocalText.Of(nameof(Strings.SessionEndedMessage), session.Title, session.ConnectionText).ToString(),
                AppInfo.Name,
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
        finally
        {
            _messageShowing = false;
        }

        if (!session.LastEndWasAfterLogon && !session.IsClosing && session.Parent is not null)
        {
            ShowMainWindow("the connection failed before the logon");
        }

        ExitIfIdle();
    }

    /// <summary>Out of sight with no tab left, the process exits; never while a message waits for the user.</summary>
    private void ExitIfIdle()
    {
        if (_tray is null || !_offScreen || _closeRequested || _messageShowing || _tabs.TabCount > 0)
        {
            return;
        }

        ClientLog.Info("No session is left while the main window is out of sight: the client exits.");
        _exitRequested = true;
        BeginInvoke(Close);
    }
}
