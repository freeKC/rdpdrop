using System.Diagnostics;
using UltimateRDP.Client.Localization;
using UltimateRDP.Client.Logging;
using UltimateRDP.Client.Resources;
using UltimateRDP.Client.Sessions;
using UltimateRDP.Client.Settings;

namespace UltimateRDP.Client;

/// <summary>
/// The main window: menus, one tab per session, and the status bar that follows the
/// active tab. Menus and shortcuts act on the active tab.
/// </summary>
/// <remarks>
/// Every text comes from the resources. <see cref="ApplyTexts"/> writes them all again
/// after a change of language.
/// </remarks>
internal sealed partial class MainForm : Form
{
    private readonly string? _initialPath;
    private readonly bool _initialAskCredentials;
    private readonly ClientSettings _settings;
    private readonly RecentConnections _recent;
    private readonly FileMenu _fileMenu = new();
    private readonly ViewMenu _viewMenu = new();
    private readonly TabControl _tabs = new() { Dock = DockStyle.Fill, ShowToolTips = true, Visible = false };
    private readonly Label _emptyLabel = new()
    {
        Dock = DockStyle.Fill,
        TextAlign = ContentAlignment.MiddleCenter,
    };

    private MenuClickAway? _menuClickAway;
    private readonly ToolStripMenuItem _sessionMenu = new();
    private readonly ToolStripMenuItem _connectItem = new();
    private readonly ToolStripMenuItem _disconnectItem = new();
    private readonly ToolStripMenuItem _findAgentItem = new();
    private readonly ToolStripMenuItem _cancelTransferItem = new();
    private readonly ToolStripMenuItem _helpMenu = new();
    private readonly ToolStripMenuItem _aboutItem = new();
    private readonly ToolStripMenuItem _projectItem = new();
    private readonly ToolStripMenuItem _logsItem = new();
    private readonly ToolStripStatusLabel _connectionLabel = new();
    private readonly ToolStripStatusLabel _agentLabel = new();
    private readonly ToolStripStatusLabel _dropLabel = new() { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
    private readonly Rectangle _initialBounds;

    private bool _closeRequested;
    private bool _closeScheduled;

    /// <summary>
    /// Creates the window. <paramref name="initialPath"/> is opened in the first tab once
    /// the window is shown; without it the window starts empty. <paramref name="settings"/>
    /// are the saved choices, already loaded to set the language. With
    /// <paramref name="startHidden"/> the window starts out of sight, with a notification
    /// icon; <paramref name="askCredentials"/> asks for the password in the first tab.
    /// </summary>
    public MainForm(string? initialPath, ClientSettings settings, bool startHidden = false, bool askCredentials = false, Loader.LoaderForm? startupLoader = null)
    {
        _startupLoader = startupLoader;
        _initialPath = initialPath;
        _initialAskCredentials = askCredentials;
        _settings = settings;
        _recent = new RecentConnections(_settings, ClientSettings.DefaultPath);
        InitializeFileMenu();
        InitializeViewMenu();

        SuspendLayout();
        Text = AppInfo.Name;
        AllowDrop = true;

        // Sizes are given at 96 DPI and the font is the default one: Windows Forms scales
        // both to the monitor the window opens on, and again on every DpiChanged when the
        // window goes to a monitor with another DPI.
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;

        var menu = BuildMenu();
        var status = new StatusStrip { ShowItemToolTips = true };
        _connectionLabel.BorderSides = ToolStripStatusLabelBorderSides.Right;
        _agentLabel.BorderSides = ToolStripStatusLabelBorderSides.Right;
        status.Items.AddRange([_connectionLabel, _agentLabel, _dropLabel]);

        Controls.Add(_tabs);
        Controls.Add(_emptyLabel);
        Controls.Add(status);
        ResumeLayout(false);
        PerformLayout();

        // After the first auto scaling: the menu bar already takes its padding from the
        // DPI when it is created, and would be scaled a second time.
        Controls.Add(menu);
        MainMenuStrip = menu;
        _menuClickAway = new MenuClickAway(menu);

        // In pixels of the monitor the window opens on, so after the auto scaling above;
        // set again in OnLoad.
        StartPosition = FormStartPosition.Manual;
        _initialBounds = InitialBounds();
        Bounds = _initialBounds;
        if (startHidden)
        {
            StartOutOfSight();
        }

        _tabs.SelectedIndexChanged += (_, _) => OnActiveTabChanged();

        ClientLog.Info("Main form created.");
        ApplyTexts();
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
            Title = Strings.OpenDialogTitle,
            Filter = Strings.OpenDialogFilter,
            CheckFileExists = true,
        };
        return dialog.ShowDialog(owner) == DialogResult.OK ? dialog.FileName : null;
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        var open = Sessions.Count(session => session.State != SessionState.Disconnected);
        ClientLog.Info($"Form closing: reason {e.CloseReason}, {open} session(s) not disconnected, close requested {_closeRequested}.");
        if (HideInsteadOfClosing(e))
        {
            return;
        }

        if (!_closeRequested && open > 0 && e.CloseReason == CloseReason.UserClosing && !ConfirmQuitWithRemotePrograms())
        {
            _exitRequested = false;
            e.Cancel = true;
            return;
        }

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

    /// <summary>
    /// A window created on a monitor whose DPI differs from the system DPI is scaled once
    /// more by Windows Forms. Its first bounds are already pixels of that
    /// monitor, so they are set again.
    /// </summary>
    protected override void OnLoad(EventArgs e)
    {
        if (!_offScreen && Bounds != _initialBounds)
        {
            ClientLog.Info(string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"Window created at {DeviceDpi} DPI; bounds {Bounds} set back to {_initialBounds}."));
            Bounds = _initialBounds;
        }

        base.OnLoad(e);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        ClientLog.Info("Form closed.");
        FinishLoaders("the window closed");

        _tray?.Dispose();
        _tray = null;
        _menuClickAway?.Dispose();
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

    /// <summary>
    /// Four fifths of the working area of the monitor under the mouse, centered on it,
    /// where CenterScreen would put the window, in pixels of that monitor.
    /// </summary>
    private static Rectangle InitialBounds()
    {
        var area = Screen.FromPoint(Cursor.Position).WorkingArea;
        var width = area.Width * 4 / 5;
        var height = area.Height * 4 / 5;
        return new Rectangle(area.X + ((area.Width - width) / 2), area.Y + ((area.Height - height) / 2), width, height);
    }

    private static SessionView? SessionOf(TabPage? page) =>
        page?.Controls.OfType<SessionView>().FirstOrDefault();

    private void OnShown()
    {
        PlaceLoaders();
        if (_initialPath is not null)
        {
            OpenSession(_initialPath, _initialAskCredentials);
        }

        ExitIfIdle();
    }

    /// <summary>
    /// Opens <paramref name="path"/> in a new tab and connects. A file that cannot be
    /// read is reported and taken out of the recent connections.
    /// </summary>
    private void OpenSession(string path, bool askCredentials = false)
    {
        if (_closeRequested)
        {
            return;
        }

        if (!RdpFileLoader.TryLoad(path, out var fullPath, out var file, out var error))
        {
            _recent.Remove(path);
            _startupLoader?.Finish("the .rdp file cannot be read");
            _startupLoader = null;
            MessageBox.Show(DialogOwner, error.ToString(), AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Error);
            ExitIfIdle();
            return;
        }

        _recent.Add(fullPath);
        var session = new SessionView(fullPath, file, _settings) { AskCredentialsFromCommandLine = askCredentials };
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

        AttachLoader(session);
        _tray?.UpdateText();

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

    /// <summary>
    /// Asks before Quit when RemoteApp programs have windows open: they keep running on the
    /// server. Returns true to go on.
    /// </summary>
    private bool ConfirmQuitWithRemotePrograms()
    {
        var programs = Sessions.Where(session => session.KeepsProgramOpenOnClose).Select(session => session.RemoteAppName).Distinct().ToList();
        if (programs.Count == 0)
        {
            return true;
        }

        var answer = MessageBox.Show(
            DialogOwner,
            LocalText.Of(nameof(Strings.RemoteAppQuitQuestion), string.Join(", ", programs)).ToString(),
            AppInfo.Name,
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question,
            MessageBoxDefaultButton.Button2);
        ClientLog.Info($"Quit with RemoteApp programs open ({string.Join(", ", programs)}): {(answer == DialogResult.Yes ? "confirmed" : "not confirmed")}.");
        return answer == DialogResult.Yes;
    }

    /// <summary>Closes the active tab; its session is disconnected first.</summary>
    private void CloseActiveTab()
    {
        if (ActiveSession is { } session)
        {
            if (session.KeepsProgramOpenOnClose)
            {
                var answer = MessageBox.Show(
                    this,
                    LocalText.Of(nameof(Strings.RemoteAppCloseQuestion), session.RemoteAppName ?? session.Host).ToString(),
                    AppInfo.Name,
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question,
                    MessageBoxDefaultButton.Button2);
                ClientLog.Info($"Close tab of the RemoteApp program {session.RemoteAppName} with windows open: {(answer == DialogResult.Yes ? "confirmed" : "not confirmed")}.");
                if (answer != DialogResult.Yes)
                {
                    return;
                }
            }

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
        FinishLoader(session, "the tab closed");
        _wasConnected.Remove(session);
        page.Dispose();
        ClientLog.Info($"Tab closed for {session.Host}, {_tabs.TabCount} tab(s) left.");
        if (_tabs.TabCount == 0)
        {
            ShowTabs(false);
        }

        OnActiveTabChanged();
        _tray?.UpdateText();
        ExitIfIdle();
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
        OnSessionStateForLoader(session);
        OnSessionStateForTray(session);
        if (session.Parent is TabPage page)
        {
            var text = session.State == SessionState.Disconnected
                ? LocalText.Of(nameof(Strings.TabDisconnected), session.Title).ToString()
                : session.Title;
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
        _cancelTransferItem.Click += (_, _) => ActiveSession?.CancelTransfer();

        _sessionMenu.DropDownItems.AddRange([_connectItem, _disconnectItem, _findAgentItem, new ToolStripSeparator(), _cancelTransferItem]);
        _sessionMenu.DropDownOpening += (_, _) => UpdateMenu();

        _aboutItem.Click += (_, _) => ShowAbout();
        _projectItem.Click += (_, _) => OpenWithShell(AppInfo.ProjectUrl);
        _logsItem.Click += (_, _) => OpenLogFolder();
        _helpMenu.DropDownItems.AddRange([_aboutItem, _projectItem, _logsItem]);

        var menu = new MenuStrip();
        menu.Items.AddRange([_fileMenu.Root, _sessionMenu, _viewMenu.Root, _helpMenu]);
        return menu;
    }

    /// <summary>
    /// Writes every text of the window in the current user interface language: menus,
    /// empty window hint, tab texts, status bar and the texts of each session. Called once
    /// at start and after each change of language.
    /// </summary>
    private void ApplyTexts()
    {
        _fileMenu.ApplyTexts();
        _viewMenu.ApplyTexts();
        _sessionMenu.Text = Strings.MenuSession;
        _connectItem.Text = Strings.MenuConnect;
        _disconnectItem.Text = Strings.MenuDisconnect;
        _findAgentItem.Text = Strings.MenuFindAgent;
        _cancelTransferItem.Text = Strings.MenuCancelTransfer;
        _helpMenu.Text = Strings.MenuHelp;
        _aboutItem.Text = Strings.MenuAbout;
        _projectItem.Text = Strings.MenuProjectPage;
        _logsItem.Text = Strings.MenuOpenLogFolder;
        _emptyLabel.Text = Strings.EmptyWindowHint;

        foreach (var session in Sessions)
        {
            session.ApplyTexts();
            OnSessionStateChanged(session);
        }

        OnActiveTabChanged();
    }

    /// <summary>Shows the texts and the title of the active session.</summary>
    private void ShowSessionStatus()
    {
        var session = ActiveSession;
        Text = session?.WindowTitle ?? AppInfo.Name;
        SetLabel(_connectionLabel, session?.ConnectionText ?? Strings.StatusNoSession);
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
        _cancelTransferItem.Enabled = session?.CanCancelTransfer == true;
        _fileMenu.ShowActiveSession(session is { IsClosing: false }, session?.CanLogOff == true);
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
        var publisher = LocalText.Of(nameof(Strings.AboutPublisher), AppInfo.Publisher).ToString();
        var page = new TaskDialogPage
        {
            Caption = LocalText.Of(nameof(Strings.AboutTitle), AppInfo.Name).ToString(),
            Text = LocalText.Of(nameof(Strings.AboutText), AppInfo.Name, AppInfo.Version, Link(AppInfo.ProjectUrl)).ToString(),
            Footnote = new TaskDialogFootnote($"{publisher}\n{Link(AppInfo.PublisherUrl)}"),
            Icon = TaskDialogIcon.Information,
            EnableLinks = true,
            Buttons = { TaskDialogButton.OK },
        };

        // Only the two addresses written above are links.
        page.LinkClicked += (_, e) =>
        {
            if (e.LinkHref is AppInfo.ProjectUrl or AppInfo.PublisherUrl)
            {
                OpenWithShell(e.LinkHref);
            }
        };

        TaskDialog.ShowDialog(this, page);
    }

    /// <summary>Task dialog markup that shows <paramref name="url"/> as a link to itself.</summary>
    private static string Link(string url) => $"<a href=\"{url}\">{url}</a>";

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
