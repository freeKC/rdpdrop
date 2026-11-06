using System.Diagnostics;
using RdpDrop.Client.Logging;
using RdpDrop.Client.Sessions;
using RdpDrop.Client.Settings;
using RdpDrop.Core.RdpFiles;

namespace RdpDrop.Client;

/// <summary>
/// The main window: menus, the session view and the status bar that shows it.
/// </summary>
internal sealed partial class MainForm : Form
{
    private readonly ClientSettings _settings;
    private readonly ViewMenu _viewMenu = new();
    private readonly SessionView _session;
    private readonly ToolStripMenuItem _connectItem = new("&Connect");
    private readonly ToolStripMenuItem _disconnectItem = new("&Disconnect");
    private readonly ToolStripMenuItem _findAgentItem = new("&Look for the agent again");
    private readonly ToolStripStatusLabel _connectionLabel = new() { Text = "Not connected" };
    private readonly ToolStripStatusLabel _agentLabel = new() { Text = "Agent: not connected" };
    private readonly ToolStripStatusLabel _dropLabel = new() { Spring = true, TextAlign = ContentAlignment.MiddleLeft };

    private bool _closeRequested;

    public MainForm(string rdpPath, RdpFile file)
    {
        _settings = ClientSettings.Load(ClientSettings.DefaultPath);
        InitializeViewMenu();

        StartPosition = FormStartPosition.CenterScreen;
        var area = Screen.PrimaryScreen?.WorkingArea.Size ?? new Size(1280, 800);
        Size = new Size(area.Width * 4 / 5, area.Height * 4 / 5);
        AllowDrop = true;

        var menu = BuildMenu();
        var status = new StatusStrip { ShowItemToolTips = true };
        _connectionLabel.BorderSides = ToolStripStatusLabelBorderSides.Right;
        _agentLabel.BorderSides = ToolStripStatusLabelBorderSides.Right;
        status.Items.AddRange([_connectionLabel, _agentLabel, _dropLabel]);

        _session = new SessionView(rdpPath, file, _settings);
        Controls.Add(_session);
        Controls.Add(status);
        Controls.Add(menu);
        MainMenuStrip = menu;

        _session.StateChanged += (_, _) => UpdateMenu();
        _session.ConnectionTextChanged += (_, _) => SetLabel(_connectionLabel, _session.ConnectionText);
        _session.AgentTextChanged += (_, _) => SetLabel(_agentLabel, _session.AgentText);
        _session.DropTextChanged += (_, _) => SetLabel(_dropLabel, _session.DropText);
        _session.CloseCompleted += (_, _) => OnSessionClosed();
        ShowSessionStatus();

        ClientLog.Info("Main form created.");
        UpdateMenu();
        Shown += (_, _) => _session.Connect();
    }

    /// <summary>The session the menus and shortcuts act on.</summary>
    private SessionView? ActiveSession => _session;

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        ClientLog.Info($"Form closing: reason {e.CloseReason}, close requested {_closeRequested}.");
        if (!_closeRequested && _session.State != SessionState.Disconnected)
        {
            // Disconnect cleanly first; the session reports when it is closed.
            _closeRequested = true;
            e.Cancel = true;
            ClientLog.Info("Window closing, disconnecting first.");
            _session.BeginClose();
            StartExitWatchdog();
            return;
        }

        base.OnFormClosing(e);
    }

    // The form is a drop target too, as a fallback: OLE reaches it when no window under
    // the cursor is registered. It answers through the active session, which refuses
    // points outside the session area.
    protected override void OnDragEnter(DragEventArgs drgevent)
    {
        drgevent.Effect = ActiveSession?.ProcessDragOver(drgevent.Data, new Point(drgevent.X, drgevent.Y), drgevent.AllowedEffect) ?? DragDropEffects.None;
    }

    protected override void OnDragOver(DragEventArgs drgevent)
    {
        drgevent.Effect = ActiveSession?.ProcessDragOver(drgevent.Data, new Point(drgevent.X, drgevent.Y), drgevent.AllowedEffect) ?? DragDropEffects.None;
    }

    protected override void OnDragDrop(DragEventArgs drgevent)
    {
        drgevent.Effect = ActiveSession?.ProcessDrop(drgevent.Data, new Point(drgevent.X, drgevent.Y), drgevent.AllowedEffect) ?? DragDropEffects.None;
    }

    protected override void OnDragLeave(EventArgs e)
    {
        ActiveSession?.ProcessDragLeave();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        ClientLog.Info("Form closed.");
        base.OnFormClosed(e);
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

    private void OnSessionClosed()
    {
        if (_closeRequested)
        {
            BeginInvoke(Close);
        }
    }

    private MenuStrip BuildMenu()
    {
        _connectItem.Click += (_, _) => ActiveSession?.Connect();
        _disconnectItem.Click += (_, _) => ActiveSession?.Disconnect();
        _findAgentItem.Click += (_, _) => ActiveSession?.LookForAgent();
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
        menu.Items.AddRange([session, _viewMenu.Root, help]);
        return menu;
    }

    /// <summary>Shows the texts and the title of the active session.</summary>
    private void ShowSessionStatus()
    {
        var session = ActiveSession;
        Text = session?.WindowTitle ?? AppInfo.Name;
        SetLabel(_connectionLabel, session?.ConnectionText ?? "No session");
        SetLabel(_agentLabel, session?.AgentText ?? string.Empty);
        SetLabel(_dropLabel, session?.DropText ?? string.Empty);
    }

    private void UpdateMenu()
    {
        var session = ActiveSession;
        _connectItem.Enabled = session is { State: SessionState.Disconnected, IsClosing: false };
        _disconnectItem.Enabled = session is not null && session.State != SessionState.Disconnected;
        _findAgentItem.Enabled = session?.CanLookForAgent == true;
        ShowViewChoices();
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
}
