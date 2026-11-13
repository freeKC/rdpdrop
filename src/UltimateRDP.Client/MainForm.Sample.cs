using UltimateRDP.Client.Previews;

namespace UltimateRDP.Client;

/// <summary>
/// Development aid, since 0.10.0: the main window drawn into a bitmap for the images of the
/// README, with sample texts only and a tab whose content the caller gives, without a session,
/// a connection or a window on the screen.
/// </summary>
internal sealed partial class MainForm
{
    /// <summary>
    /// Draws the window at <paramref name="clientSize"/> with one tab holding
    /// <paramref name="content"/>, the status bar texts given, and the File menu open when
    /// <paramref name="openFileMenu"/>. The window is never shown.
    /// </summary>
    public Bitmap DrawSample(Control content, string tabTitle, string windowTitle, string connection, string agent, string drop, bool openFileMenu, Size clientSize)
    {
        ClientSize = clientSize;
        content.Dock = DockStyle.Fill;
        var page = new TabPage(tabTitle) { UseVisualStyleBackColor = true };
        page.Controls.Add(content);
        ShowTabs(true);
        _tabs.TabPages.Add(page);
        _connectionLabel.Text = connection;
        _agentLabel.Text = agent;
        _agentLabel.Visible = true;
        _dropLabel.Text = drop;
        _fileMenu.ShowActiveSession(hasSession: true, canLogOff: true);
        _fileMenu.ShowSavedCredentials(null, saved: false);
        PerformLayout();
        using var client = HiddenRender.DrawClient(this);
        if (openFileMenu && MainMenuStrip is { } menu)
        {
            using var dropDown = HiddenRender.DrawDropDown(_fileMenu.Root.DropDown);
            HiddenRender.Overlay(client, dropDown, new Point(menu.Left + _fileMenu.Root.Bounds.Left, menu.Bottom));
        }

        return HiddenRender.Frame(client, windowTitle, Font, DeviceDpi / 96f);
    }
}
