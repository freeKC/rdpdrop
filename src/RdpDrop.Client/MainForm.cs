using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using RdpDrop.Client.Channel;
using RdpDrop.Client.Logging;
using RdpDrop.Client.Rdp;
using RdpDrop.Core.Paths;
using RdpDrop.Core.Protocol;
using RdpDrop.Core.RdpFiles;

namespace RdpDrop.Client;

internal sealed class MainForm : Form
{
    private readonly string _rdpPath;
    private readonly RdpFile _file;
    private readonly string _host;
    private readonly RdpControl _rdp = new();
    private readonly ChildDropTargets _childTargets;
    private readonly AgentLink _agent;
    private readonly ToolStripMenuItem _connectItem = new("&Connect");
    private readonly ToolStripMenuItem _disconnectItem = new("&Disconnect");
    private readonly ToolStripMenuItem _findAgentItem = new("&Look for the agent again");
    private readonly ToolStripStatusLabel _connectionLabel = new() { Text = "Not connected" };
    private readonly ToolStripStatusLabel _agentLabel = new() { Text = "Agent: not connected" };
    private readonly ToolStripStatusLabel _dropLabel = new() { Spring = true, TextAlign = ContentAlignment.MiddleLeft };

    private ConnectionState _state = ConnectionState.Disconnected;
    private bool _channelCreated;
    private bool _closeRequested;
    private Size _desktopSize;
    private string? _pendingDropId;
    private string? _dropTextBeforeDrag;
    private string _dropStatus = string.Empty;

    public MainForm(string rdpPath, RdpFile file)
    {
        _rdpPath = rdpPath;
        _file = file;
        _host = RdpAddress.Split(file.FullAddress ?? string.Empty).Host;
        _childTargets = new ChildDropTargets(_rdp);
        _agent = new AgentLink(_rdp);

        Text = $"{AppInfo.Name}: {file.FullAddress}";
        StartPosition = FormStartPosition.CenterScreen;
        var area = Screen.PrimaryScreen?.WorkingArea.Size ?? new Size(1280, 800);
        Size = new Size(area.Width * 4 / 5, area.Height * 4 / 5);
        AllowDrop = true;

        var menu = BuildMenu();
        var status = new StatusStrip { ShowItemToolTips = true };
        _connectionLabel.BorderSides = ToolStripStatusLabelBorderSides.Right;
        _agentLabel.BorderSides = ToolStripStatusLabelBorderSides.Right;
        status.Items.AddRange([_connectionLabel, _agentLabel, _dropLabel]);

        ((System.ComponentModel.ISupportInitialize)_rdp).BeginInit();
        _rdp.Dock = DockStyle.Fill;
        Controls.Add(_rdp);
        Controls.Add(status);
        Controls.Add(menu);
        MainMenuStrip = menu;
        ((System.ComponentModel.ISupportInitialize)_rdp).EndInit();
        _rdp.AllowDrop = true;

        _rdp.OnConnecting += (_, _) => OnRdpConnecting();
        _rdp.OnConnected += (_, _) => OnRdpConnected();
        _rdp.OnLoginComplete += (_, _) => OnRdpLoginComplete();
        _rdp.OnDisconnected += (_, e) => OnRdpDisconnected(e.discReason);
        _rdp.OnFatalError += (_, e) => OnRdpFatalError(e.errorCode);
        _rdp.OnWarning += (_, e) => ClientLog.Warn(Invariant($"Control warning {e.warningCode}"));
        _rdp.OnLogonError += (_, e) => ClientLog.Warn(Invariant($"Logon error {e.lError}"));
        _rdp.OnRemoteDesktopSizeChange += (_, e) => OnRemoteDesktopSizeChange(e.width, e.height);
        _rdp.OnAutoReconnecting2 += (_, e) => OnAutoReconnecting(e.disconnectReason, e.attemptCount);
        _rdp.OnAutoReconnected += (_, _) => OnAutoReconnected();
        _rdp.FileDragOver += OnFileDragOver;
        _rdp.FileDropped += OnFileDropped;
        _rdp.FileDragLeft += (_, _) => OnFileDragLeft();
        _agent.StateChanged += (_, _) => OnAgentStateChanged();
        _agent.DropResultReceived += (_, result) => OnDropResult(result);

        ClientLog.Info(Invariant($"Main form created, control version {SafeControlVersion()}"));
        if (!file.RequestsDriveRedirection)
        {
            ShowDropStatus("This .rdp file does not redirect drives: drops will not work.");
        }

        UpdateMenu();
        Shown += (_, _) => Connect();
    }

