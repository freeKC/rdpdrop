using UltimateRDP.Client.Localization;
using UltimateRDP.Client.Resources;

namespace UltimateRDP.Client;

/// <summary>
/// The notification icon of a client started for a RemoteApp program with its main window
/// hidden, since 0.9.0. Its menu names each program and its server, then holds Show
/// UltimateRDP, Disconnect and Exit. It is the only thing of the client on the screen besides
/// the windows of the programs, and stays until the process exits.
/// </summary>
internal sealed class ClientTray : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly ContextMenuStrip _menu = new();
    private readonly Font _bold;
    private readonly Func<IReadOnlyList<(string Program, string Server)>> _sessions;
    private readonly Action _show;
    private readonly Action _disconnect;
    private readonly Action _exit;

    /// <param name="sessions">The program and server of each open tab.</param>
    /// <param name="show">Shows the main window.</param>
    /// <param name="disconnect">Disconnects every session.</param>
    /// <param name="exit">Disconnects every session and exits.</param>
    public ClientTray(Func<IReadOnlyList<(string Program, string Server)>> sessions, Action show, Action disconnect, Action exit)
    {
        _sessions = sessions;
        _show = show;
        _disconnect = disconnect;
        _exit = exit;
        _bold = new Font(_menu.Font, FontStyle.Bold);
        _menu.Opening += (_, _) => Rebuild();
        Rebuild();
        _icon = new NotifyIcon
        {
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Application,
            Text = AppInfo.Name,
            ContextMenuStrip = _menu,
            Visible = true,
        };
        _icon.DoubleClick += (_, _) => _show();
        UpdateText();
    }

    /// <summary>Writes the tooltip again: the programs that run, within the 127 characters Windows allows.</summary>
    public void UpdateText()
    {
        var sessions = _sessions();
        var text = sessions.Count == 0
            ? AppInfo.Name
            : AppInfo.Name + ": " + string.Join(", ", sessions.Select(session => session.Program));
        _icon.Text = text.Length <= 127 ? text : string.Concat(text.AsSpan(0, 124), "...");
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _menu.Dispose();
        _bold.Dispose();
    }

    private void Rebuild()
    {
        foreach (var old in _menu.Items.OfType<ToolStripItem>().ToList())
        {
            _menu.Items.Remove(old);
            old.Dispose();
        }

        var sessions = _sessions();
        if (sessions.Count == 0)
        {
            _menu.Items.Add(new ToolStripMenuItem(Strings.TrayNoSession) { Enabled = false });
        }

        foreach (var (program, server) in sessions)
        {
            _menu.Items.Add(new ToolStripMenuItem(LocalText.Of(nameof(Strings.TraySessionLine), program, server).ToString().Replace("&", "&&", StringComparison.Ordinal)) { Enabled = false });
        }

        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(new ToolStripMenuItem(Strings.TrayShowClient, null, (_, _) => _show()) { Font = _bold });
        _menu.Items.Add(new ToolStripMenuItem(Strings.TrayDisconnect, null, (_, _) => _disconnect()) { Enabled = sessions.Count > 0 });
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(new ToolStripMenuItem(Strings.TrayExit, null, (_, _) => _exit()));
    }
}
