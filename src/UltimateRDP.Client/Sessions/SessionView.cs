using System.Globalization;
using System.Runtime.InteropServices;
using UltimateRDP.Client.Channel;
using UltimateRDP.Client.Localization;
using UltimateRDP.Client.Logging;
using UltimateRDP.Client.Rdp;
using UltimateRDP.Client.Resources;
using UltimateRDP.Client.Settings;
using UltimateRDP.Core.RdpFiles;

namespace UltimateRDP.Client.Sessions;

/// <summary>
/// One Remote Desktop session: the control, the settings of its .rdp file, the display
/// logic, the agent link and the drop handling. The host shows the texts this view
/// publishes and calls its operations; it knows nothing of the control.
/// </summary>
/// <remarks>
/// Every member is called on the UI thread, like the control itself. The texts are kept
/// as <see cref="LocalText"/>, so that <see cref="ApplyTexts"/> can show them again in
/// another language.
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
    private LocalText _connectionText = LocalText.Of(nameof(Strings.ConnectionNotConnected));
    private LocalText _dropText = LocalText.Empty;

    /// <summary>The notice shown after a change of monitor DPI, while it is the drop text.</summary>
    private LocalText? _scaleNotice;

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
        _agent = new AgentLink(_rdp, settings.TransferBatchCharacters, settings.TransferWindowBytes);
        _display = new DisplayController(_rdp, this, settings.DisplayMode);
        _transferChunkBytes = settings.TransferChunkBytes;
        _transferWindowBytes = settings.TransferWindowBytes;
        _directTransferMode = settings.DirectTransferMode;
        _directConnections = settings.DirectConnections;
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
        InitializeTransfer();
        InitializeLogOff();
        InitializeRemoteApp();
        InitializeReceive(settings);

        ClientLog.Info($"Session view created for {Host} from {rdpPath}");
        if (!file.RequestsDriveRedirection)
        {
            ShowDropStatus(LocalText.Of(nameof(Strings.FileDoesNotRedirectDrives)));
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

    /// <summary>Short name of the session, for a tab: the host, or the name of the RemoteApp program.</summary>
    public string Title => RemoteAppName ?? Host;

    /// <summary>Title of the window while this session is the active one.</summary>
    public string WindowTitle => RemoteAppName is { } program
        ? $"{AppInfo.Name}: {program} ({RdpFile.FullAddress})"
        : $"{AppInfo.Name}: {RdpFile.FullAddress}";

    public SessionState State { get; private set; } = SessionState.Disconnected;

    public bool IsConnected => State == SessionState.Connected;

    /// <summary>True once <see cref="BeginClose"/> was called.</summary>
    public bool IsClosing => _closeRequested;

    /// <summary>Connection state, in the current user interface language.</summary>
    public string ConnectionText => _connectionText.ToString();

    /// <summary>Agent state, in the current user interface language.</summary>
    public string AgentText => _agent.State switch
    {
        AgentState.Waiting => Strings.AgentWaiting,
        AgentState.Ready => LocalText.Of(nameof(Strings.AgentReady), _agent.AgentVersion ?? "?").ToString(),
        AgentState.NotFound => Strings.AgentNotFound,
        _ => Strings.AgentNotConnected,
    };

    /// <summary>Last drop result or message, in the current user interface language.</summary>
    public string DropText => _dropText.ToString();

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
            if (IsRemoteApp)
            {
                // The windows of the program go where they are on the server, which spans
                // every local monitor; the picture is never scaled.
                options = options with { SmartSizing = false, UseMultimon = true };
                OnRemoteAppConnecting();
            }

            var desktop = RdpSettingsApplier.Apply(_rdp, RdpFile, options, createChannel: !_channelCreated, _remoteApp);
            ApplySavedCredentials();
            _display.OnConnecting(desktop);
            _sessionUsesAllMonitors = _useAllMonitors;
            _channelCreated = true;
            SetConnection(SessionState.Connecting, LocalText.Of(nameof(Strings.ConnectingTo), Host));
            ClientLog.Info($"Connecting to {Host} with {RdpPath}, control version {SafeControlVersion()}");
            _agent.PrepareChannel();
            _rdp.Connect();
        }
        catch (Exception ex) when (ex is COMException or ArgumentException or InvalidOperationException or AxHost.InvalidActiveXStateException)
        {
            ClientLog.Error($"Connect to {Host} failed", ex);
            SetConnection(SessionState.Disconnected, LocalText.Of(nameof(Strings.CannotConnect), ex.Message));
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
            SetConnectionText(LocalText.Of(nameof(Strings.Disconnecting)));
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

        // Before the disconnection, so that the agent can still be told.
        CancelTransferForClose();
        CancelReceiveForClose();

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

    /// <summary>
    /// Shows every text again in the current user interface language, and tells the agent
    /// the new language so that the texts it sends next follow it. Texts that came from
    /// the agent or the control stay as they were written.
    /// </summary>
    public void ApplyTexts()
    {
        _agent.AnnounceLanguage();
        ConnectionTextChanged?.Invoke(this, EventArgs.Empty);
        AgentTextChanged?.Invoke(this, EventArgs.Empty);
        DropTextChanged?.Invoke(this, EventArgs.Empty);
        _progressWindow?.ApplyTexts();
        UpdatePanel();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            ClientLog.Info($"Session view for {Host} disposed.");
            _closeTimer?.Dispose();
            DisposeLogOff();
            _childTargets.RevokeAll();
            DisposeRemoteApp();
            DisposeReceive();
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

    /// <summary>
    /// The window went to a monitor with another DPI. The menu shows the new Auto factor
    /// at once; an open session that uses Auto is told that it applies at the next
    /// connection. Nothing reconnects by itself.
    /// </summary>
    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        if (_display is null || _closeRequested || IsRemoteApp)
        {
            return;
        }

        if (_display.OnDpiChanged(connected: State != SessionState.Disconnected))
        {
            _scaleNotice = LocalText.Of(nameof(Strings.SettingAppliesNextConnection), LocalText.Of(nameof(Strings.SettingScaleFactor)));
            ShowDropStatus(_scaleNotice);
        }
        else if (_dropText == _scaleNotice)
        {
            // Back on a monitor where Auto gives the factor of the session.
            ShowDropStatus(LocalText.Empty);
        }

        RaiseStateChanged();
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
        SetConnection(SessionState.Connecting, LocalText.Of(nameof(Strings.ConnectingTo), Host));
    }

    private void OnRdpConnected()
    {
        ClientLog.Info($"Event OnConnected ({Host})");
        SetConnection(SessionState.Connected, LocalText.Of(nameof(Strings.ConnectedTo), Host));
        _childTargets.Refresh("connected");
        if (IsRemoteApp)
        {
            OnRemoteAppConnected();
        }

        _agent.Start();
    }

    private void OnRdpLoginComplete()
    {
        ClientLog.Info($"Event OnLoginComplete ({Host})");
        SetConnection(SessionState.Connected, LocalText.Of(nameof(Strings.LoggedOnTo), Host));
        _childTargets.Refresh("logged on");
        _display.OnLoggedOn();
        if (IsRemoteApp)
        {
            OnRemoteAppLoggedOn();
        }

        _agent.RestartDiscovery("logged on");
    }

    private void OnRdpDisconnected(int reason)
    {
        var extended = SafeExtendedReason();
        var text = SafeErrorDescription(reason, extended);
        ClientLog.Info(Invariant($"Event OnDisconnected ({Host}) reason={reason} extended={extended} text=\"{text}\""));
        OnReceiveDisconnected();
        _agent.Stop();
        _display.OnDisconnected();
        ResetDrops();
        EndLogOff();
        ShowFullScreenState(false);
        SetConnection(SessionState.Disconnected, DescribeDisconnect(reason, text));
        if (IsRemoteApp)
        {
            OnRemoteAppDisconnected();
        }

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
    /// <remarks>The description comes from the control, in the language of Windows.</remarks>
    private static LocalText DescribeDisconnect(int reason, string description) => reason switch
    {
        1 => LocalText.Of(nameof(Strings.Disconnected)),
        2 => LocalText.Of(nameof(Strings.DisconnectedSessionClosed)),
        3 => LocalText.Of(nameof(Strings.DisconnectedByRemote)),
        _ when string.IsNullOrWhiteSpace(description) => LocalText.Of(nameof(Strings.DisconnectedCode), reason),
        _ => LocalText.Of(nameof(Strings.DisconnectedReason), description.ReplaceLineEndings(" ").Replace("  ", " ", StringComparison.Ordinal).Trim()),
    };

    private void OnRdpFatalError(int errorCode)
    {
        ClientLog.Error(Invariant($"Event OnFatalError ({Host}) code={errorCode}"));
        OnReceiveDisconnected();
        _agent.Stop();
        _display.OnDisconnected();
        ResetDrops();
        EndLogOff();
        _reconnectRequested = false;
        ShowFullScreenState(false);
        SetConnection(SessionState.Disconnected, LocalText.Of(nameof(Strings.FatalError), errorCode));
        if (IsRemoteApp)
        {
            OnRemoteAppDisconnected();
        }

        if (_closeRequested)
        {
            BeginInvoke(ReportClosed);
        }
    }

    private void OnAutoReconnecting(int reason, int attempt)
    {
        ClientLog.Info(Invariant($"Auto reconnecting to {Host}, reason={reason} attempt={attempt}"));
        SetConnectionText(LocalText.Of(nameof(Strings.Reconnecting), Host, attempt));
    }

    private void OnAutoReconnected()
    {
        ClientLog.Info($"Auto reconnected to {Host}");
        SetConnection(SessionState.Connected, LocalText.Of(nameof(Strings.ConnectedTo), Host));
        _childTargets.Refresh("reconnected");
    }

    private void SetConnection(SessionState state, LocalText text)
    {
        State = state;
        SetConnectionText(text);
        RaiseStateChanged();
    }

    private void SetConnectionText(LocalText text)
    {
        _connectionText = text;
        ConnectionTextChanged?.Invoke(this, EventArgs.Empty);
        UpdatePanel();
    }

    private void RaiseStateChanged() => StateChanged?.Invoke(this, EventArgs.Empty);

    private void OnAgentStateChanged()
    {
        AgentTextChanged?.Invoke(this, EventArgs.Empty);
        UpdatePanel();

        if (_agent.State == AgentState.Ready && _agent.DriveRedirectionAvailable == false)
        {
            ShowDropStatus(LocalText.Of(nameof(Strings.SessionSeesNoDrive)));
        }

        RaiseStateChanged();
    }

    private void ShowDropStatus(LocalText text)
    {
        _dropText = text;
        DropTextChanged?.Invoke(this, EventArgs.Empty);
        UpdatePanel();
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
