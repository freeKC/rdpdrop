using System.Globalization;
using System.Runtime.InteropServices;
using RdpDrop.Client.Channel;
using RdpDrop.Client.Logging;
using RdpDrop.Client.Rdp;
using RdpDrop.Client.Settings;
using RdpDrop.Core.RdpFiles;

namespace RdpDrop.Client.Sessions;

/// <summary>
/// One Remote Desktop session: the control, the settings of its .rdp file, the display
/// logic, the agent link and the drop handling. The host shows the texts this view
/// publishes and calls its operations; it knows nothing of the control.
/// </summary>
/// <remarks>
/// Every member is called on the UI thread, like the control itself.
/// </remarks>
internal sealed partial class SessionView : UserControl
{
    private const int CloseTimeoutMilliseconds = 5000;

    private readonly RdpControl _rdp = new();
    private readonly ChildDropTargets _childTargets;
    private readonly AgentLink _agent;
    private readonly DisplayController _display;

    private bool _channelCreated;
    private bool _closeRequested;
    private bool _closeReported;
    private bool _reconnectRequested;
    private System.Windows.Forms.Timer? _closeTimer;

    /// <summary>
    /// Creates the view for <paramref name="file"/>, read from <paramref name="rdpPath"/>.
    /// The display choices start from <paramref name="settings"/>; the keys of the .rdp
    /// file win over them when present.
    /// </summary>
    public SessionView(string rdpPath, RdpFile file, ClientSettings settings)
    {
        RdpPath = rdpPath;
        RdpFile = file;
        Host = RdpAddress.Split(file.FullAddress ?? string.Empty).Host;
        _childTargets = new ChildDropTargets(_rdp);
        _agent = new AgentLink(_rdp);
        _display = new DisplayController(_rdp, this, settings.DisplayMode);
        InitializeViewChoices(settings);

        Dock = DockStyle.Fill;
        ((System.ComponentModel.ISupportInitialize)_rdp).BeginInit();
        _rdp.Dock = DockStyle.Fill;
        Controls.Add(_rdp);
        ((System.ComponentModel.ISupportInitialize)_rdp).EndInit();
        _rdp.AllowDrop = true;

        _rdp.OnConnecting += (_, _) => OnRdpConnecting();
        _rdp.OnConnected += (_, _) => OnRdpConnected();
        _rdp.OnLoginComplete += (_, _) => OnRdpLoginComplete();
        _rdp.OnDisconnected += (_, e) => OnRdpDisconnected(e.discReason);
        _rdp.OnFatalError += (_, e) => OnRdpFatalError(e.errorCode);
        _rdp.OnWarning += (_, e) => ClientLog.Warn(Invariant($"Control warning {e.warningCode} ({Host})"));
        _rdp.OnLogonError += (_, e) => ClientLog.Warn(Invariant($"Logon error {e.lError} ({Host})"));
        _rdp.OnRemoteDesktopSizeChange += (_, e) => _display.OnRemoteDesktopSizeChange(e.width, e.height);
        _rdp.OnAutoReconnecting2 += (_, e) => OnAutoReconnecting(e.disconnectReason, e.attemptCount);
        _rdp.OnAutoReconnected += (_, _) => OnAutoReconnected();
        _rdp.OnEnterFullScreenMode += (_, _) => OnFullScreenChanged(true);
        _rdp.OnLeaveFullScreenMode += (_, _) => OnFullScreenChanged(false);
        _rdp.FileDragOver += OnFileDragOver;
        _rdp.FileDropped += OnFileDropped;
        _rdp.FileDragLeft += (_, _) => OnFileDragLeft();
        _agent.StateChanged += (_, _) => OnAgentStateChanged();
        _agent.DropResultReceived += (_, result) => OnDropResult(result);
        InitializeDrops();

        ClientLog.Info($"Session view created for {Host} from {rdpPath}");
        if (!file.RequestsDriveRedirection)
        {
            ShowDropStatus("This .rdp file does not redirect drives: drops will not work.");
        }
    }

    /// <summary>Raised when <see cref="State"/> or anything shown in the menus changes.</summary>
    public event EventHandler? StateChanged;

    /// <summary>Raised when <see cref="ConnectionText"/> changes.</summary>
    public event EventHandler? ConnectionTextChanged;

    /// <summary>Raised when <see cref="AgentText"/> changes.</summary>
    public event EventHandler? AgentTextChanged;

    /// <summary>Raised when <see cref="DropText"/> changes.</summary>
    public event EventHandler? DropTextChanged;

    /// <summary>
    /// Raised once after <see cref="BeginClose"/>, when the session is disconnected or
    /// the control did not answer in time. The host then removes and disposes the view.
    /// </summary>
    public event EventHandler? CloseCompleted;

    /// <summary>Full path of the .rdp file.</summary>
    public string RdpPath { get; }

    public RdpFile RdpFile { get; }

