using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using AxMSTSCLib;
using RdpDrop.Client.Logging;
using RdpDrop.Client.Rdp;
using RdpDrop.Core.RdpFiles;

namespace RdpDrop.Client;

internal sealed class MainForm : Form
{
    private readonly string _rdpPath;
    private readonly RdpFile _file;
    private readonly string _host;
    private readonly RdpControl _rdp = new();
    private readonly ChildDropTargets _childTargets;
    private readonly ToolStripMenuItem _connectItem = new("&Connect");
    private readonly ToolStripMenuItem _disconnectItem = new("&Disconnect");
    private readonly ToolStripStatusLabel _connectionLabel = new() { Text = "Not connected" };
    private readonly ToolStripStatusLabel _agentLabel = new() { Text = "Agent: not connected" };
    private readonly ToolStripStatusLabel _dropLabel = new() { Spring = true, TextAlign = ContentAlignment.MiddleLeft };

    private ConnectionState _state = ConnectionState.Disconnected;
    private bool _channelCreated;
    private bool _closeRequested;
    private Size _desktopSize;

    public MainForm(string rdpPath, RdpFile file)
    {
        _rdpPath = rdpPath;
        _file = file;
        _host = RdpAddress.Split(file.FullAddress ?? string.Empty).Host;
        _childTargets = new ChildDropTargets(_rdp);

        Text = $"{AppInfo.Name}: {file.FullAddress}";
        StartPosition = FormStartPosition.CenterScreen;
        var area = Screen.PrimaryScreen?.WorkingArea.Size ?? new Size(1280, 800);
        Size = new Size(area.Width * 4 / 5, area.Height * 4 / 5);
        AllowDrop = true;

        var menu = BuildMenu();
        var status = new StatusStrip();
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
            return;
        }

        base.OnFormClosing(e);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _childTargets.RevokeAll();
        base.OnFormClosed(e);
    }

    private MenuStrip BuildMenu()
    {
        _connectItem.Click += (_, _) => Connect();
        _disconnectItem.Click += (_, _) => Disconnect();
        var quit = new ToolStripMenuItem("&Quit");
        quit.Click += (_, _) => Close();

        var session = new ToolStripMenuItem("&Session");
        session.DropDownItems.AddRange([_connectItem, _disconnectItem, new ToolStripSeparator(), quit]);

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
        catch (Exception ex) when (ex is COMException or InvalidOperationException or AxHost.InvalidActiveXStateException)
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
            _connectionLabel.Text = "Disconnecting...";
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
    }

    private void OnRdpLoginComplete()
    {
        ClientLog.Info("Event OnLoginComplete");
        SetConnection(ConnectionState.Connected, $"Logged on to {_host}");
        _childTargets.Refresh("logged on");
    }

    private void OnRdpDisconnected(int reason)
    {
        var extended = SafeExtendedReason();
        var text = SafeErrorDescription(reason, extended);
        ClientLog.Info(Invariant($"Event OnDisconnected reason={reason} extended={extended} text=\"{text}\""));
        SetConnection(ConnectionState.Disconnected, string.IsNullOrWhiteSpace(text) ? Invariant($"Disconnected (code {reason})") : $"Disconnected: {text}");

        if (_closeRequested)
        {
            BeginInvoke(Close);
        }
    }

    private void OnRdpFatalError(int errorCode)
    {
        ClientLog.Error(Invariant($"Event OnFatalError code={errorCode}"));
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
        _connectionLabel.Text = Invariant($"Reconnecting to {_host} (attempt {attempt})...");
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
        _connectionLabel.Text = text;
        UpdateMenu();
    }

    private void UpdateMenu()
    {
        _connectItem.Enabled = _state == ConnectionState.Disconnected;
        _disconnectItem.Enabled = _state != ConnectionState.Disconnected;
    }

    private void ShowDropStatus(string text) => _dropLabel.Text = text;

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
        Directory.CreateDirectory(ClientLog.Directory);
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