    private enum ConnectionState
    {
        Disconnected,
        Connecting,
        Connected,
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        ClientLog.Info($"Form closing: reason {e.CloseReason}, state {_state}, close requested {_closeRequested}.");
        if (_state != ConnectionState.Disconnected && !_closeRequested)
        {
            // Disconnect cleanly first; OnDisconnected closes the window.
            _closeRequested = true;
            e.Cancel = true;
            ClientLog.Info("Window closing, disconnecting first.");
            Disconnect();
            var timer = new System.Windows.Forms.Timer { Interval = 5000 };
            timer.Tick += (_, _) =>
            {
                timer.Dispose();
                ClientLog.Warn("No disconnect event after five seconds, closing anyway.");
                _state = ConnectionState.Disconnected;
                Close();
            };
            timer.Start();
            StartExitWatchdog();
            return;
        }

        base.OnFormClosing(e);
    }

    // The form is a drop target too, as a fallback: OLE reaches it when no window under
    // the cursor is registered. It answers through the control, which refuses points
    // outside the session area.
    protected override void OnDragEnter(DragEventArgs drgevent)
    {
        drgevent.Effect = _rdp.ProcessDragOver(drgevent.Data, new Point(drgevent.X, drgevent.Y), drgevent.AllowedEffect);
    }

    protected override void OnDragOver(DragEventArgs drgevent)
    {
        drgevent.Effect = _rdp.ProcessDragOver(drgevent.Data, new Point(drgevent.X, drgevent.Y), drgevent.AllowedEffect);
    }

    protected override void OnDragDrop(DragEventArgs drgevent)
    {
        drgevent.Effect = _rdp.ProcessDrop(drgevent.Data, new Point(drgevent.X, drgevent.Y), drgevent.AllowedEffect);
    }

    protected override void OnDragLeave(EventArgs e)
    {
        _rdp.ProcessDragLeave();
    }

    /// <summary>
    /// The control can block its own teardown, for example while its credential prompt
    /// is open. If the process is still alive ten seconds after the user asked to close,
    /// it exits without waiting for the control.
    /// </summary>
    private static void StartExitWatchdog()
    {
        var watchdog = new Thread(() =>
        {
            Thread.Sleep(TimeSpan.FromSeconds(10));
            ClientLog.Warn("The Remote Desktop control did not let the window close; exiting.");
            Environment.Exit(0);
        })
        {
            IsBackground = true,
            Name = "Exit watchdog",
        };
        watchdog.Start();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        ClientLog.Info("Form closed.");
        _childTargets.RevokeAll();
        _agent.Dispose();
        base.OnFormClosed(e);
    }

    private MenuStrip BuildMenu()
    {
        _connectItem.Click += (_, _) => Connect();
        _disconnectItem.Click += (_, _) => Disconnect();
        _findAgentItem.Click += (_, _) => _agent.RestartDiscovery("menu");
        var quit = new ToolStripMenuItem("&Quit");
        quit.Click += (_, _) => Close();

        var session = new ToolStripMenuItem("&Session");
        session.DropDownItems.AddRange([_connectItem, _disconnectItem, _findAgentItem, new ToolStripSeparator(), quit]);

        var about = new ToolStripMenuItem("&About RdpDrop");
        about.Click += (_, _) => ShowAbout();
        var project = new ToolStripMenuItem("&Project page");
        project.Click += (_, _) => OpenWithShell(AppInfo.ProjectUrl);
        var logs = new ToolStripMenuItem("Open &log folder");
        logs.Click += (_, _) => OpenLogFolder();

        var help = new ToolStripMenuItem("&Help");
        help.DropDownItems.AddRange([about, project, logs]);

        var menu = new MenuStrip();
        menu.Items.AddRange([session, help]);
        return menu;
    }

    private void Connect()
    {
        if (_state != ConnectionState.Disconnected || _closeRequested)
        {
            return;
        }

        try
        {
            _desktopSize = RdpSettingsApplier.Apply(_rdp, _file, createChannel: !_channelCreated);
            _channelCreated = true;
            SetConnection(ConnectionState.Connecting, $"Connecting to {_host}...");
            ClientLog.Info($"Connecting to {_host} with {_rdpPath}");
            _rdp.Connect();
        }
        catch (Exception ex) when (ex is COMException or ArgumentException or InvalidOperationException or AxHost.InvalidActiveXStateException)
        {
            ClientLog.Error("Connect failed", ex);
            SetConnection(ConnectionState.Disconnected, $"Cannot connect: {ex.Message}");
        }
    }

