using System.Diagnostics;
using RdpDrop.Agent.Channel;
using RdpDrop.Agent.Drops;
using RdpDrop.Agent.Logging;
using RdpDrop.Agent.Settings;
using RdpDrop.Core.Targets;

namespace RdpDrop.Agent.Tray;

/// <summary>
/// Application context of the agent: the tray icon and its menu, the channel loop and
/// the session notifications. Lives on the UI thread.
/// </summary>
internal sealed class TrayContext : ApplicationContext
{
    private readonly FileLogger _logger;
    private readonly AgentSettings _settings;
    private readonly ChannelLoop _loop;
    private readonly SessionNotificationWindow _sessionWindow;
    private readonly TrayIconFactory _iconFactory;
    private readonly NotifyIcon _notifyIcon;
    private readonly ContextMenuStrip _menu;
    private readonly ToolStripMenuItem _desktopItem;
    private readonly ToolStripMenuItem _downloadsItem;
    private readonly ToolStripMenuItem _customItem;
    private readonly ToolStripMenuItem _stageItem;
    private readonly ToolStripMenuItem _fallbackCopyItem;
    private readonly StagingFolder _staging;
    private readonly NativeDropCoordinator _native;
    private readonly SynchronizationContext _uiContext;
    private readonly RegisteredWaitHandle? _exitWait;
    private bool _exiting;

    public TrayContext(FileLogger logger, AgentSettings settings, EventWaitHandle? exitEvent)
    {
        _logger = logger;
        _settings = settings;

        _desktopItem = new ToolStripMenuItem("Desktop", null, (_, _) => SetFallback(new FallbackFolder(FallbackKind.Desktop)));
        _downloadsItem = new ToolStripMenuItem("Downloads", null, (_, _) => SetFallback(new FallbackFolder(FallbackKind.Downloads)));
        _customItem = new ToolStripMenuItem("Choose…", null, (_, _) => ChooseCustomFolder());
        var fallbackMenu = new ToolStripMenuItem("Fallback folder");
        fallbackMenu.DropDownItems.AddRange([_desktopItem, _downloadsItem, _customItem]);

        _stageItem = new ToolStripMenuItem("Stage files in the session before the drop", null, (_, _) => ToggleStageFiles());
        _fallbackCopyItem = new ToolStripMenuItem("Copy to the fallback folder when nothing accepts the drop", null, (_, _) => ToggleFallbackCopy());

        _menu = new ContextMenuStrip();
        _menu.Items.AddRange(
        [
            fallbackMenu,
            _stageItem,
            _fallbackCopyItem,
            new ToolStripSeparator(),
            new ToolStripMenuItem("Open log folder", null, (_, _) => OpenLogFolder()),
            new ToolStripMenuItem("About", null, (_, _) => ShowAbout()),
            new ToolStripSeparator(),
            new ToolStripMenuItem("Exit", null, (_, _) => Exit("the Exit menu")),
        ]);
        _menu.Opening += (_, _) => UpdateFallbackChecks();
        UpdateFallbackChecks();

        _uiContext = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();

        _iconFactory = new TrayIconFactory(SystemInformation.SmallIconSize);
        _notifyIcon = new NotifyIcon
        {
            Icon = _iconFactory.Icon,
            Text = AgentInfo.ProductName,
            ContextMenuStrip = _menu,
            Visible = true,
        };

        using (var process = Process.GetCurrentProcess())
        {
            _logger.Info($"{AgentInfo.ProductName} {AgentInfo.Version} started in session {process.SessionId}.");
        }

        _logger.Info($"Settings: {settings}.");
        _staging = new StagingFolder(logger, AgentInfo.StagingDirectory);
        ThreadPool.QueueUserWorkItem(_ => _staging.DeleteOldFolders());

        var probe = new DriveRedirectionProbe(logger);
        var executor = new DropExecutor(logger, settings, probe, new CursorTargetResolver(logger));
        _loop = new ChannelLoop(logger);
        _native = new NativeDropCoordinator(logger, _loop.TrySend, settings, probe, executor, _staging);
        var handler = new AgentProtocolHandler(logger, _loop.TrySend, probe, executor, _native);
        _sessionWindow = new SessionNotificationWindow(_loop, logger);
        _loop.Start(handler.Handle);

        if (exitEvent is not null)
        {
            _exitWait = ThreadPool.RegisterWaitForSingleObject(
                exitEvent,
                (_, _) => _uiContext.Post(_ => Exit("a request from --uninstall"), null),
                null,
                Timeout.Infinite,
                executeOnlyOnce: true);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _exitWait?.Unregister(null);
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
            _menu.Dispose();
            _iconFactory.Dispose();
            _sessionWindow.Dispose();

            // The drag in progress ends first, while its result can still be sent.
            _native.Dispose();
            _loop.Dispose();
            _staging.Dispose();
        }

        base.Dispose(disposing);
    }

