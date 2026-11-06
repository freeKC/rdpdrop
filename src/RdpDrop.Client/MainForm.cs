using System.Diagnostics;
using RdpDrop.Client.Logging;
using RdpDrop.Client.Sessions;
using RdpDrop.Client.Settings;

namespace RdpDrop.Client;

/// <summary>
/// The main window: menus, one tab per session, and the status bar that follows the
/// active tab. Menus and shortcuts act on the active tab.
/// </summary>
internal sealed partial class MainForm : Form
{
    private const string DisconnectedMarker = " (disconnected)";

    private readonly string? _initialPath;
    private readonly ClientSettings _settings;
    private readonly RecentConnections _recent;
    private readonly FileMenu _fileMenu = new();
    private readonly ViewMenu _viewMenu = new();
    private readonly TabControl _tabs = new() { Dock = DockStyle.Fill, ShowToolTips = true, Visible = false };
    private readonly Label _emptyLabel = new()
    {
        Dock = DockStyle.Fill,
        TextAlign = ContentAlignment.MiddleCenter,
        Text = "No session is open. Use File, Open .rdp or File, Recent to start one.",
    };

    private readonly ToolStripMenuItem _connectItem = new("&Connect");
    private readonly ToolStripMenuItem _disconnectItem = new("&Disconnect");
    private readonly ToolStripMenuItem _findAgentItem = new("&Look for the agent again");
    private readonly ToolStripStatusLabel _connectionLabel = new();
    private readonly ToolStripStatusLabel _agentLabel = new();
    private readonly ToolStripStatusLabel _dropLabel = new() { Spring = true, TextAlign = ContentAlignment.MiddleLeft };

    private bool _closeRequested;
    private bool _closeScheduled;

    /// <summary>
    /// Creates the window. <paramref name="initialPath"/> is opened in the first tab once
    /// the window is shown; without it the window starts empty.
    /// </summary>
    public MainForm(string? initialPath)
    {
        _initialPath = initialPath;
        _settings = ClientSettings.Load(ClientSettings.DefaultPath);
        _recent = new RecentConnections(_settings, ClientSettings.DefaultPath);
        InitializeFileMenu();
        InitializeViewMenu();

        Text = AppInfo.Name;
        StartPosition = FormStartPosition.CenterScreen;
        var area = Screen.PrimaryScreen?.WorkingArea.Size ?? new Size(1280, 800);
        Size = new Size(area.Width * 4 / 5, area.Height * 4 / 5);
        AllowDrop = true;

        var menu = BuildMenu();
        var status = new StatusStrip { ShowItemToolTips = true };
        _connectionLabel.BorderSides = ToolStripStatusLabelBorderSides.Right;
        _agentLabel.BorderSides = ToolStripStatusLabelBorderSides.Right;
        status.Items.AddRange([_connectionLabel, _agentLabel, _dropLabel]);

        Controls.Add(_tabs);
        Controls.Add(_emptyLabel);
        Controls.Add(status);
        Controls.Add(menu);
        MainMenuStrip = menu;
        _tabs.SelectedIndexChanged += (_, _) => OnActiveTabChanged();

        ClientLog.Info("Main form created.");
        OnActiveTabChanged();
        Shown += (_, _) => OnShown();
    }

    /// <summary>The session the menus and shortcuts act on, or null when no tab is open.</summary>
    private SessionView? ActiveSession => SessionOf(_tabs.SelectedTab);

    private IEnumerable<SessionView> Sessions =>
        _tabs.TabPages.Cast<TabPage>().Select(SessionOf).OfType<SessionView>();