    /// <summary>Host name from the full address of the .rdp file, without the port.</summary>
    public string Host { get; }

    /// <summary>Short name of the session, for a tab.</summary>
    public string Title => Host;

    /// <summary>Title of the window while this session is the active one.</summary>
    public string WindowTitle => $"{AppInfo.Name}: {RdpFile.FullAddress}";

    public SessionState State { get; private set; } = SessionState.Disconnected;

    public bool IsConnected => State == SessionState.Connected;

    /// <summary>True once <see cref="BeginClose"/> was called.</summary>
    public bool IsClosing => _closeRequested;

    public string ConnectionText { get; private set; } = "Not connected";

    public string AgentText { get; private set; } = "Agent: not connected";

    public string DropText { get; private set; } = string.Empty;

    /// <summary>True when "Look for the agent again" makes sense.</summary>
    public bool CanLookForAgent => IsConnected && _agent.State != AgentState.Ready;

    /// <summary>
    /// True when the keyboard is in the session: the control has the focus or runs full
    /// screen. Shortcuts of the host then belong to the remote computer.
    /// </summary>
    public bool HasSessionKeyboard => State != SessionState.Disconnected && (_rdp.ContainsFocus || IsFullScreen);

    /// <summary>
    /// Starts a connection with the current settings of the .rdp file and the display
    /// choices. Does nothing unless disconnected.
    /// </summary>
    public void Connect()
    {
        if (State != SessionState.Disconnected || _closeRequested)
        {
            return;
        }

        try
        {
            _reconnectRequested = false;
            var options = _display.PrepareConnection(_scaleChoice, _keyboardHookMode, _useAllMonitors, WindowTitle);
            var desktop = RdpSettingsApplier.Apply(_rdp, RdpFile, options, createChannel: !_channelCreated);
            ApplySavedCredentials();
            _display.OnConnecting(desktop);
            _sessionUsesAllMonitors = _useAllMonitors;
            _channelCreated = true;
            SetConnection(SessionState.Connecting, $"Connecting to {Host}...");
            ClientLog.Info($"Connecting to {Host} with {RdpPath}, control version {SafeControlVersion()}");
            _rdp.Connect();
        }
        catch (Exception ex) when (ex is COMException or ArgumentException or InvalidOperationException or AxHost.InvalidActiveXStateException)
        {
            ClientLog.Error($"Connect to {Host} failed", ex);
            SetConnection(SessionState.Disconnected, $"Cannot connect: {ex.Message}");
        }
    }

    public void Disconnect()
    {
        if (State == SessionState.Disconnected)
        {
            return;
        }

        try
        {
            ClientLog.Info($"Disconnect from {Host} requested.");
            SetConnectionText("Disconnecting...");
            _rdp.Disconnect();
        }
        catch (Exception ex) when (ex is COMException or AxHost.InvalidActiveXStateException)
        {
            ClientLog.Error($"Disconnect from {Host} failed", ex);
        }
    }

    /// <summary>
    /// Disconnects and raises <see cref="CloseCompleted"/> once the session is closed, or
    /// after five seconds when the control does not answer, for example while its
    /// credential prompt is open.
    /// </summary>
    public void BeginClose()
    {
        if (_closeRequested)
        {
            return;
        }

        _closeRequested = true;
        if (State == SessionState.Disconnected)
        {
            ReportClosed();
            return;
        }

        ClientLog.Info($"Closing the session to {Host}, disconnecting first.");
        _closeTimer = new System.Windows.Forms.Timer { Interval = CloseTimeoutMilliseconds };
        _closeTimer.Tick += (_, _) =>
        {
            ClientLog.Warn($"No disconnect event from {Host} after five seconds, closing anyway.");
            State = SessionState.Disconnected;
            ReportClosed();
        };
        _closeTimer.Start();
        Disconnect();
    }

