using RdpDrop.Client.Logging;
using RdpDrop.Client.Sessions;

namespace RdpDrop.Client;

/// <summary>
/// File menu of the main form: open, recent connections, tabs, saved credentials and Quit.
/// </summary>
internal sealed partial class MainForm
{
    private void InitializeFileMenu()
    {
        _fileMenu.OpenClicked += (_, _) => OpenFromDialog();
        _fileMenu.RecentChosen += (_, path) => OpenRecent(path);
        _fileMenu.ClearRecentClicked += (_, _) => _recent.Clear();
        _fileMenu.ReopenClicked += (_, _) => ReopenActive();
        _fileMenu.CloseTabClicked += (_, _) => CloseActiveTab();
        _fileMenu.ForgetCredentialsClicked += (_, _) => ForgetSavedCredentials();
        _fileMenu.QuitClicked += (_, _) => Close();
        _fileMenu.Root.DropDownOpening += (_, _) => OnFileMenuOpening();
        _recent.Changed += (_, _) => _fileMenu.ShowRecent(_recent.Paths);
        _fileMenu.ShowRecent(_recent.Paths);
    }

    private void OnFileMenuOpening()
    {
        UpdateMenu();
        var session = ActiveSession;
        _fileMenu.ShowSavedCredentials(session?.Host, session?.FindSavedCredentials() is { } kinds && kinds != SavedCredentialKinds.None);
    }

    private void ForgetSavedCredentials()
    {
        if (ActiveSession is not { } session)
        {
            return;
        }

        var answer = MessageBox.Show(
            this,
            $"Forget the credentials that Windows saved for {session.Host}?{Environment.NewLine}{Environment.NewLine}"
            + "The next connection asks for them again. The open session is not affected.",
            AppInfo.Name,
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question,
            MessageBoxDefaultButton.Button2);
        if (answer == DialogResult.Yes)
        {
            ClientLog.Info($"Forgetting the saved credentials for {session.Host}.");
            session.ForgetSavedCredentials();
        }
    }

    /// <summary>
    /// Ctrl+O and Ctrl+W. While the session has the keyboard they belong to the remote
    /// computer, like every other key, and the menu is the way to reach the command.
    /// </summary>
    private bool ProcessFileShortcut(Keys keyData)
    {
        if (keyData is not ((Keys.Control | Keys.O) or (Keys.Control | Keys.W)))
        {
            return false;
        }

        if (ActiveSession?.HasSessionKeyboard == true)
        {
            return false;
        }

        ClientLog.Info($"Shortcut {(keyData == (Keys.Control | Keys.O) ? "Ctrl+O" : "Ctrl+W")}");
        if (keyData == (Keys.Control | Keys.O))
        {
            OpenFromDialog();
        }
        else
        {
            CloseActiveTab();
        }

        return true;
    }
}