    private void Exit(string reason)
    {
        if (_exiting)
        {
            return;
        }

        _exiting = true;
        _logger.Info($"Exiting after {reason}.");
        _notifyIcon.Visible = false;
        ExitThread();
    }

    private void UpdateFallbackChecks()
    {
        var fallback = _settings.Fallback;
        _desktopItem.Checked = fallback.Kind == FallbackKind.Desktop;
        _downloadsItem.Checked = fallback.Kind == FallbackKind.Downloads;
        _customItem.Checked = fallback.Kind == FallbackKind.Custom;
        _customItem.Text = fallback.Kind == FallbackKind.Custom ? $"Choose… ({fallback.CustomPath})" : "Choose…";
        _stageItem.Checked = _settings.StageFiles;
        _fallbackCopyItem.Checked = _settings.FallbackCopy;
    }

    private void ToggleStageFiles()
    {
        _settings.SetStageFiles(!_settings.StageFiles);
        _logger.Info($"Stage files in the session before the drop: {_settings.StageFiles}.");
        UpdateFallbackChecks();
    }

    private void ToggleFallbackCopy()
    {
        _settings.SetFallbackCopy(!_settings.FallbackCopy);
        _logger.Info($"Copy to the fallback folder when nothing accepts the drop: {_settings.FallbackCopy}.");
        UpdateFallbackChecks();
    }

    private void SetFallback(FallbackFolder fallback)
    {
        _settings.SetFallback(fallback);
        _logger.Info($"Fallback folder set to {fallback.Kind}{(fallback.CustomPath is null ? string.Empty : " " + fallback.CustomPath)}.");
        UpdateFallbackChecks();
    }

    private void ChooseCustomFolder()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "Folder that receives drops made outside an Explorer window or the desktop",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true,
        };

        var current = _settings.Fallback;
        if (current.Kind == FallbackKind.Custom && Directory.Exists(current.CustomPath))
        {
            dialog.InitialDirectory = current.CustomPath;
        }

        if (dialog.ShowDialog() != DialogResult.OK)
        {
            return;
        }

        if (!TargetFolderPolicy.IsFileSystemPath(dialog.SelectedPath))
        {
            MessageBox.Show(
                "Choose a folder on a drive or a network share.",
                AgentInfo.ProductName,
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }

        SetFallback(new FallbackFolder(FallbackKind.Custom, dialog.SelectedPath));
    }

    private void OpenLogFolder()
    {
        try
        {
            Directory.CreateDirectory(_logger.Directory);
            Process.Start(new ProcessStartInfo { FileName = _logger.Directory, UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            _logger.Warn($"Cannot open the log folder. {ex.Message}");
            MessageBox.Show($"Cannot open {_logger.Directory}.\n\n{ex.Message}", AgentInfo.ProductName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void ShowAbout()
    {
        var openButton = new TaskDialogButton("Open the project page");
        var page = new TaskDialogPage
        {
            Caption = "About " + AgentInfo.ProductName,
            Heading = $"{AgentInfo.ProductName} {AgentInfo.Version}",
            Text = "Receives files dropped on the RdpDrop client window and drops them, through drive redirection, "
                + "on the window under the cursor in this session. When nothing there accepts them, it copies them to a folder.\n\n"
                + AgentInfo.ProjectUrl,
            Icon = TaskDialogIcon.Information,
            Buttons = { openButton, TaskDialogButton.Close },
        };

        if (TaskDialog.ShowDialog(page) == openButton)
        {
            try
            {
                Process.Start(new ProcessStartInfo { FileName = AgentInfo.ProjectUrl, UseShellExecute = true })?.Dispose();
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                _logger.Warn($"Cannot open {AgentInfo.ProjectUrl}. {ex.Message}");
            }
        }
    }
}
