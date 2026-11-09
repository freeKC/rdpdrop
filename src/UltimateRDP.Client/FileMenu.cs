using System.Globalization;
using UltimateRDP.Client.Localization;
using UltimateRDP.Client.Resources;

namespace UltimateRDP.Client;

/// <summary>
/// The File menu: open a .rdp file, recent connections, tabs, log off, saved credentials
/// and Quit. Like <see cref="ViewMenu"/>, it only shows the state it is given and raises
/// an event per choice; the form decides what a choice does.
/// </summary>
/// <remarks>
/// Ctrl+O and Ctrl+W are only shown here. The form handles them, so that they reach the
/// remote computer while the session has the keyboard.
/// </remarks>
internal sealed class FileMenu
{
    private readonly ToolStripMenuItem _openItem = new() { ShortcutKeyDisplayString = "Ctrl+O" };
    private readonly ToolStripMenuItem _recentItem = new();
    private readonly ToolStripMenuItem _clearRecentItem = new();
    private readonly ToolStripMenuItem _reopenItem = new();
    private readonly ToolStripMenuItem _closeTabItem = new() { ShortcutKeyDisplayString = "Ctrl+W" };
    private readonly ToolStripMenuItem _logOffItem = new();
    private readonly ToolStripMenuItem _forgetItem = new();
    private readonly ToolStripMenuItem _quitItem = new();
    private IReadOnlyList<string> _recentPaths = [];
    private string? _credentialsHost;

    public FileMenu()
    {
        _openItem.Click += (_, _) => OpenClicked?.Invoke(this, EventArgs.Empty);
        _clearRecentItem.Click += (_, _) => ClearRecentClicked?.Invoke(this, EventArgs.Empty);
        _reopenItem.Click += (_, _) => ReopenClicked?.Invoke(this, EventArgs.Empty);
        _closeTabItem.Click += (_, _) => CloseTabClicked?.Invoke(this, EventArgs.Empty);
        _logOffItem.Click += (_, _) => LogOffClicked?.Invoke(this, EventArgs.Empty);
        _forgetItem.Click += (_, _) => ForgetCredentialsClicked?.Invoke(this, EventArgs.Empty);
        _quitItem.Click += (_, _) => QuitClicked?.Invoke(this, EventArgs.Empty);

        Root.DropDownItems.AddRange(
        [
            _openItem,
            _recentItem,
            new ToolStripSeparator(),
            _reopenItem,
            _closeTabItem,
            new ToolStripSeparator(),
            _logOffItem,
            new ToolStripSeparator(),
            _forgetItem,
            new ToolStripSeparator(),
            _quitItem,
        ]);
        ApplyTexts();
    }

    public event EventHandler? OpenClicked;

    /// <summary>Raised with the full path of the recent file chosen.</summary>
    public event EventHandler<string>? RecentChosen;

    public event EventHandler? ClearRecentClicked;

    public event EventHandler? ReopenClicked;

    public event EventHandler? CloseTabClicked;

    public event EventHandler? LogOffClicked;

    public event EventHandler? ForgetCredentialsClicked;

    public event EventHandler? QuitClicked;

    public ToolStripMenuItem Root { get; } = new();

    /// <summary>Writes every entry in the current user interface language.</summary>
    public void ApplyTexts()
    {
        Root.Text = Strings.MenuFile;
        _openItem.Text = Strings.MenuOpenRdp;
        _recentItem.Text = Strings.MenuRecent;
        _clearRecentItem.Text = Strings.MenuClearRecent;
        _reopenItem.Text = Strings.MenuReopen;
        _closeTabItem.Text = Strings.MenuCloseTab;
        _logOffItem.Text = Strings.MenuLogOff;
        _quitItem.Text = Strings.MenuQuit;
        _forgetItem.ToolTipText = Strings.ForgetCredentialsTip;
        ShowRecent(_recentPaths);
        ShowSavedCredentials(_credentialsHost, _forgetItem.Enabled);
    }

    /// <summary>
    /// Lists <paramref name="paths"/>, most recent first, numbered 1 to 9 then 0, with
    /// "Clear recent" at the bottom.
    /// </summary>
    public void ShowRecent(IReadOnlyList<string> paths)
    {
        _recentPaths = [.. paths];
        var items = _recentItem.DropDownItems;
        foreach (var old in items.OfType<ToolStripItem>().Where(item => item != _clearRecentItem).ToList())
        {
            items.Remove(old);
            old.Dispose();
        }

        items.Clear();
        for (var i = 0; i < paths.Count; i++)
        {
            var path = paths[i];
            var number = ((i + 1) % 10).ToString(CultureInfo.InvariantCulture);
            var item = new ToolStripMenuItem($"&{number} {path.Replace("&", "&&", StringComparison.Ordinal)}") { ToolTipText = path };
            item.Click += (_, _) => RecentChosen?.Invoke(this, path);
            items.Add(item);
        }

        if (paths.Count == 0)
        {
            items.Add(new ToolStripMenuItem(Strings.MenuRecentNone) { Enabled = false });
        }

        items.Add(new ToolStripSeparator());
        items.Add(_clearRecentItem);
        _clearRecentItem.Enabled = paths.Count > 0;
    }

    /// <summary>
    /// Names the host of the active tab in the "Forget saved credentials" entry, enabled
    /// when Windows keeps a credential for it.
    /// </summary>
    public void ShowSavedCredentials(string? host, bool saved)
    {
        _credentialsHost = host;
        _forgetItem.Text = host is null
            ? Strings.MenuForgetCredentials
            : LocalText.Of(nameof(Strings.MenuForgetCredentialsFor), host.Replace("&", "&&", StringComparison.Ordinal)).ToString();
        _forgetItem.Enabled = host is not null && saved;
    }

    /// <summary>
    /// Enables the entries that need an active tab, and the log off entry when the
    /// session is connected and its agent is ready.
    /// </summary>
    public void ShowActiveSession(bool hasSession, bool canLogOff)
    {
        _reopenItem.Enabled = hasSession;
        _closeTabItem.Enabled = hasSession;
        _logOffItem.Enabled = canLogOff;
    }
}
