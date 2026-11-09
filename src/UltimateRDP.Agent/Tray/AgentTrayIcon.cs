using System.Diagnostics;
using UltimateRDP.Agent.Localization;
using UltimateRDP.Agent.Logging;
using UltimateRDP.Agent.Resources;
using UltimateRDP.Agent.Settings;

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

    /// <param name="logger">The log.</param>
    /// <param name="settings">The settings changed from the menu.</param>
    /// <param name="exit">Runs when the user chooses Exit.</param>
    public AgentTrayIcon(FileLogger logger, AgentSettings settings, Action exit)
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