    /// <summary>
    /// Shows an open dialog for a .rdp file and returns the chosen path, or null.
    /// </summary>
    public static string? AskForRdpFile(IWin32Window? owner)
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Open a Remote Desktop file",
            Filter = "Remote Desktop files (*.rdp)|*.rdp",
            CheckFileExists = true,
        };
        return dialog.ShowDialog(owner) == DialogResult.OK ? dialog.FileName : null;
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        var open = Sessions.Count(session => session.State != SessionState.Disconnected);
        ClientLog.Info($"Form closing: reason {e.CloseReason}, {open} session(s) not disconnected, close requested {_closeRequested}.");
        if (!_closeRequested && open > 0)
        {
            // Disconnect every session cleanly first; each one reports when it is closed.
            _closeRequested = true;
            e.Cancel = true;
            ClientLog.Info("Window closing, disconnecting first.");
            foreach (var session in Sessions.ToList())
            {
                session.BeginClose();
            }

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
        drgevent.Effect = ActiveSession?.ProcessDragOver(drgevent.Data, new Point(drgevent.X, drgevent.Y), drgevent.AllowedEffect, drgevent.KeyState) ?? DragDropEffects.None;
    }

    protected override void OnDragOver(DragEventArgs drgevent)
    {
        drgevent.Effect = ActiveSession?.ProcessDragOver(drgevent.Data, new Point(drgevent.X, drgevent.Y), drgevent.AllowedEffect, drgevent.KeyState) ?? DragDropEffects.None;
    }

    protected override void OnDragDrop(DragEventArgs drgevent)
    {
        drgevent.Effect = ActiveSession?.ProcessDrop(drgevent.Data, new Point(drgevent.X, drgevent.Y), drgevent.AllowedEffect, drgevent.KeyState) ?? DragDropEffects.None;
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

    private static SessionView? SessionOf(TabPage? page) =>
        page?.Controls.OfType<SessionView>().FirstOrDefault();

    private void OnShown()
    {
        if (_initialPath is not null)
        {
            OpenSession(_initialPath);
        }
    }

    /// <summary>
    /// Opens <paramref name="path"/> in a new tab and connects. A file that cannot be
    /// read is reported and taken out of the recent connections.
    /// </summary>
    private void OpenSession(string path)
    {
        if (_closeRequested)
        {
            return;
        }

        if (!RdpFileLoader.TryLoad(path, out var fullPath, out var file, out var error))
        {
            _recent.Remove(path);
            MessageBox.Show(this, error, AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        _recent.Add(fullPath);
        var session = new SessionView(fullPath, file, _settings);
        var page = new TabPage(session.Title) { ToolTipText = fullPath, UseVisualStyleBackColor = true };
        page.Controls.Add(session);
        session.StateChanged += (_, _) => OnSessionStateChanged(session);
        session.ConnectionTextChanged += (_, _) => OnSessionTextChanged(session, _connectionLabel, session.ConnectionText);
        session.AgentTextChanged += (_, _) => OnSessionTextChanged(session, _agentLabel, session.AgentText);
        session.DropTextChanged += (_, _) => OnSessionTextChanged(session, _dropLabel, session.DropText);
        session.CloseCompleted += (_, _) => OnSessionClosed(session);

        ShowTabs(true);
        _tabs.TabPages.Add(page);
        _tabs.SelectedTab = page;

        // The first page added is selected without a SelectedIndexChanged event.
        OnActiveTabChanged();
        OnSessionStateChanged(session);
        ClientLog.Info($"Tab opened for {session.Host}, {_tabs.TabCount} tab(s).");

        // Connect once the tab has its final size: the desktop size comes from it.
        BeginInvoke(session.Connect);
    }

    private void OpenFromDialog()
    {
        var path = AskForRdpFile(this);
        if (path is not null)
        {
            OpenSession(path);
        }
    }

    private void OpenRecent(string path)
    {
        ClientLog.Info($"Opening recent connection {path}");
        OpenSession(path);
    }

    private void ReopenActive()
    {
        if (ActiveSession is { } session)
        {
            OpenSession(session.RdpPath);
        }
    }

    /// <summary>Closes the active tab; its session is disconnected first.</summary>
    private void CloseActiveTab()
    {
        if (ActiveSession is { } session)
        {
            ClientLog.Info($"Close tab requested for {session.Host}.");
            session.BeginClose();
            UpdateMenu();
        }
    }

    private void OnSessionClosed(SessionView session)
    {
        if (_closeRequested)
        {
            if (!_closeScheduled && Sessions.All(s => s.State == SessionState.Disconnected))
            {
                _closeScheduled = true;
                BeginInvoke(Close);
            }

            return;
        }

        // Not from inside the event of the view that is about to be disposed.
        BeginInvoke(() => RemoveTab(session));
    }

    private void RemoveTab(SessionView session)
    {
        if (session.Parent is not TabPage page || _closeRequested)
        {
            return;
        }

        _tabs.TabPages.Remove(page);
        page.Dispose();
        ClientLog.Info($"Tab closed for {session.Host}, {_tabs.TabCount} tab(s) left.");
        if (_tabs.TabCount == 0)
        {
            ShowTabs(false);
        }

        OnActiveTabChanged();
    }

    /// <summary>The tab control is hidden while no session is open, so the empty window shows a hint.</summary>
    private void ShowTabs(bool visible)
    {
        _tabs.Visible = visible;
        _emptyLabel.Visible = !visible;
    }

    private void OnActiveTabChanged()
    {
        ShowSessionStatus();
        UpdateMenu();
    }

    private void OnSessionStateChanged(SessionView session)
    {
        if (session.Parent is TabPage page)
        {
            var text = session.State == SessionState.Disconnected ? session.Title + DisconnectedMarker : session.Title;
            if (page.Text != text)
            {
                page.Text = text;
            }
        }

        if (session == ActiveSession)
        {
            UpdateMenu();
        }
    }

    private void OnSessionTextChanged(SessionView session, ToolStripStatusLabel label, string text)
    {
        if (session == ActiveSession)
        {
            SetLabel(label, text);
        }
    }

    private MenuStrip BuildMenu()
    {
        _connectItem.Click += (_, _) => ActiveSession?.Connect();
        _disconnectItem.Click += (_, _) => ActiveSession?.Disconnect();
        _findAgentItem.Click += (_, _) => ActiveSession?.LookForAgent();

        var session = new ToolStripMenuItem("&Session");
        session.DropDownItems.AddRange([_connectItem, _disconnectItem, _findAgentItem]);
        session.DropDownOpening += (_, _) => UpdateMenu();

        var about = new ToolStripMenuItem("&About RdpDrop");
        about.Click += (_, _) => ShowAbout();
        var project = new ToolStripMenuItem("&Project page");
        project.Click += (_, _) => OpenWithShell(AppInfo.ProjectUrl);
        var logs = new ToolStripMenuItem("Open &log folder");
        logs.Click += (_, _) => OpenLogFolder();

        var help = new ToolStripMenuItem("&Help");
        help.DropDownItems.AddRange([about, project, logs]);

        var menu = new MenuStrip();
        menu.Items.AddRange([_fileMenu.Root, session, _viewMenu.Root, help]);
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
        _agentLabel.Visible = session is not null;
    }

    private void UpdateMenu()
    {
        var session = ActiveSession;
        _connectItem.Enabled = session is { State: SessionState.Disconnected, IsClosing: false };
        _disconnectItem.Enabled = session is { IsClosing: false } && session.State != SessionState.Disconnected;
        _findAgentItem.Enabled = session?.CanLookForAgent == true;
        _fileMenu.ShowActiveSession(session is { IsClosing: false });
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
            + $"Saved credentials: RdpDrop never saves a password. Windows saves it when you tick \"Remember me\" in its credential prompt. "
            + $"Before connecting, RdpDrop uses the generic credential TERMSRV/<host> of the Windows Credential Manager when there is one.{Environment.NewLine}{Environment.NewLine}"
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