    /// <summary>Starts a new agent discovery, from the menu.</summary>
    public void LookForAgent() => _agent.RestartDiscovery("menu");

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            ClientLog.Info($"Session view for {Host} disposed.");
            _closeTimer?.Dispose();
            _childTargets.RevokeAll();
            DisposeDrops();
            _agent.Dispose();
            _display.Dispose();
        }

        base.Dispose(disposing);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);

        // The base constructor resizes the view before the controller exists.
        _display?.OnWindowResized();
    }

    private void ReportClosed()
    {
        _closeTimer?.Stop();
        if (_closeReported)
        {
            return;
        }

        _closeReported = true;
        CloseCompleted?.Invoke(this, EventArgs.Empty);
    }

    private void OnRdpConnecting()
    {
        ClientLog.Info($"Event OnConnecting ({Host})");
        SetConnection(SessionState.Connecting, $"Connecting to {Host}...");
    }

    private void OnRdpConnected()
    {
        ClientLog.Info($"Event OnConnected ({Host})");
        SetConnection(SessionState.Connected, $"Connected to {Host}");
        _childTargets.Refresh("connected");
        _agent.Start();
    }

    private void OnRdpLoginComplete()
    {
        ClientLog.Info($"Event OnLoginComplete ({Host})");
        SetConnection(SessionState.Connected, $"Logged on to {Host}");
        _childTargets.Refresh("logged on");
        _display.OnLoggedOn();
        _agent.RestartDiscovery("logged on");
    }

    private void OnRdpDisconnected(int reason)
    {
        var extended = SafeExtendedReason();
        var text = SafeErrorDescription(reason, extended);
        ClientLog.Info(Invariant($"Event OnDisconnected ({Host}) reason={reason} extended={extended} text=\"{text}\""));
        _agent.Stop();
        _display.OnDisconnected();
        ResetDrops();
        ShowFullScreenState(false);
        SetConnection(SessionState.Disconnected, DescribeDisconnect(reason, text));

        if (_closeRequested)
        {
            BeginInvoke(ReportClosed);
        }
        else if (_reconnectRequested)
        {
            _reconnectRequested = false;
            ClientLog.Info("Reconnecting to apply the new display settings.");
            BeginInvoke(Connect);
        }
    }

    /// <summary>
    /// Codes 1 to 3 are normal ends of a session; the control describes code 1 as an
    /// internal error, so they get their own text.
    /// </summary>
    private static string DescribeDisconnect(int reason, string description) => reason switch
    {
        1 => "Disconnected",
        2 => "Disconnected: the session was closed from the remote computer",
        3 => "Disconnected by the remote computer",
        _ when string.IsNullOrWhiteSpace(description) => Invariant($"Disconnected (code {reason})"),
        _ => $"Disconnected: {description.ReplaceLineEndings(" ").Replace("  ", " ", StringComparison.Ordinal).Trim()}",
    };

    private void OnRdpFatalError(int errorCode)
    {
        ClientLog.Error(Invariant($"Event OnFatalError ({Host}) code={errorCode}"));
        _agent.Stop();
        _display.OnDisconnected();
        ResetDrops();
        _reconnectRequested = false;
        ShowFullScreenState(false);
        SetConnection(SessionState.Disconnected, Invariant($"Fatal error {errorCode} in the Remote Desktop control"));
        if (_closeRequested)
        {
            BeginInvoke(ReportClosed);
        }
    }

    private void OnAutoReconnecting(int reason, int attempt)
    {
        ClientLog.Info(Invariant($"Auto reconnecting to {Host}, reason={reason} attempt={attempt}"));
        SetConnectionText(Invariant($"Reconnecting to {Host} (attempt {attempt})..."));
    }

    private void OnAutoReconnected()
    {
        ClientLog.Info($"Auto reconnected to {Host}");
        SetConnection(SessionState.Connected, $"Connected to {Host}");
        _childTargets.Refresh("reconnected");
    }

    private void SetConnection(SessionState state, string text)
    {
        State = state;
        SetConnectionText(text);
        RaiseStateChanged();
    }

    private void SetConnectionText(string text)
    {
        ConnectionText = text;
        ConnectionTextChanged?.Invoke(this, EventArgs.Empty);
    }

    private void RaiseStateChanged() => StateChanged?.Invoke(this, EventArgs.Empty);

    private void OnAgentStateChanged()
    {
        AgentText = _agent.State switch
        {
            AgentState.Waiting => "Agent: waiting",
            AgentState.Ready => $"Agent ready (v{_agent.AgentVersion ?? "?"})",
            AgentState.NotFound => "No agent on the remote computer",
            _ => "Agent: not connected",
        };
        AgentTextChanged?.Invoke(this, EventArgs.Empty);

        if (_agent.State == AgentState.Ready && _agent.DriveRedirectionAvailable == false)
        {
            ShowDropStatus("The session sees no redirected drive: drops will not work. Turn on drives under Local Resources.");
        }

        RaiseStateChanged();
    }

    private void ShowDropStatus(string text)
    {
        DropText = text;
        DropTextChanged?.Invoke(this, EventArgs.Empty);
    }

    private string SafeControlVersion()
    {
        try
        {
            return _rdp.Version;
        }
        catch (Exception ex) when (ex is COMException or AxHost.InvalidActiveXStateException)
        {
            return "unknown";
        }
    }

    private uint SafeExtendedReason()
    {
        try
        {
            return (uint)_rdp.ExtendedDisconnectReason;
        }
        catch (Exception ex) when (ex is COMException or AxHost.InvalidActiveXStateException)
        {
            return 0;
        }
    }

    private string SafeErrorDescription(int reason, uint extended)
    {
        try
        {
            return _rdp.GetErrorDescription((uint)reason, extended);
        }
        catch (Exception ex) when (ex is COMException or AxHost.InvalidActiveXStateException)
        {
            return string.Empty;
        }
    }

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
