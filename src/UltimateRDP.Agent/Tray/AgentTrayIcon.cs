using System.Diagnostics;
using UltimateRDP.Agent.Localization;
using UltimateRDP.Agent.Logging;
using UltimateRDP.Agent.Resources;
using UltimateRDP.Agent.Settings;
using UltimateRDP.Core.Agent;

namespace UltimateRDP.Agent.Tray;

/// <summary>
/// The icon of the agent in the notification area, with its menu. Created on the UI
/// thread the first time it must be shown, and kept afterwards: hiding it only removes it
/// from the notification area. The menu and the dialogs follow the Windows display
/// language of the session.
/// </summary>
internal sealed class AgentTrayIcon : IDisposable
{
    private readonly FileLogger _logger;
    private readonly AgentSettings _settings;
    private readonly TrayIconFactory _iconFactory;
    private readonly NotifyIcon _notifyIcon;
    private readonly ContextMenuStrip _menu;
    private readonly ToolStripMenuItem _stageItem;
    private readonly ToolStripMenuItem _fastTransferItem;
    private readonly (ToolStripMenuItem Item, FastTransferMode Mode)[] _fastTransferModes;
    private readonly ToolStripMenuItem _directTransferItem;
    private readonly (ToolStripMenuItem Item, DirectTransferMode Mode)[] _directTransferModes;
    private readonly ToolStripMenuItem _mapDrivesItem;
    private readonly (ToolStripMenuItem Item, MapClientDrivesMode Mode)[] _mapDrivesModes;
    private readonly Action<MapClientDrivesMode> _mapDrivesChanged;
    private readonly ToolStripMenuItem _sendItem;

    /// <param name="logger">The log.</param>
    /// <param name="settings">The settings changed from the menu.</param>
    /// <param name="exit">Runs when the user chooses Exit.</param>
    /// <param name="mapDrivesChanged">Runs when the user changes "Client drives on drive letters".</param>
    /// <param name="sendFiles">Runs when the user chooses "Send files to my PC...".</param>
    public AgentTrayIcon(FileLogger logger, AgentSettings settings, Action exit, Action<MapClientDrivesMode> mapDrivesChanged, Action sendFiles)
    {
        _logger = logger;
        _settings = settings;
        _mapDrivesChanged = mapDrivesChanged;
        _sendItem = new ToolStripMenuItem(Strings.TraySendFiles, null, (_, _) => sendFiles()) { Enabled = false };

        _stageItem = new ToolStripMenuItem(Strings.TrayStageFiles, null, (_, _) => ToggleStageFiles());
        _fastTransferModes =
        [
            (new ToolStripMenuItem(Strings.TrayFastTransferAuto), FastTransferMode.Auto),
            (new ToolStripMenuItem(Strings.TrayFastTransferAlways), FastTransferMode.Always),
            (new ToolStripMenuItem(Strings.TrayFastTransferNever), FastTransferMode.Never),
        ];
        _fastTransferItem = new ToolStripMenuItem(Strings.TrayFastTransfer);
        foreach (var (item, mode) in _fastTransferModes)
        {
            item.Click += (_, _) => SetFastTransfer(mode);
            _fastTransferItem.DropDownItems.Add(item);
        }

        _directTransferModes =
        [
            (new ToolStripMenuItem(Strings.TrayFastTransferAuto), DirectTransferMode.Auto),
            (new ToolStripMenuItem(Strings.TrayFastTransferNever), DirectTransferMode.Never),
        ];
        _directTransferItem = new ToolStripMenuItem(Strings.TrayDirectTransfer);
        foreach (var (item, mode) in _directTransferModes)
        {
            item.Click += (_, _) => SetDirectTransfer(mode);
            _directTransferItem.DropDownItems.Add(item);
        }

        _mapDrivesModes =
        [
            (new ToolStripMenuItem(Strings.TrayFastTransferAuto), MapClientDrivesMode.Auto),
            (new ToolStripMenuItem(Strings.TrayFastTransferNever), MapClientDrivesMode.Never),
        ];
        _mapDrivesItem = new ToolStripMenuItem(Strings.TrayMapClientDrives);
        foreach (var (item, mode) in _mapDrivesModes)
        {
            item.Click += (_, _) => SetMapClientDrives(mode);
            _mapDrivesItem.DropDownItems.Add(item);
        }

        _menu = new ContextMenuStrip();
        _menu.Items.AddRange(
        [
            _sendItem,
            new ToolStripSeparator(),
            _stageItem,
            _fastTransferItem,
            _directTransferItem,
            _mapDrivesItem,
            new ToolStripSeparator(),
            new ToolStripMenuItem(Strings.TrayOpenLogFolder, null, (_, _) => OpenLogFolder()),
            new ToolStripMenuItem(Strings.TrayAbout, null, (_, _) => ShowAbout()),
            new ToolStripSeparator(),
            new ToolStripMenuItem(Strings.TrayExit, null, (_, _) => exit()),
        ]);
        _menu.Opening += (_, _) => UpdateChecks();
        UpdateChecks();

        _iconFactory = new TrayIconFactory(SystemInformation.SmallIconSize);
        _notifyIcon = new NotifyIcon
        {
            Icon = _iconFactory.Icon,
            Text = AgentInfo.ProductName,
            ContextMenuStrip = _menu,
        };
    }