    private void Disconnect()
    {
        if (_state == ConnectionState.Disconnected)
        {
            return;
        }

        try
        {
            ClientLog.Info("Disconnect requested.");
            SetLabel(_connectionLabel, "Disconnecting...");
            _rdp.Disconnect();
        }
        catch (COMException ex)
        {
            ClientLog.Error("Disconnect failed", ex);
        }
    }

    private void OnRdpConnecting()
    {
        ClientLog.Info("Event OnConnecting");
        SetConnection(ConnectionState.Connecting, $"Connecting to {_host}...");
    }

    private void OnRdpConnected()
    {
        ClientLog.Info("Event OnConnected");
        SetConnection(ConnectionState.Connected, $"Connected to {_host}");
        _childTargets.Refresh("connected");
        _agent.Start();
    }

    private void OnRdpLoginComplete()
    {
        ClientLog.Info("Event OnLoginComplete");
        SetConnection(ConnectionState.Connected, $"Logged on to {_host}");
        _childTargets.Refresh("logged on");
        _agent.RestartDiscovery("logged on");
    }

    private void OnRdpDisconnected(int reason)
    {
        var extended = SafeExtendedReason();
        var text = SafeErrorDescription(reason, extended);
        ClientLog.Info(Invariant($"Event OnDisconnected reason={reason} extended={extended} text=\"{text}\""));
        _agent.Stop();
        _pendingDropId = null;
        SetConnection(ConnectionState.Disconnected, DescribeDisconnect(reason, text));

        if (_closeRequested)
        {
            BeginInvoke(Close);
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
        ClientLog.Error(Invariant($"Event OnFatalError code={errorCode}"));
        _agent.Stop();
        SetConnection(ConnectionState.Disconnected, Invariant($"Fatal error {errorCode} in the Remote Desktop control"));
    }

    private void OnRemoteDesktopSizeChange(int width, int height)
    {
        ClientLog.Info(Invariant($"Remote desktop size is now {width}x{height}"));
        if (width > 0 && height > 0)
        {
            _desktopSize = new Size(width, height);
        }
    }

    private void OnAutoReconnecting(int reason, int attempt)
    {
        ClientLog.Info(Invariant($"Auto reconnecting, reason={reason} attempt={attempt}"));
        SetLabel(_connectionLabel, Invariant($"Reconnecting to {_host} (attempt {attempt})..."));
    }

    private void OnAutoReconnected()
    {
        ClientLog.Info("Auto reconnected");
        SetConnection(ConnectionState.Connected, $"Connected to {_host}");
        _childTargets.Refresh("reconnected");
    }

    private void SetConnection(ConnectionState state, string text)
    {
        _state = state;
        SetLabel(_connectionLabel, text);
        UpdateMenu();
    }

    private void UpdateMenu()
    {
        _connectItem.Enabled = _state == ConnectionState.Disconnected;
        _disconnectItem.Enabled = _state != ConnectionState.Disconnected;
        _findAgentItem.Enabled = _state == ConnectionState.Connected && _agent.State != AgentState.Ready;
    }

    private void ShowDropStatus(string text)
    {
        _dropStatus = text;
        SetLabel(_dropLabel, text);
    }

    /// <summary>
    /// Shows a text in a status label, shortened when long; the tooltip keeps all of it.
    /// </summary>
    private static void SetLabel(ToolStripStatusLabel label, string text)
    {
        const int MaxLength = 110;
        label.ToolTipText = text;
        label.Text = text.Length <= MaxLength ? text : string.Concat(text.AsSpan(0, MaxLength - 3), "...");
    }

    private void OnAgentStateChanged()
    {
        _agentLabel.Text = _agent.State switch
        {
            AgentState.Waiting => "Agent: waiting",
            AgentState.Ready => $"Agent ready (v{_agent.AgentVersion ?? "?"})",
            AgentState.NotFound => "No agent on the remote computer",
            _ => "Agent: not connected",
        };

        if (_agent.State == AgentState.Ready && _agent.DriveRedirectionAvailable == false)
        {
            ShowDropStatus("The session sees no redirected drive: drops will not work. Turn on drives under Local Resources.");
        }

        UpdateMenu();
    }

    /// <summary>
    /// Why a drop cannot be sent now, or null when it can.
    /// </summary>
    private string? DropRefusalReason()
    {
        if (_state != ConnectionState.Connected)
        {
            return "Drop refused: not connected.";
        }

        return _agent.State switch
        {
            AgentState.Ready => null,
            AgentState.Waiting => "Drop refused: the RdpDrop agent has not answered yet.",
            _ => "Drop refused: the RdpDrop agent is not running in the remote session.",
        };
    }

    private void OnFileDragOver(object? sender, FileDragEventArgs e)
    {
        _dropTextBeforeDrag ??= _dropStatus;
        var reason = DropRefusalReason();
        if (reason is null)
        {
            e.Effect = DragDropEffects.Copy;
            ShowDropStatus(_dropTextBeforeDrag);
        }
        else
        {
            e.Effect = DragDropEffects.None;
            ShowDropStatus(reason);
        }
    }

    private void OnFileDragLeft()
    {
        if (_dropTextBeforeDrag is not null)
        {
            ShowDropStatus(_dropTextBeforeDrag);
            _dropTextBeforeDrag = null;
        }
    }

    private void OnFileDropped(object? sender, FileDragEventArgs e)
    {
        _dropTextBeforeDrag = null;
        var reason = DropRefusalReason();
        if (reason is not null)
        {
            ClientLog.Info(Invariant($"Drop of {e.Files.Count} item(s) refused: {reason}"));
            ShowDropStatus(reason);
            return;
        }

        var items = new List<DropItem>(e.Files.Count);
        foreach (var path in e.Files)
        {
            if (!ClientPathMapper.TryMapToTsClient(path, out _, out var pathProblem))
            {
                ClientLog.Info($"Drop refused: {pathProblem}");
                ShowDropStatus($"Drop refused: {pathProblem}");
                return;
            }

            items.Add(new DropItem(path, Directory.Exists(path)));
        }

        var remote = DropCoordinateMapper.ToRemote(e.ClientPoint, _rdp.ClientSize, _desktopSize);
        var drop = new DropMessage(Guid.NewGuid().ToString("N"), remote.X, remote.Y, items);
        ClientLog.Info(Invariant(
            $"Drop {drop.Id}: {items.Count} item(s) at control ({e.ClientPoint.X}, {e.ClientPoint.Y}) of {_rdp.ClientSize.Width}x{_rdp.ClientSize.Height}, remote ({remote.X}, {remote.Y}) of {_desktopSize.Width}x{_desktopSize.Height}, model {_agent.Model}: {string.Join("; ", e.Files)}"));

        if (!_agent.SendDrop(drop))
        {
            ShowDropStatus("Drop failed: the message could not be sent to the agent.");
            return;
        }

        _pendingDropId = drop.Id;
        e.Effect = DragDropEffects.Copy;
        ShowDropStatus(Invariant($"Sending {items.Count} item(s)..."));
    }

    private void OnDropResult(DropResultMessage result)
    {
        if (_pendingDropId is not null && result.Id != _pendingDropId)
        {
            ClientLog.Info($"Result {result.Id} belongs to an earlier drop; the status bar keeps the latest one.");
            return;
        }

        _pendingDropId = null;
        ShowDropStatus(DescribeResult(result));
    }

    private static string DescribeResult(DropResultMessage result) => result.Status switch
    {
        DropStatus.Ok => !string.IsNullOrWhiteSpace(result.Message)
            ? result.Message
            : $"Copied to {result.TargetFolder}",
        DropStatus.Cancelled => !string.IsNullOrWhiteSpace(result.Message)
            ? $"Cancelled: {result.Message}"
            : "Copy cancelled.",
        _ => $"Drop failed: {(string.IsNullOrWhiteSpace(result.Message) ? "no detail from the agent." : result.Message)}",
    };

    private void ShowAbout()
    {
        MessageBox.Show(
            this,
            $"{AppInfo.Name} {AppInfo.Version}{Environment.NewLine}{Environment.NewLine}"
            + $"Drag files and folders from this computer and drop them into the Remote Desktop session.{Environment.NewLine}{Environment.NewLine}"
            + $"{AppInfo.ProjectUrl}",
            $"About {AppInfo.Name}",
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);
    }

    private static void OpenLogFolder()
    {
        try
        {
            Directory.CreateDirectory(ClientLog.Directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ClientLog.Error($"Cannot create {ClientLog.Directory}", ex);
            return;
        }

        OpenWithShell(ClientLog.Directory);
    }

    private static void OpenWithShell(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            ClientLog.Error($"Cannot open {target}", ex);
        }
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
