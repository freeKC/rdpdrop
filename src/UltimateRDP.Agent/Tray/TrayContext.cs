using System.Diagnostics;
using UltimateRDP.Agent.Channel;
using UltimateRDP.Agent.Drops;
using UltimateRDP.Agent.Localization;
using UltimateRDP.Agent.Logging;
using UltimateRDP.Agent.Resources;
using UltimateRDP.Agent.Settings;

namespace UltimateRDP.Agent.Tray;

/// <summary>
/// Application context of the agent: the tray icon and its menu, the channel loop and
/// the session notifications. Lives on the UI thread. The menu and the dialogs follow the
/// Windows display language of the session.
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
    private readonly ToolStripMenuItem _stageItem;
    private readonly StagingFolder _staging;
    private readonly NativeDropCoordinator _native;
    private readonly SynchronizationContext _uiContext;
    private readonly RegisteredWaitHandle? _exitWait;
    private bool _exiting;

    public TrayContext(FileLogger logger, AgentSettings settings, EventWaitHandle? exitEvent)
    {
        _logger = logger;
        _settings = settings;

        _stageItem = new ToolStripMenuItem(Strings.TrayStageFiles, null, (_, _) => ToggleStageFiles());

        _menu = new ContextMenuStrip();
        _menu.Items.AddRange(
        [
            _stageItem,
            new ToolStripSeparator(),
            new ToolStripMenuItem(Strings.TrayOpenLogFolder, null, (_, _) => OpenLogFolder()),
            new ToolStripMenuItem(Strings.TrayAbout, null, (_, _) => ShowAbout()),
            new ToolStripSeparator(),
            new ToolStripMenuItem(Strings.TrayExit, null, (_, _) => Exit("the Exit menu")),
        ]);
        _menu.Opening += (_, _) => UpdateChecks();
        UpdateChecks();

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
        var language = new ClientLanguage();
        _loop = new ChannelLoop(logger);
        _native = new NativeDropCoordinator(logger, _loop.TrySend, settings, probe, _staging, language);
        var logoff = new SessionLogoff(logger, _loop.TrySend, language);
        var handler = new AgentProtocolHandler(logger, _loop.TrySend, probe, _native, logoff, language);
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

    private void UpdateChecks()
    {
        _stageItem.Checked = _settings.StageFiles;
    }

    private void ToggleStageFiles()
    {
        _settings.SetStageFiles(!_settings.StageFiles);
        _logger.Info($"Stage files in the session before the drop: {_settings.StageFiles}.");
        UpdateChecks();
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
            MessageBox.Show(
                LocalText.Of(nameof(Strings.CannotOpenLogFolder), _logger.Directory, ex.Message).ForUser(),
                AgentInfo.ProductName,
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
    }

    private void ShowAbout()
    {
        var openButton = new TaskDialogButton(Strings.AboutOpenProjectPage);
        var page = new TaskDialogPage
        {
            Caption = LocalText.Of(nameof(Strings.AboutCaption), AgentInfo.ProductName).ForUser(),
            Heading = $"{AgentInfo.ProductName} {AgentInfo.Version}",
            Text = LocalText.Of(nameof(Strings.AboutText), AgentInfo.ProjectUrl).ForUser(),

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
