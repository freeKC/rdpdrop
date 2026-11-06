using System.Globalization;

namespace RdpDrop.Client;

/// <summary>
/// The File menu: open a .rdp file, recent connections, tabs, saved credentials and
/// Quit. Like <see cref="ViewMenu"/>, it only shows the state it is given and raises an
/// event per choice; the form decides what a choice does.
/// </summary>
/// <remarks>
/// Ctrl+O and Ctrl+W are only shown here. The form handles them, so that they reach the
/// remote computer while the session has the keyboard.
/// </remarks>
internal sealed class FileMenu
{
    private readonly ToolStripMenuItem _openItem = new("&Open .rdp...") { ShortcutKeyDisplayString = "Ctrl+O" };
    private readonly ToolStripMenuItem _recentItem = new("&Recent");
    private readonly ToolStripMenuItem _clearRecentItem = new("&Clear recent");
    private readonly ToolStripMenuItem _reopenItem = new("Reopen in &new tab");
    private readonly ToolStripMenuItem _closeTabItem = new("&Close tab") { ShortcutKeyDisplayString = "Ctrl+W" };
    private readonly ToolStripMenuItem _forgetItem = new("&Forget saved credentials")
    {
        ToolTipText = "RdpDrop never saves credentials. Windows saves them when you tick \"Remember me\" in its credential prompt.",
    };

    private readonly ToolStripMenuItem _quitItem = new("&Quit");

    public FileMenu()
    {
        _openItem.Click += (_, _) => OpenClicked?.Invoke(this, EventArgs.Empty);
        _clearRecentItem.Click += (_, _) => ClearRecentClicked?.Invoke(this, EventArgs.Empty);
        _reopenItem.Click += (_, _) => ReopenClicked?.Invoke(this, EventArgs.Empty);
        _closeTabItem.Click += (_, _) => CloseTabClicked?.Invoke(this, EventArgs.Empty);
        _forgetItem.Click += (_, _) => ForgetCredentialsClicked?.Invoke(this, EventArgs.Empty);
        _quitItem.Click += (_, _) => QuitClicked?.Invoke(this, EventArgs.Empty);

        ShowRecent([]);
        Root.DropDownItems.AddRange(
        [
            _openItem,
            _recentItem,
            new ToolStripSeparator(),
            _reopenItem,
            _closeTabItem,
            new ToolStripSeparator(),
            _forgetItem,
            new ToolStripSeparator(),
            _quitItem,
        ]);
    }

    public event EventHandler? OpenClicked;

    /// <summary>Raised with the full path of the recent file chosen.</summary>
    public event EventHandler<string>? RecentChosen;

    public event EventHandler? ClearRecentClicked;

    public event EventHandler? ReopenClicked;

    public event EventHandler? CloseTabClicked;

    public event EventHandler? ForgetCredentialsClicked;

    public event EventHandler? QuitClicked;

    public ToolStripMenuItem Root { get; } = new("&File");

    /// <summary>
    /// Lists <paramref name="paths"/>, most recent first, numbered 1 to 9 then 0, with
    /// "Clear recent" at the bottom.
    /// </summary>
    public void ShowRecent(IReadOnlyList<string> paths)
    {
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
            items.Add(new ToolStripMenuItem("(none)") { Enabled = false });
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
        _forgetItem.Text = host is null
            ? "&Forget saved credentials"
            : $"&Forget saved credentials for {host.Replace("&", "&&", StringComparison.Ordinal)}";
        _forgetItem.Enabled = host is not null && saved;
    }

    /// <summary>Enables the entries that need an active tab.</summary>
    public void ShowActiveSession(bool hasSession)
    {
        _reopenItem.Enabled = hasSession;
        _closeTabItem.Enabled = hasSession;
    }
}