    /// <summary>Shows or hides the icon.</summary>
    public bool Visible
    {
        get => _notifyIcon.Visible;
        set => _notifyIcon.Visible = value;
    }

    /// <summary>Enables "Send files to my PC..." while a client that receives files is connected.</summary>
    public void SetCanSend(bool canSend) => _sendItem.Enabled = canSend;

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _menu.Dispose();
        _iconFactory.Dispose();
    }

    private void UpdateChecks()
    {
        _stageItem.Checked = _settings.StageFiles;
        foreach (var (item, mode) in _fastTransferModes)
        {
            item.Checked = _settings.FastTransfer == mode;
        }

        foreach (var (item, mode) in _directTransferModes)
        {
            item.Checked = _settings.DirectTransfer == mode;
        }

        foreach (var (item, mode) in _mapDrivesModes)
        {
            item.Checked = _settings.MapClientDrives == mode;
        }
    }

    private void SetMapClientDrives(MapClientDrivesMode mode)
    {
        _settings.SetMapClientDrives(mode);
        _logger.Info($"Client drives on drive letters: {ClientDriveLetters.ToSetting(mode)}.");
        UpdateChecks();
        _mapDrivesChanged(mode);
    }

    private void SetDirectTransfer(DirectTransferMode mode)
    {
        _settings.SetDirectTransfer(mode);
        _logger.Info($"Direct connection for large files: {DirectTransferPolicy.ToSetting(mode)}.");
        UpdateChecks();
    }

    private void SetFastTransfer(FastTransferMode mode)
    {
        _settings.SetFastTransfer(mode);
        _logger.Info($"Fast transfer of large files: {FastTransferPolicy.ToSetting(mode)}.");
        UpdateChecks();
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
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.ComponentModel.Win32Exception)
        {
            // The user asked for the folder from the menu: the answer is shown to them.
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
            Text = LocalText.Of(nameof(Strings.AboutText), Link(AgentInfo.ProjectUrl)).ForUser(),
            Footnote = new TaskDialogFootnote(
                $"{LocalText.Of(nameof(Strings.AboutPublisher), AgentInfo.Publisher).ForUser()}\n{Link(AgentInfo.PublisherUrl)}"),
            Icon = TaskDialogIcon.Information,
            EnableLinks = true,
            Buttons = { openButton, TaskDialogButton.Close },
        };

        // Only the two addresses written above are links.
        page.LinkClicked += (_, e) =>
        {
            if (e.LinkHref is AgentInfo.ProjectUrl or AgentInfo.PublisherUrl)
            {
                OpenUrl(e.LinkHref);
            }
        };

        if (TaskDialog.ShowDialog(page) == openButton)
        {
            OpenUrl(AgentInfo.ProjectUrl);
        }
    }

    private void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true })?.Dispose();
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            _logger.Warn($"Cannot open {url}. {ex.Message}");
        }
    }

    /// <summary>Task dialog markup that shows <paramref name="url"/> as a link to itself.</summary>
    private static string Link(string url) => $"<a href=\"{url}\">{url}</a>";
}
